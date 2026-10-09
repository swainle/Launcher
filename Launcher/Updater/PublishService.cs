using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using static Launcher.Updater.UpdateService;

namespace Launcher.Updater;

public sealed class PublishService
{
    private readonly HttpClient _client;
    private readonly VersionService _versions;

    public PublishService(HttpClient client, VersionService versions)
    {
        this._client = client;
        this._versions = versions;
    }

    public async Task<PublishResult> PublishVersionAsync(
        string releasesDirectory,
        string version,
        string osName,
        string arch,
        string releaseEndpoint,
        string cosBucketURL,
        string secretID,
        string secretKey,
        string sessionToken,
        bool showHistory,
        CancellationToken ct,
        bool publishUpgrade = true,
        bool publishFull = false,
        string indexTitle = "Flori Starter: downloads")
    {
        var endpoint = ValidateEndpoint(releaseEndpoint);
        if (!ValidVersion(version) || !PlatformPart().IsMatch(osName) || !PlatformPart().IsMatch(arch) ||
            !ValidHttps(cosBucketURL) ||
            secretID.Length == 0 || secretKey.Length == 0)
        {
            throw new ArgumentException("invalid version, COS endpoint, or credentials");
        }

        if (!publishUpgrade && !publishFull)
        {
            throw new ArgumentException("select at least one package type");
        }

        var versionDirectory = Path.Combine(releasesDirectory, version);
        var manifestPath = Path.Combine(versionDirectory, "manifest.json");
        var manifest = VersionService.ReadVersionManifest(manifestPath);
        VersionPackage? selectedPackage = null;
        foreach (var candidate in manifest.Packages)
        {
            if (candidate.OS == osName && candidate.Arch == arch)
            {
                selectedPackage = candidate;
                break;
            }
        }

        if (publishUpgrade && (selectedPackage is null ||
            (selectedPackage.Files.Count == 0 && (selectedPackage.Deleted is null || selectedPackage.Deleted.Count == 0))))
        {
            throw new InvalidDataException("platform has no update package");
        }

        var files = new List<(string Source, string Key, VersionFile File)>();
        var archives = new Dictionary<string, ReleaseAsset>(StringComparer.Ordinal);
        var endpointPath = new Uri(endpoint).AbsolutePath.Trim('/');
        if (publishUpgrade)
        {
            var package = selectedPackage!;
            if (package.Type != "upgrade" ||
                !PlatformPart().IsMatch(package.OS) || !PlatformPart().IsMatch(package.Arch))
            {
                throw new InvalidDataException("invalid package in manifest");
            }

            foreach (var file in package.Files)
            {
                var relative = ReleaseIndexService.PackageFilePath(package, file.Path);
                var source = ResolveTarget(versionDirectory, relative);
                VerifyFile(source, file);
                files.Add((source, JoinKey(endpointPath, version, relative), file));
            }

            if (package.Deleted is not null)
            {
                foreach (var path in package.Deleted)
                {
                    ResolveTarget(versionDirectory, path);
                }
            }
        }

        if (publishFull)
        {
            var fullRoot = Path.Combine(versionDirectory, "full", osName, arch, "files");
            if (!Directory.Exists(fullRoot))
            {
                throw new DirectoryNotFoundException(fullRoot);
            }

            foreach (var source in Directory.EnumerateFiles(fullRoot, "*",
                new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                var path = Path.GetRelativePath(fullRoot, source).Replace('\\', '/');
                var relative = "full/" + osName + "/" + arch + "/files/" + path;
                using var stream = File.OpenRead(source);
                var file = new VersionFile();
                file.Path = path;
                file.Size = stream.Length;
                file.SHA256 = Convert.ToHexStringLower(SHA256.HashData(stream));
                files.Add((source, JoinKey(endpointPath, version, relative), file));
            }
        }

        foreach (var type in new[] { "upgrade", "full" })
        {
            if ((type == "upgrade" && !publishUpgrade) || (type == "full" && !publishFull))
            {
                continue;
            }

            var relative = "archives/" + osName + "/" + arch + "/" + type + ".zip";
            var source = ResolveTarget(versionDirectory, relative);
            using var stream = File.OpenRead(source);
            var file = new VersionFile();
            file.Path = relative;
            file.Size = stream.Length;
            file.SHA256 = Convert.ToHexStringLower(SHA256.HashData(stream));
            files.Add((source, JoinKey(endpointPath, version, relative), file));
            var asset = new ReleaseAsset();
            asset.URL = version + "/" + ReleaseIndexService.EncodeRelativePath(relative);
            asset.Size = file.Size;
            asset.SHA256 = file.SHA256;
            archives[osName + "/" + arch + "/" + type + ".zip"] = asset;
        }

        var cos = new CosClient(this._client, cosBucketURL, secretID, secretKey, sessionToken);
        var indexKey = JoinKey(endpointPath, "index.json");
        var index = await cos.GetIndexAsync(indexKey, ct);
        if (publishUpgrade)
        {
            var previous = this._versions.ReadPreviousInventory(releasesDirectory, version, osName, arch);
            if (previous.Package is null)
            {
                if (index.Stable.Length > 0 && index.Stable != version)
                {
                    throw new InvalidOperationException("first update package must be the first published version");
                }
            }
            else if (index.Stable != previous.Version && index.Stable != version)
            {
                throw new InvalidOperationException("upgrade package does not follow the published stable version");
            }
        }

        var remoteManifestKey = JoinKey(endpointPath, version, "manifest.json");
        VersionManifest? remoteManifest = null;
        if (publishUpgrade)
        {
            remoteManifest = await cos.GetManifestAsync(remoteManifestKey, ct);
            if (remoteManifest is null)
            {
                remoteManifest = new VersionManifest { SchemaVersion = 2, Version = version };
            }
            else if (remoteManifest.SchemaVersion != 2 || remoteManifest.Version != version)
            {
                throw new InvalidDataException("remote version manifest is invalid");
            }

            remoteManifest.Packages.RemoveAll(item => item.OS == osName && item.Arch == arch);
            selectedPackage!.Published = true;
            remoteManifest.Packages.Add(selectedPackage);
        }

        var result = new PublishResult { Status = "published", Version = version };
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            VerifyFile(file.Source, file.File);
            if (await cos.PutFileIfChangedAsync(file.Key, file.Source, file.File.SHA256, ct))
            {
                result.Skipped++;
            }
            else
            {
                result.Uploaded++;
            }
        }

        if (publishUpgrade)
        {
            await cos.PutBytesAsync(
                remoteManifestKey,
                JsonSerializer.SerializeToUtf8Bytes(remoteManifest!, UpdaterJsonContext.Default.VersionManifest),
                "application/json; charset=utf-8",
                ct);
        }

        var indexed = index.Versions.Find(item => item.Version == version);
        if (publishUpgrade)
        {
            var updated = ReleaseIndexService.BuildIndexVersion(remoteManifest!);
            updated.Archives = indexed?.Archives;
            indexed = updated;
        }

        if (indexed is null)
        {
            indexed = new ReleaseIndexVersion();
            indexed.Version = version;
            indexed.Manifest = version + "/manifest.json";
        }

        indexed.Archives ??= new Dictionary<string, ReleaseAsset>(StringComparer.Ordinal);
        foreach (var archive in archives)
        {
            indexed.Archives[archive.Key] = archive.Value;
        }

        index.Versions.RemoveAll(item => item.Version == version);
        index.Versions.Insert(0, indexed);
        if (publishUpgrade)
        {
            index.Stable = version;
        }

        ReleaseIndexService.WriteIndexFiles(releasesDirectory, index, showHistory, indexTitle);

        await cos.PutBytesAsync(
            JoinKey(endpointPath, "index.html"),
            File.ReadAllBytes(Path.Combine(releasesDirectory, "index.html")),
            "text/html; charset=utf-8",
            ct);
        await cos.PutBytesAsync(
            indexKey,
            File.ReadAllBytes(Path.Combine(releasesDirectory, "index.json")),
            "application/json; charset=utf-8",
            ct);
        if (publishUpgrade)
        {
            var channel = new ReleaseChannel { Version = version, Manifest = endpoint + "/" + version + "/manifest.json" };
            var channelBody = JsonSerializer.SerializeToUtf8Bytes(channel, UpdaterJsonContext.Default.ReleaseChannel);
            await cos.PutBytesAsync(
                JoinKey(endpointPath, "channels/stable.json"),
                channelBody,
                "application/json; charset=utf-8",
                ct);
            this._versions.MarkPublished(releasesDirectory, version, osName, arch);
        }

        return result;
    }

    private static void VerifyFile(string path, VersionFile expected)
    {
        using var file = File.OpenRead(path);
        var digest = Convert.ToHexStringLower(SHA256.HashData(file));
        if (file.Length != expected.Size || digest != expected.SHA256)
        {
            throw new InvalidDataException($"package file changed: {expected.Path}");
        }
    }

    private static string JoinKey(params string[] parts)
    {
        var key = string.Empty;
        foreach (var part in parts)
        {
            if (part.Length == 0)
            {
                continue;
            }

            if (key.Length > 0)
            {
                key += "/";
            }

            key += part.Trim('/');
        }

        return key;
    }
}

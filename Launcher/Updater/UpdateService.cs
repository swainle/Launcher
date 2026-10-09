using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Launcher.Updater;

public sealed partial class UpdateService
{
    internal const long MaxManifestBytes = 4 << 20;
    private readonly HttpClient _client;

    public UpdateService(HttpClient client)
    {
        this._client = client;
    }

    public async Task<UpdateResult> CheckAsync(string releaseEndpoint, CancellationToken ct)
    {
        var endpoint = ValidateEndpoint(releaseEndpoint);
        var channel = await ReadJsonAsync(endpoint + "/channels/stable.json",
            UpdaterJsonContext.Default.ReleaseChannel, ct);

        // 稳定通道必须指向有效版本与 HTTPS 清单。
        if (!ValidVersion(channel.Version) || !ValidHttps(channel.Manifest))
        {
            throw new InvalidDataException("invalid stable channel");
        }

        return new UpdateResult { Status = "ok", Version = channel.Version };
    }

    public async Task<UpdateResult> GetAsync(
        string releaseEndpoint,
        string? releaseVersion,
        string workingDirectory,
        CancellationToken ct)
    {
        var endpoint = ValidateEndpoint(releaseEndpoint);
        var version = releaseVersion ?? string.Empty;

        // 显式版本必须符合发布目录约定。
        if (version.Length > 0 && !ValidVersion(version))
        {
            throw new ArgumentException("--release-version must be a version such as v0.22.1");
        }

        if (version.Length == 0)
        {
            var channel = await ReadJsonAsync(endpoint + "/channels/stable.json",
                UpdaterJsonContext.Default.ReleaseChannel, ct);

            // 通道内容不能决定任意文件路径。
            if (!ValidVersion(channel.Version) || !ValidHttps(channel.Manifest))
            {
                throw new InvalidDataException("invalid stable channel");
            }

            version = channel.Version;
        }

        var manifestUrl = endpoint + "/" + version + "/manifest.json";
        var manifestJson = await ReadJsonAsync(manifestUrl, UpdaterJsonContext.Default.JsonElement, ct);
        var osName = GetOS();
        var arch = GetArch();
        ReleasePlatform? platform = null;
        VersionPackage? delta = null;
        if (manifestJson.TryGetProperty("schemaVersion", out var schema))
        {
            if (schema.GetInt32() != 2)
            {
                throw new InvalidDataException("unsupported manifest schema");
            }

            var manifest = manifestJson.Deserialize(UpdaterJsonContext.Default.VersionManifest);
            if (manifest is null || manifest.Version != version)
            {
                throw new InvalidDataException("manifest version does not match requested version");
            }

            foreach (var package in manifest.Packages)
            {
                if (package.OS != osName || package.Arch != arch ||
                    (package.Type != "full" && package.Type != "upgrade"))
                {
                    continue;
                }

                if (package.Type == "upgrade")
                {
                    delta = package;
                }

                platform = new ReleasePlatform { OS = osName, Arch = arch };
                foreach (var file in package.Files)
                {
                    var asset = new ReleaseAsset();
                    asset.URL = endpoint + "/" + version + "/" + ReleaseIndexService.EncodeRelativePath(ReleaseIndexService.PackageFilePath(package, file.Path));
                    asset.SHA256 = file.SHA256;
                    asset.Size = file.Size;
                    asset.Target = file.Path;
                    platform.Files.Add(file.Path, asset);
                }

                break;
            }
        }
        else
        {
            var manifest = manifestJson.Deserialize(UpdaterJsonContext.Default.ReleaseManifest);
            if (manifest is null || manifest.Version != version)
            {
                throw new InvalidDataException("manifest version does not match requested version");
            }

            foreach (var item in manifest.Platforms)
            {
                if (item.OS == osName && item.Arch == arch)
                {
                    platform = item;
                    break;
                }
            }
        }

        // 当前平台必须有可安装资产。
        if (platform is null)
        {
            throw new InvalidDataException($"no platform for {osName}/{arch}");
        }

        if (platform.Files.Count == 0 && (delta is null || delta.Deleted is null || delta.Deleted.Count == 0))
        {
            throw new InvalidDataException("platform has no assets");
        }

        var targetDirectory = Path.Combine(workingDirectory, "release", version);
        var marker = string.Empty;
        if (delta is not null)
        {
            targetDirectory = Path.Combine(workingDirectory, "release", "current", osName, arch);
            marker = Path.Combine(workingDirectory, "release", "current", osName, arch + ".version");
            var index = await ReadJsonAsync(endpoint + "/index.json", UpdaterJsonContext.Default.ReleaseIndex, ct);
            var foundCurrent = false;
            var previous = string.Empty;
            foreach (var item in index.Versions)
            {
                if (item.Version == version)
                {
                    foundCurrent = true;
                    continue;
                }

                if (!foundCurrent || item.Packages is null)
                {
                    continue;
                }

                foreach (var package in item.Packages)
                {
                    if (package.OS == osName && package.Arch == arch)
                    {
                        previous = item.Version;
                        break;
                    }
                }

                if (previous.Length > 0)
                {
                    break;
                }
            }

            if (!foundCurrent)
            {
                throw new InvalidDataException("version is missing from release index");
            }

            var installedVersion = string.Empty;
            if (File.Exists(marker))
            {
                installedVersion = File.ReadAllText(marker).Trim();
            }
            if (installedVersion == version)
            {
                var matches = true;
                foreach (var file in delta.Inventory)
                {
                    var target = ResolveTarget(targetDirectory, file.Path);
                    var asset = new ReleaseAsset { Size = file.Size, SHA256 = file.SHA256 };
                    if (!await LocalAssetMatchesAsync(target, asset, ct))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    if (delta.Deleted is not null)
                    {
                        foreach (var path in delta.Deleted)
                        {
                            if (File.Exists(ResolveTarget(targetDirectory, path)))
                            {
                                matches = false;
                                break;
                            }
                        }
                    }
                }

                if (matches)
                {
                    return new UpdateResult { Status = "up-to-date", Version = version };
                }
            }

            if (installedVersion != previous && installedVersion != version)
            {
                throw new InvalidDataException($"update requires {previous}, installed {installedVersion}");
            }
        }

        Directory.CreateDirectory(targetDirectory);
        var pending = new List<ReleaseAsset>();
        var targets = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in platform.Files)
        {
            var asset = file.Value;
            asset.Target = file.Key;
            ValidateAsset(asset, targetDirectory);

            // 清单中的旧域名可以替换为当前发布域名。
            var assetUrl = new Uri(asset.URL);
            var manifestOrigin = new Uri(manifestUrl);
            if (assetUrl.Authority != manifestOrigin.Authority)
            {
                var rewritten = new UriBuilder(assetUrl);
                rewritten.Scheme = manifestOrigin.Scheme;
                rewritten.Host = manifestOrigin.Host;
                rewritten.Port = manifestOrigin.Port;
                asset.URL = rewritten.Uri.AbsoluteUri;
            }

            if (!targets.Add(asset.Target))
            {
                throw new InvalidDataException($"duplicate target: {asset.Target}");
            }

            var target = ResolveTarget(targetDirectory, asset.Target);
            if (!await LocalAssetMatchesAsync(target, asset, ct))
            {
                pending.Add(asset);
            }
        }

        if (delta is not null)
        {
            var changed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in delta.Files)
            {
                changed.Add(file.Path);
            }

            foreach (var file in delta.Inventory)
            {
                if (changed.Contains(file.Path))
                {
                    continue;
                }

                var target = ResolveTarget(targetDirectory, file.Path);
                var asset = new ReleaseAsset { Size = file.Size, SHA256 = file.SHA256 };
                if (!await LocalAssetMatchesAsync(target, asset, ct))
                {
                    throw new InvalidDataException($"base file does not match inventory: {file.Path}");
                }
            }
        }

        if (pending.Count == 0 && delta is null)
        {
            return new UpdateResult { Status = "up-to-date", Version = version };
        }

        // 所有文件完成下载与校验后才开始安装。
        var staging = Path.Combine(targetDirectory, ".fsu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var installed = false;
        try
        {
            var downloads = new List<(ReleaseAsset Asset, string Path)>();
            foreach (var asset in pending)
            {
                var destination = ResolveTarget(staging, asset.Target);
                await DownloadAsync(asset, destination, ct);
                downloads.Add((asset, destination));
            }

            Install(targetDirectory, staging, downloads, delta?.Deleted);
            if (delta is not null)
            {
                File.WriteAllText(marker, version);
            }
            installed = true;
        }
        finally
        {
            var backupDirectory = Path.Combine(staging, "backup");
            var backupRemaining = false;
            if (Directory.Exists(backupDirectory))
            {
                foreach (var backup in Directory.EnumerateFiles(backupDirectory, "*", SearchOption.AllDirectories))
                {
                    backupRemaining = true;
                    break;
                }
            }

            if (installed || !backupRemaining)
            {
                Directory.Delete(staging, recursive: true);
            }
        }

        return new UpdateResult { Status = "updated", Version = version };
    }

    private async Task<T> ReadJsonAsync<T>(string url, JsonTypeInfo<T> jsonType, CancellationToken ct)
    {
        using var response = await this._client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var limited = new LimitedStream(stream, MaxManifestBytes);
        var value = await JsonSerializer.DeserializeAsync(limited, jsonType, ct);
        if (value is null)
        {
            throw new InvalidDataException($"empty JSON: {url}");
        }

        return value;
    }

    private static async Task<bool> LocalAssetMatchesAsync(string target, ReleaseAsset asset, CancellationToken ct)
    {
        if (!File.Exists(target))
        {
            return false;
        }

        var info = new FileInfo(target);
        if (info.Length != asset.Size)
        {
            return false;
        }

        await using var file = File.OpenRead(target);
        var digest = await SHA256.HashDataAsync(file, ct);
        return Convert.ToHexStringLower(digest).Equals(asset.SHA256, StringComparison.OrdinalIgnoreCase);
    }

    private async Task DownloadAsync(ReleaseAsset asset, string destination, CancellationToken ct)
    {
        using var response = await this._client.GetAsync(asset.URL, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(destination);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long count = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0)
            {
                break;
            }

            count += read;
            if (count > asset.Size)
            {
                throw new InvalidDataException($"size mismatch for {asset.URL}");
            }

            hash.AppendData(buffer, 0, read);
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        if (count != asset.Size)
        {
            throw new InvalidDataException($"size mismatch for {asset.URL}: got {count}, want {asset.Size}");
        }

        var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!digest.Equals(asset.SHA256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"sha256 mismatch for {asset.URL}");
        }
    }

    private static void Install(
        string targetDirectory,
        string staging,
        List<(ReleaseAsset Asset, string Path)> downloads,
        List<string>? deleted)
    {
        var replaced = new List<(string Target, string Backup, bool Existed)>();
        try
        {
            foreach (var item in downloads)
            {
                var target = ResolveTarget(targetDirectory, item.Asset.Target);
                var backup = ResolveTarget(Path.Combine(staging, "backup"), item.Asset.Target);
                var existed = File.Exists(target);
                if (existed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Move(target, backup);
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(item.Path, target);
                }
                catch
                {
                    if (existed)
                    {
                        File.Move(backup, target);
                    }

                    throw;
                }

                replaced.Add((target, backup, existed));
            }

            if (deleted is not null)
            {
                foreach (var path in deleted)
                {
                    var target = ResolveTarget(targetDirectory, path);
                    if (!File.Exists(target))
                    {
                        continue;
                    }

                    var backup = ResolveTarget(Path.Combine(staging, "backup"), path);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Move(target, backup);
                    replaced.Add((target, backup, true));
                }
            }
        }
        catch (Exception error)
        {
            var rollbackErrors = new List<Exception>();
            for (var index = replaced.Count - 1; index >= 0; index--)
            {
                var item = replaced[index];
                try
                {
                    File.Delete(item.Target);
                    if (item.Existed)
                    {
                        File.Move(item.Backup, item.Target);
                    }
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add(rollbackError);
                }
            }

            if (rollbackErrors.Count > 0)
            {
                rollbackErrors.Insert(0, error);
                throw new AggregateException("installation failed; backups remain in staging", rollbackErrors);
            }

            throw;
        }
    }

    private static void ValidateAsset(ReleaseAsset asset, string targetDirectory)
    {
        if (!ValidHttps(asset.URL))
        {
            throw new InvalidDataException("asset URL must be an absolute HTTPS URL");
        }

        if (asset.Size < 0 || asset.Size > 1L << 40)
        {
            throw new InvalidDataException($"invalid asset size for {asset.URL}");
        }

        if (asset.SHA256.Length != 64)
        {
            throw new InvalidDataException($"invalid sha256 for {asset.URL}");
        }

        try
        {
            Convert.FromHexString(asset.SHA256);
        }
        catch (FormatException)
        {
            throw new InvalidDataException($"invalid sha256 for {asset.URL}");
        }

        ResolveTarget(targetDirectory, asset.Target);
    }

    internal static string ResolveTarget(string directory, string target)
    {
        if (target.Length == 0 || Path.IsPathRooted(target) || target.Contains(':'))
        {
            throw new InvalidDataException($"invalid target: {target}");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var path = Path.GetFullPath(Path.Combine(root, target.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"invalid target: {target}");
        }

        // 已存在的链接不能把安装位置引到发布目录外。
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"linked target: {target}");
            }
        }

        return path;
    }

    internal static string ValidateEndpoint(string rawURL)
    {
        if (!ValidHttps(rawURL))
        {
            throw new ArgumentException("release-endpoint: must be an absolute HTTPS URL");
        }

        return rawURL.TrimEnd('/');
    }

    internal static bool ValidVersion(string version)
    {
        return VersionPattern().IsMatch(version);
    }

    internal static bool ValidHttps(string rawURL)
    {
        return Uri.TryCreate(rawURL, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0;
    }

    public static string GetOS()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "darwin";
        }

        return "linux";
    }

    public static string GetArch()
    {
        return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "amd64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X86 => "386",
            System.Runtime.InteropServices.Architecture.Arm => "arm",
            _ => throw new PlatformNotSupportedException("unsupported architecture")
        };
    }

    [GeneratedRegex("^[a-z0-9]+$")]
    internal static partial Regex PlatformPart();

    [GeneratedRegex("^v[0-9][A-Za-z0-9._-]*$")]
    private static partial Regex VersionPattern();

    internal sealed class LimitedStream(Stream source, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => source.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => this._read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, (int)Math.Min(count, limit - this._read + 1));
            this._read += read;
            if (this._read > limit)
            {
                throw new InvalidDataException("JSON exceeds size limit");
            }

            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await source.ReadAsync(buffer[..(int)Math.Min(buffer.Length, limit - this._read + 1)], cancellationToken);
            this._read += read;
            if (this._read > limit)
            {
                throw new InvalidDataException("JSON exceeds size limit");
            }

            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

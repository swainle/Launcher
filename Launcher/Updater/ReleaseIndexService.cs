using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using static Launcher.Updater.UpdateService;

namespace Launcher.Updater;

public sealed class ReleaseIndexService
{
    private readonly VersionService _versions;

    public ReleaseIndexService(VersionService versions)
    {
        this._versions = versions;
    }

    public void WriteLocalIndex(string releasesDirectory, string version, bool showHistory, string indexTitle)
    {
        var current = BuildIndexVersion(releasesDirectory, version);
        var indexPath = Path.Combine(releasesDirectory, "index.json");
        var index = new ReleaseIndex();
        if (File.Exists(indexPath))
        {
            index = JsonSerializer.Deserialize(File.ReadAllBytes(indexPath), UpdaterJsonContext.Default.ReleaseIndex) ??
                new ReleaseIndex();
        }

        index.Versions.RemoveAll(item => item.Version == version);
        index.Versions.Insert(0, current);
        foreach (var localVersion in this._versions.GetVersions(releasesDirectory))
        {
            var exists = false;
            foreach (var item in index.Versions)
            {
                if (item.Version == localVersion)
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
            {
                var manifest = this._versions.GetManifest(releasesDirectory, localVersion);
                if (manifest.Packages.Count > 0)
                {
                    index.Versions.Add(BuildIndexVersion(releasesDirectory, localVersion));
                }
            }
        }

        if (current.Packages?.Count > 0)
        {
            index.Stable = version;
        }
        WriteIndexFiles(releasesDirectory, index, showHistory, indexTitle);
    }

    public void RebuildLocalIndex(string releasesDirectory, bool showHistory, string indexTitle)
    {
        var index = new ReleaseIndex();
        foreach (var version in this._versions.GetVersions(releasesDirectory))
        {
            var entry = BuildIndexVersion(releasesDirectory, version);
            if ((entry.Packages is null || entry.Packages.Count == 0) &&
                (entry.Archives is null || entry.Archives.Count == 0))
            {
                continue;
            }

            if (index.Stable.Length == 0 && entry.Packages?.Count > 0)
            {
                index.Stable = version;
            }

            index.Versions.Add(entry);
        }

        WriteIndexFiles(releasesDirectory, index, showHistory, indexTitle);
    }

    internal static ReleaseIndexVersion BuildIndexVersion(string releasesDirectory, string version)
    {
        var manifest = VersionService.ReadVersionManifest(Path.Combine(releasesDirectory, version, "manifest.json"));
        var entry = BuildIndexVersion(manifest);
        foreach (var package in manifest.Packages)
        {
            if (!PlatformPart().IsMatch(package.OS) || !PlatformPart().IsMatch(package.Arch))
            {
                throw new InvalidDataException("invalid package platform");
            }

            foreach (var type in new[] { "upgrade", "full" })
            {
                var relative = "archives/" + package.OS + "/" + package.Arch + "/" + type + ".zip";
                var path = Path.Combine(releasesDirectory, version, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                {
                    continue;
                }

                using var stream = File.OpenRead(path);
                var asset = new ReleaseAsset();
                asset.URL = version + "/" + EncodeRelativePath(relative);
                asset.Size = stream.Length;
                asset.SHA256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
                entry.Archives ??= new Dictionary<string, ReleaseAsset>(StringComparer.Ordinal);
                entry.Archives[package.OS + "/" + package.Arch + "/" + type + ".zip"] = asset;
            }
        }

        return entry;
    }

    internal static ReleaseIndexVersion BuildIndexVersion(VersionManifest manifest)
    {
        var entry = new ReleaseIndexVersion();
        entry.Version = manifest.Version;
        entry.Manifest = manifest.Version + "/manifest.json";
        entry.Packages = [];
        foreach (var package in manifest.Packages)
        {
            if (package.Files.Count == 0 && (package.Deleted is null || package.Deleted.Count == 0))
            {
                continue;
            }

            var indexed = new ReleaseIndexPackage();
            indexed.Type = package.Type;
            indexed.OS = package.OS;
            indexed.Arch = package.Arch;
            indexed.Deleted = package.Deleted;
            foreach (var file in package.Files)
            {
                var indexedFile = new ReleaseIndexFile();
                indexedFile.Path = file.Path;
                indexedFile.Size = file.Size;
                indexedFile.SHA256 = file.SHA256;
                indexedFile.URL = manifest.Version + "/" + EncodeRelativePath(PackageFilePath(package, file.Path));
                indexed.Files.Add(indexedFile);
            }

            entry.Packages.Add(indexed);
        }

        return entry;
    }

    internal static string PackageFilePath(VersionPackage package, string path)
    {
        if (package.Type is not "full" and not "upgrade" ||
            !PlatformPart().IsMatch(package.OS) || !PlatformPart().IsMatch(package.Arch))
        {
            throw new InvalidDataException("invalid package platform");
        }

        ResolveTarget(Path.GetTempPath(), path);
        return package.Type + "/" + package.OS + "/" + package.Arch + "/files/" + path;
    }

    internal static void WriteIndexFiles(string releasesDirectory, ReleaseIndex index, bool showHistory, string indexTitle)
    {
        Directory.CreateDirectory(releasesDirectory);
        var json = JsonSerializer.SerializeToUtf8Bytes(index, UpdaterJsonContext.Default.ReleaseIndex);
        var html = BuildReleasePage(json, showHistory, indexTitle);
        var jsonTemporary = Path.Combine(releasesDirectory, ".index-" + Guid.NewGuid().ToString("N") + ".tmp");
        var htmlTemporary = Path.Combine(releasesDirectory, ".page-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(jsonTemporary, json);
            File.WriteAllBytes(htmlTemporary, html);
            File.Move(htmlTemporary, Path.Combine(releasesDirectory, "index.html"), overwrite: true);
            File.Move(jsonTemporary, Path.Combine(releasesDirectory, "index.json"), overwrite: true);
        }
        finally
        {
            File.Delete(jsonTemporary);
            File.Delete(htmlTemporary);
        }
    }

    private static byte[] BuildReleasePage(byte[] indexBody, bool showHistory, string indexTitle)
    {
        if (string.IsNullOrWhiteSpace(indexTitle))
        {
            throw new InvalidDataException("indexTitle cannot be empty");
        }

        var html = """
            <!doctype html>
            <html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Flori Starter: downloads</title>
            <style>body{max-width:780px;margin:34px auto;padding:0 20px;color:#222;font:14px Arial,sans-serif;line-height:1.55}h1{padding-bottom:7px;border-bottom:2px solid #6b9c4a;font-size:26px;font-weight:normal}h4{margin:26px 0 7px;font-size:16px}.version{margin-top:13px}.platform{margin:10px 0 10px 18px}.asset{margin:5px 0 5px 18px}.asset code{display:block;overflow-wrap:anywhere}.platform b{color:#444}a{color:#285f9a;text-decoration:none}a:hover{text-decoration:underline}code{color:#555;font:12px Consolas,monospace}.muted{color:#666}</style>
            <h1>Flori Starter: downloads</h1><main id="releases"></main>
            <script>
            const data=INDEX_DATA,showHistory=SHOW_HISTORY;
            const root=document.querySelector('#releases');
            function tag(name,value){const element=document.createElement(name);element.textContent=value;return element}
            function addFile(parent,file){const row=tag('div','');row.className='asset';const link=document.createElement('a');link.href=file.url;link.textContent=file.path||file.url.split('/').pop();row.append(link,tag('code',file.sha256));parent.append(row)}
            function release(item){const section=tag('section','');section.className='version';section.append(tag('b',item.version));
              if(item.archives){const block=tag('div','');block.className='platform';block.append(tag('b','Package archives'));for(const [name,file] of Object.entries(item.archives))addFile(block,{path:name,url:file.url,sha256:file.sha256});section.append(block)}
              return section}
            const downloads=data.versions.filter(item=>item.archives&&Object.keys(item.archives).length);
            const stable=downloads.find(item=>item.version===data.stable);if(stable){root.append(tag('h4','Stable version'),release(stable))}
            {const older=downloads.filter(item=>item.version!==data.stable&&(showHistory||item===downloads[0]));if(older.length){root.append(tag('h4','Other versions'));for(const item of older)root.append(release(item))}}
            </script>
            """;
        html = html.Replace("INDEX_DATA", Encoding.UTF8.GetString(indexBody), StringComparison.Ordinal);
        html = html.Replace("SHOW_HISTORY", showHistory.ToString().ToLowerInvariant(), StringComparison.Ordinal);
        var title = System.Net.WebUtility.HtmlEncode(indexTitle);
        html = html.Replace("<title>Flori Starter: downloads</title>", "<title>" + title + "</title>", StringComparison.Ordinal);
        html = html.Replace("<h1>Flori Starter: downloads</h1>", "<h1>" + title + "</h1>", StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes(html);
    }

    internal static string EncodeRelativePath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        for (var index = 0; index < parts.Length; index++)
        {
            parts[index] = Uri.EscapeDataString(parts[index]);
        }

        return string.Join("/", parts);
    }


}

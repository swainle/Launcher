using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using static Launcher.Updater.UpdateService;

namespace Launcher.Updater;

public sealed class VersionService
{

    public void CreateVersion(string releasesDirectory, string version)
    {
        if (!ValidVersion(version))
        {
            throw new ArgumentException("invalid version name");
        }

        foreach (var previous in this.GetVersions(releasesDirectory))
        {
            if (CompareVersions(version, previous) <= 0)
            {
                throw new ArgumentException("new version must be later than existing versions");
            }
        }

        var directory = Path.Combine(releasesDirectory, version);
        if (Directory.Exists(directory))
        {
            throw new IOException($"version already exists: {version}");
        }

        Directory.CreateDirectory(directory);
        var manifest = new VersionManifest { SchemaVersion = 2, Version = version };
        File.WriteAllBytes(Path.Combine(directory, "manifest.json"),
            JsonSerializer.SerializeToUtf8Bytes(manifest, UpdaterJsonContext.Default.VersionManifest));
    }

    public bool HasPackage(string releasesDirectory, string version, string osName, string arch)
    {
        var path = Path.Combine(releasesDirectory, version, "manifest.json");
        if (!File.Exists(path))
        {
            return false;
        }

        foreach (var package in ReadVersionManifest(path).Packages)
        {
            if (package.OS == osName && package.Arch == arch &&
                (package.Files.Count > 0 || package.Deleted?.Count > 0))
            {
                return true;
            }
        }

        return false;
    }

    public List<string> GetVersions(string releasesDirectory)
    {
        var versions = new List<string>();
        if (!Directory.Exists(releasesDirectory))
        {
            return versions;
        }

        foreach (var directory in Directory.EnumerateDirectories(releasesDirectory))
        {
            var version = Path.GetFileName(directory);
            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!ValidVersion(version) || !File.Exists(manifestPath))
            {
                continue;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            if (document.RootElement.TryGetProperty("schemaVersion", out var schema) && schema.GetInt32() == 2)
            {
                versions.Add(version);
            }
        }

        versions.Sort((left, right) => CompareVersions(right, left));
        return versions;
    }

    public string GetNextVersion(string releasesDirectory)
    {
        foreach (var version in this.GetVersions(releasesDirectory))
        {
            var parts = version[1..].Split('.');
            var numeric = true;
            foreach (var part in parts)
            {
                if (!int.TryParse(part, out _))
                {
                    numeric = false;
                    break;
                }
            }

            if (!numeric || !int.TryParse(parts[^1], out var revision) || revision == int.MaxValue)
            {
                continue;
            }

            parts[^1] = (revision + 1).ToString();
            return "v" + string.Join('.', parts);
        }

        return "v0.1.0";
    }

    public void DeleteVersion(string releasesDirectory, string version)
    {
        if (!ValidVersion(version))
        {
            throw new ArgumentException("invalid version name");
        }

        var directory = ResolveTarget(releasesDirectory, version);
        var manifest = ReadVersionManifest(Path.Combine(directory, "manifest.json"));
        foreach (var package in manifest.Packages)
        {
            if (package.Published)
            {
                throw new InvalidOperationException("published version cannot be deleted locally");
            }
        }

        foreach (var later in this.GetVersions(releasesDirectory))
        {
            if (CompareVersions(later, version) <= 0)
            {
                continue;
            }

            var laterManifest = this.GetManifest(releasesDirectory, later);
            foreach (var package in laterManifest.Packages)
            {
                if (package.Inventory.Count > 0 || package.Files.Count > 0 ||
                    package.Deleted?.Count > 0 || package.Type == "full")
                {
                    throw new InvalidOperationException("later update package depends on this version");
                }
            }
        }

        Directory.Delete(directory, recursive: true);
    }

    public void DeletePlatform(string releasesDirectory, string version, string osName, string arch)
    {
        if (!ValidVersion(version) || !PlatformPart().IsMatch(osName) || !PlatformPart().IsMatch(arch))
        {
            throw new ArgumentException("invalid version or platform");
        }

        var manifest = this.GetManifest(releasesDirectory, version);
        var package = manifest.Packages.Find(item => item.OS == osName && item.Arch == arch);
        if (package is null)
        {
            throw new InvalidDataException("platform not found");
        }

        if (package.Published)
        {
            throw new InvalidOperationException("published platform cannot be deleted locally");
        }

        foreach (var later in this.GetVersions(releasesDirectory))
        {
            if (CompareVersions(later, version) <= 0)
            {
                continue;
            }

            var laterManifest = this.GetManifest(releasesDirectory, later);
            foreach (var laterPackage in laterManifest.Packages)
            {
                if (laterPackage.OS == osName && laterPackage.Arch == arch &&
                    (laterPackage.Inventory.Count > 0 || laterPackage.Files.Count > 0 ||
                     laterPackage.Deleted?.Count > 0 || laterPackage.Type == "full"))
                {
                    throw new InvalidOperationException("later update package depends on this platform");
                }
            }
        }

        var versionDirectory = ResolveTarget(releasesDirectory, version);
        var backupRoot = Path.Combine(versionDirectory, ".delete-" + Guid.NewGuid().ToString("N"));
        var moved = new List<(string Original, string Backup)>();
        try
        {
            foreach (var type in new[] { "upgrade", "full", "archives" })
            {
                var relative = Path.Combine(type, osName, arch);
                var original = ResolveTarget(versionDirectory, relative);
                if (!Directory.Exists(original))
                {
                    continue;
                }

                var backup = Path.Combine(backupRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                Directory.Move(original, backup);
                moved.Add((original, backup));
            }

            manifest.Packages.Remove(package);
            this.SaveManifest(releasesDirectory, manifest);
        }
        catch
        {
            for (var index = moved.Count - 1; index >= 0; index--)
            {
                Directory.Move(moved[index].Backup, moved[index].Original);
            }

            throw;
        }

        if (Directory.Exists(backupRoot))
        {
            var backup = ResolveTarget(versionDirectory, Path.GetFileName(backupRoot));
            Directory.Delete(backup, recursive: true);
        }
    }

    public VersionManifest GetManifest(string releasesDirectory, string version)
    {
        return ReadVersionManifest(Path.Combine(releasesDirectory, version, "manifest.json"));
    }

    public void AddPlatform(string releasesDirectory, string version, string osName, string arch)
    {
        if (!PlatformPart().IsMatch(osName) || !PlatformPart().IsMatch(arch))
        {
            throw new ArgumentException("invalid platform");
        }

        var manifest = this.GetManifest(releasesDirectory, version);
        foreach (var package in manifest.Packages)
        {
            if (package.OS == osName && package.Arch == arch)
            {
                return;
            }
        }

        manifest.Packages.Add(new VersionPackage { Type = "upgrade", OS = osName, Arch = arch, Deleted = [] });
        this.SaveManifest(releasesDirectory, manifest);
    }

    public void MarkPublished(string releasesDirectory, string version, string osName, string arch)
    {
        var manifest = this.GetManifest(releasesDirectory, version);
        foreach (var package in manifest.Packages)
        {
            if (package.OS == osName && package.Arch == arch)
            {
                package.Published = true;
                this.SaveManifest(releasesDirectory, manifest);
                return;
            }
        }

        throw new InvalidDataException("platform not found");
    }

    public void SetEnabled(string releasesDirectory, string version, string? osName, string? arch, bool enabled)
    {
        var manifest = this.GetManifest(releasesDirectory, version);
        if (osName is null || arch is null)
        {
            foreach (var package in manifest.Packages)
            {
                if (package.Published)
                {
                    throw new InvalidOperationException("published version cannot be unchecked");
                }
            }

            manifest.Enabled = enabled;
        }
        else
        {
            foreach (var package in manifest.Packages)
            {
                if (package.OS == osName && package.Arch == arch)
                {
                    if (package.Published)
                    {
                        throw new InvalidOperationException("published platform cannot be unchecked");
                    }

                    package.Enabled = enabled;
                    this.SaveManifest(releasesDirectory, manifest);
                    return;
                }
            }

            throw new InvalidDataException("platform not found");
        }

        this.SaveManifest(releasesDirectory, manifest);
    }

    internal void SaveManifest(string releasesDirectory, VersionManifest manifest)
    {
        var path = Path.Combine(releasesDirectory, manifest.Version, "manifest.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary,
                JsonSerializer.SerializeToUtf8Bytes(manifest, UpdaterJsonContext.Default.VersionManifest));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal (string Version, VersionPackage? Package) ReadPreviousInventory(
        string releasesDirectory,
        string version,
        string osName,
        string arch)
    {
        foreach (var previous in this.GetVersions(releasesDirectory))
        {
            if (CompareVersions(previous, version) >= 0)
            {
                continue;
            }

            var manifest = ReadVersionManifest(Path.Combine(releasesDirectory, previous, "manifest.json"));
            foreach (var package in manifest.Packages)
            {
                if (package.OS == osName && package.Arch == arch &&
                    (package.Type == "full" || package.Files.Count > 0 || package.Deleted?.Count > 0))
                {
                    return (previous, package);
                }
            }
        }

        return (string.Empty, null);
    }

    internal static int CompareVersions(string left, string right)
    {
        if (Version.TryParse(left[1..], out var leftVersion) &&
            Version.TryParse(right[1..], out var rightVersion))
        {
            return leftVersion.CompareTo(rightVersion);
        }

        return StringComparer.OrdinalIgnoreCase.Compare(left, right);
    }

    internal static VersionManifest ReadVersionManifest(string path)
    {
        var manifest = JsonSerializer.Deserialize(File.ReadAllBytes(path), UpdaterJsonContext.Default.VersionManifest);
        if (manifest is null || manifest.SchemaVersion != 2 || manifest.Version != Path.GetFileName(Path.GetDirectoryName(path)))
        {
            throw new InvalidDataException($"invalid version manifest: {path}");
        }

        return manifest;
    }

}

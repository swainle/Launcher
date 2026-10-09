using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using static Launcher.Updater.UpdateService;

namespace Launcher.Updater;

public sealed class PackageService
{
    private readonly VersionService _versions;

    public PackageService(VersionService versions)
    {
        this._versions = versions;
    }

    public List<ReleaseChange> FindChanges(
        string releasesDirectory,
        string version,
        string sourceDirectory,
        string osName,
        string arch,
        IReadOnlyCollection<string>? excludePaths = null)
    {
        ValidateBuildInputs(releasesDirectory, version, sourceDirectory, osName, arch);
        var exclusions = NormalizeExclusions(sourceDirectory, releasesDirectory, excludePaths);
        var previous = this._versions.ReadPreviousInventory(releasesDirectory, version, osName, arch);
        var oldFiles = new Dictionary<string, VersionFile>(StringComparer.Ordinal);
        if (previous.Package is not null)
        {
            var inventory = previous.Package.Inventory;
            if (previous.Package.Type == "full")
            {
                inventory = previous.Package.Files;
            }

            foreach (var file in inventory)
            {
                if (!IsExcluded(file.Path, exclusions))
                {
                    oldFiles.Add(file.Path, file);
                }
            }
        }

        var changes = new List<ReleaseChange>();
        foreach (var source in ScanSource(sourceDirectory, exclusions))
        {
            var kind = "新增";
            if (oldFiles.Remove(source.File.Path, out var old))
            {
                if (old.Size == source.File.Size && old.SHA256 == source.File.SHA256)
                {
                    continue;
                }

                kind = "修改";
            }

            changes.Add(new ReleaseChange { Path = source.File.Path, Kind = kind });
        }

        foreach (var old in oldFiles.Values)
        {
            changes.Add(new ReleaseChange { Path = old.Path, Kind = "删除" });
        }

        changes.Sort((left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
        return changes;
    }

    public VersionManifest BuildVersion(
        string releasesDirectory,
        string version,
        string sourceDirectory,
        string osName,
        string arch,
        IReadOnlyCollection<string> selectedPaths,
        IReadOnlyCollection<string>? excludePaths = null)
    {
        ValidateBuildInputs(releasesDirectory, version, sourceDirectory, osName, arch);
        var exclusions = NormalizeExclusions(sourceDirectory, releasesDirectory, excludePaths);
        var previous = this._versions.ReadPreviousInventory(releasesDirectory, version, osName, arch);
        var sourceFiles = new Dictionary<string, (VersionFile File, string Source)>(StringComparer.Ordinal);
        foreach (var source in ScanSource(sourceDirectory, exclusions))
        {
            sourceFiles.Add(source.File.Path, source);
        }

        var oldFiles = new Dictionary<string, VersionFile>(StringComparer.Ordinal);
        if (previous.Package is not null)
        {
            var inventory = previous.Package.Inventory;
            if (previous.Package.Type == "full")
            {
                inventory = previous.Package.Files;
            }

            foreach (var file in inventory)
            {
                if (!IsExcluded(file.Path, exclusions))
                {
                    oldFiles.Add(file.Path, file);
                }
            }
        }

        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sourceFiles.Values)
        {
            if (!oldFiles.TryGetValue(source.File.Path, out var old) ||
                old.Size != source.File.Size || old.SHA256 != source.File.SHA256)
            {
                changed.Add(source.File.Path);
            }
        }

        foreach (var old in oldFiles.Values)
        {
            if (!sourceFiles.ContainsKey(old.Path))
            {
                changed.Add(old.Path);
            }
        }

        var selected = new HashSet<string>(selectedPaths, StringComparer.Ordinal);
        foreach (var path in selected)
        {
            if (!changed.Contains(path))
            {
                throw new InvalidDataException($"file is no longer changed: {path}");
            }
        }

        var complete = new Dictionary<string, VersionFile>(oldFiles, StringComparer.Ordinal);
        var upgrade = new VersionPackage { Type = "upgrade", OS = osName, Arch = arch, Deleted = [] };
        foreach (var path in selected)
        {
            if (sourceFiles.TryGetValue(path, out var source))
            {
                complete[path] = source.File;
                upgrade.Files.Add(source.File);
            }
            else
            {
                complete.Remove(path);
                upgrade.Deleted.Add(path);
            }
        }

        foreach (var file in complete.Values)
        {
            upgrade.Inventory.Add(file);
        }

        if (upgrade.Files.Count == 0 && upgrade.Deleted.Count == 0)
        {
            throw new InvalidDataException("update package has no changes");
        }

        upgrade.Inventory.Sort((left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
        upgrade.Files.Sort((left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
        upgrade.Deleted.Sort(StringComparer.Ordinal);

        var versionDirectory = Path.Combine(releasesDirectory, version);
        var manifestPath = Path.Combine(versionDirectory, "manifest.json");
        var manifest = new VersionManifest { SchemaVersion = 2, Version = version };
        if (File.Exists(manifestPath))
        {
            manifest = VersionService.ReadVersionManifest(manifestPath);
        }
        foreach (var package in manifest.Packages)
        {
            if (package.OS == osName && package.Arch == arch && package.Published)
            {
                throw new InvalidOperationException("published package cannot be rebuilt");
            }
        }

        manifest.Packages.RemoveAll(item => item.OS == osName && item.Arch == arch);
        manifest.Packages.Add(upgrade);

        Directory.CreateDirectory(versionDirectory);
        var staging = Path.Combine(versionDirectory, ".build-" + Guid.NewGuid().ToString("N"));
        var upgradeRelative = Path.Combine("upgrade", osName, arch);
        var stagedUpgrade = Path.Combine(staging, upgradeRelative);
        Directory.CreateDirectory(Path.Combine(stagedUpgrade, "files"));
        try
        {
            foreach (var file in upgrade.Files)
            {
                var target = ResolveTarget(Path.Combine(stagedUpgrade, "files"), file.Path);
                CopyVerified(sourceFiles[file.Path].Source, target, file);
            }

            var manifestBody = JsonSerializer.SerializeToUtf8Bytes(manifest, UpdaterJsonContext.Default.VersionManifest);
            File.WriteAllBytes(Path.Combine(staging, "manifest.json"), manifestBody);
            CommitVersion(versionDirectory, staging, upgradeRelative);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                var backup = Path.Combine(staging, "backup");
                if (!Directory.Exists(backup))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
        }

        return manifest;
    }

    public void BuildArchives(
        string releasesDirectory,
        string version,
        string sourceDirectory,
        string osName,
        string arch,
        IReadOnlyCollection<string> selectedPaths,
        bool buildUpgrade,
        bool buildFull,
        IReadOnlyCollection<string>? excludePaths = null)
    {
        ValidateBuildInputs(releasesDirectory, version, sourceDirectory, osName, arch);
        if (!buildUpgrade && !buildFull)
        {
            throw new ArgumentException("select at least one package type");
        }

        var archiveDirectory = Path.Combine(releasesDirectory, version, "archives", osName, arch);
        Directory.CreateDirectory(archiveDirectory);
        if (buildUpgrade)
        {
            var manifest = this.BuildVersion(
                releasesDirectory, version, sourceDirectory, osName, arch, selectedPaths, excludePaths);
            var package = manifest.Packages.Find(item => item.OS == osName && item.Arch == arch)!;
            var upgradeFiles = Path.Combine(releasesDirectory, version, "upgrade", osName, arch, "files");
            var archivePath = Path.Combine(archiveDirectory, "upgrade.zip");
            _writeArchive(archivePath, archive =>
            {
                foreach (var file in package.Files)
                {
                    archive.CreateEntryFromFile(ResolveTarget(upgradeFiles, file.Path),
                        "files/" + file.Path, CompressionLevel.Optimal);
                }

                archive.CreateEntryFromFile(Path.Combine(releasesDirectory, version, "manifest.json"),
                    "manifest.json", CompressionLevel.Optimal);
            });
        }

        if (buildFull)
        {
            var exclusions = NormalizeExclusions(sourceDirectory, releasesDirectory, excludePaths);
            var files = ScanSource(sourceDirectory, exclusions);
            var versionDirectory = Path.Combine(releasesDirectory, version);
            var staging = Path.Combine(versionDirectory, ".full-" + Guid.NewGuid().ToString("N"));
            var stagedFiles = Path.Combine(staging, "files");
            Directory.CreateDirectory(stagedFiles);
            try
            {
                foreach (var file in files)
                {
                    CopyVerified(file.Source, ResolveTarget(stagedFiles, file.File.Path), file.File);
                }

                ZipFile.CreateFromDirectory(stagedFiles, Path.Combine(staging, "full.zip"),
                    CompressionLevel.Optimal, includeBaseDirectory: false);
                CommitFull(versionDirectory, staging, osName, arch);
            }
            finally
            {
                if (Directory.Exists(staging) &&
                    !Directory.Exists(Path.Combine(staging, "previous")) &&
                    !File.Exists(Path.Combine(staging, "previous.zip")))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
        }

        static void _writeArchive(string path, Action<ZipArchive> write)
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var archive = new ZipArchive(File.Create(temporary), ZipArchiveMode.Create))
                {
                    write(archive);
                }

                File.Move(temporary, path, true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }

    private static void ValidateBuildInputs(
        string releasesDirectory,
        string version,
        string sourceDirectory,
        string osName,
        string arch)
    {
        if (!ValidVersion(version) || !PlatformPart().IsMatch(osName) || !PlatformPart().IsMatch(arch))
        {
            throw new ArgumentException("invalid version or platform");
        }

        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException(sourceDirectory);
        }

        var source = Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var releases = Path.GetFullPath(releasesDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (source.Equals(releases, StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith(releases + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("source directory must not be inside Releases");
        }
    }

    private static List<string> NormalizeExclusions(
        string sourceDirectory,
        string releasesDirectory,
        IReadOnlyCollection<string>? excludePaths)
    {
        var exclusions = new List<string>();
        if (excludePaths is not null)
        {
            foreach (var raw in excludePaths)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    throw new InvalidDataException("excludePaths contains an empty path");
                }

                var path = raw.Replace('\\', '/').TrimEnd('/');
                if (path.StartsWith('/') || path.Contains(':'))
                {
                    throw new InvalidDataException($"invalid excluded path: {raw}");
                }

                foreach (var part in path.Split('/'))
                {
                    if (part.Length == 0 || part == "." || part == "..")
                    {
                        throw new InvalidDataException($"invalid excluded path: {raw}");
                    }
                }

                exclusions.Add(path);
            }
        }

        exclusions.Add("Config");

        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        var releasesRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(releasesDirectory));
        if (releasesRoot.StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            exclusions.Add(Path.GetRelativePath(sourceRoot, releasesRoot).Replace('\\', '/'));
        }

        return exclusions;
    }

    private static bool IsExcluded(string relative, IReadOnlyCollection<string> exclusions)
    {
        var path = relative.Replace('\\', '/');
        foreach (var excluded in exclusions)
        {
            if (path.Equals(excluded, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(excluded + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static List<(VersionFile File, string Source)> ScanSource(
        string sourceDirectory,
        IReadOnlyCollection<string> exclusions)
    {
        var files = new List<(VersionFile File, string Source)>();
        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        var pending = new Stack<string>();
        pending.Push(sourceRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var full = Path.GetFullPath(child);
                var relative = Path.GetRelativePath(sourceRoot, full).Replace('\\', '/');
                if (IsExcluded(relative, exclusions))
                {
                    continue;
                }

                if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"linked source directory: {full}");
                }

                pending.Push(full);
            }

            foreach (var path in Directory.EnumerateFiles(directory))
            {
                var relative = Path.GetRelativePath(sourceRoot, path).Replace('\\', '/');
                if (IsExcluded(relative, exclusions))
                {
                    continue;
                }

                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"linked source file: {path}");
                }

                ResolveTarget(sourceRoot, relative);
                using var stream = File.OpenRead(path);
                var file = new VersionFile();
                file.Path = relative;
                file.Size = stream.Length;
                file.SHA256 = Convert.ToHexStringLower(SHA256.HashData(stream));
                files.Add((file, path));
            }
        }

        return files;
    }

    private static void CopyVerified(string source, string target, VersionFile expected)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
        using var stream = File.OpenRead(target);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (stream.Length != expected.Size || sha256 != expected.SHA256)
        {
            throw new InvalidDataException($"source changed during packaging: {expected.Path}");
        }
    }

    private static void CommitFull(string versionDirectory, string staging, string osName, string arch)
    {
        var target = Path.Combine(versionDirectory, "full", osName, arch, "files");
        var archive = Path.Combine(versionDirectory, "archives", osName, arch, "full.zip");
        var previous = Path.Combine(staging, "previous");
        var previousArchive = Path.Combine(staging, "previous.zip");
        var hadFiles = Directory.Exists(target);
        var hadArchive = File.Exists(archive);
        try
        {
            if (hadFiles)
            {
                Directory.Move(target, previous);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(Path.Combine(staging, "files"), target);
            if (hadArchive)
            {
                File.Move(archive, previousArchive);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
            File.Move(Path.Combine(staging, "full.zip"), archive);
        }
        catch
        {
            if (Directory.Exists(previous))
            {
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                }

                Directory.Move(previous, target);
            }
            else if (!hadFiles && Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            if (File.Exists(previousArchive))
            {
                File.Delete(archive);
                File.Move(previousArchive, archive);
            }

            throw;
        }

        Directory.Delete(staging, recursive: true);
    }

    private static void CommitVersion(string versionDirectory, string staging, string upgradeRelative)
    {
        var backupRoot = Path.Combine(staging, "backup");
        var replaced = new List<(string Target, string Backup, bool Existed, bool IsFile)>();
        try
        {
            foreach (var relative in new[] { upgradeRelative })
            {
                var target = Path.Combine(versionDirectory, relative);
                var backup = Path.Combine(backupRoot, relative);
                var source = Path.Combine(staging, relative);
                var existed = Directory.Exists(target);
                if (existed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    Directory.Move(target, backup);
                }

                replaced.Add((target, backup, existed, false));
                if (Directory.Exists(source))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    Directory.Move(source, target);
                }
            }

            var manifest = Path.Combine(versionDirectory, "manifest.json");
            var manifestBackup = Path.Combine(backupRoot, "manifest.json");
            var hadManifest = File.Exists(manifest);
            if (hadManifest)
            {
                Directory.CreateDirectory(backupRoot);
                File.Move(manifest, manifestBackup);
            }

            replaced.Add((manifest, manifestBackup, hadManifest, true));
            File.Move(Path.Combine(staging, "manifest.json"), manifest);
        }
        catch (Exception error)
        {
            var rollbackErrors = new List<Exception>();
            for (var index = replaced.Count - 1; index >= 0; index--)
            {
                var item = replaced[index];
                try
                {
                    if (item.IsFile)
                    {
                        File.Delete(item.Target);
                        if (item.Existed)
                        {
                            File.Move(item.Backup, item.Target);
                        }
                    }
                    else
                    {
                        if (Directory.Exists(item.Target))
                        {
                            Directory.Delete(item.Target, recursive: true);
                        }

                        if (item.Existed)
                        {
                            Directory.Move(item.Backup, item.Target);
                        }
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
                throw new AggregateException("package failed; backups remain in staging", rollbackErrors);
            }

            if (Directory.Exists(backupRoot))
            {
                Directory.Delete(backupRoot, recursive: true);
            }

            throw;
        }

        if (Directory.Exists(backupRoot))
        {
            Directory.Delete(backupRoot, recursive: true);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Launcher.Updater;

var root = Path.Combine(Path.GetTempPath(), "updater-tests-" + Guid.NewGuid().ToString("N"));
var releases = Path.Combine(root, "Releases");
var source = Path.Combine(root, "source");
Directory.CreateDirectory(source);
try
{
    File.WriteAllText(Path.Combine(source, "a.txt"), "one");
    Directory.CreateDirectory(Path.Combine(source, "sub"));
    File.WriteAllText(Path.Combine(source, "sub", "b.txt"), "base");
    using var client = new HttpClient();
    var versions = new VersionService();
    var index = new ReleaseIndexService(versions);
    var packages = new PackageService(versions);
    var configRoot = Path.Combine(root, "config-test");
    Directory.CreateDirectory(Path.Combine(configRoot, "Config"));
    File.WriteAllText(Path.Combine(configRoot, "Config", "publish.json"),
        "{\"releasesDirectory\":\"Releases\",\"platformSources\":{\"linux/amd64\":\"LinuxPayload\"},\"indexTitle\":\"Downloads <safe>\"}");
    File.WriteAllText(Path.Combine(configRoot, "Config", "launcher.json"),
        "{\"releaseEndpoint\":\"https://example.com/releases\"}");
    var config = LauncherConfig.Load(configRoot);
    Assert(config.IndexTitle == "Downloads <safe>", "index title loads from publish config");
    Assert(LauncherConfig.LoadLaunch(configRoot).ReleaseEndpoint == "https://example.com/releases",
        "launch config loads independently of publish config");
    Assert(config.GetSourceDirectory(configRoot, "windows", "amd64") == configRoot,
        "source defaults to executable directory");
    Assert(config.GetSourceDirectory(configRoot, "linux", "amd64") ==
        Path.Combine(configRoot, "LinuxPayload"), "platform source from executable config");
    Assert(config.ExcludePaths.Contains("Config") && config.ExcludePaths.Contains("Data") &&
        config.ExcludePaths.Contains("Logs") && config.ExcludePaths.Contains("runtimes"),
        "default directories excluded");
    var legacyManifest = JsonSerializer.Deserialize(
        "{\"version\":\"v0.1.0\",\"platforms\":[{\"os\":\"windows\",\"arch\":\"amd64\",\"files\":{\"app.exe\":{\"url\":\"https://example.com/app.exe\",\"sha256\":\"abc\",\"size\":1}}}]}",
        UpdaterJsonContext.Default.ReleaseManifest);
    Assert(legacyManifest?.Platforms[0].Files["app.exe"].Target == "app.exe",
        "legacy platform JSON loads without reflection");
    var folder = new ReleaseFileNode { Name = "Launcher" };
    var added = new ReleaseChange { Path = "Launcher/new.txt", Kind = "新增" };
    var modified = new ReleaseChange { Path = "Launcher/old.txt", Kind = "修改" };
    var addedNode = new ReleaseFileNode { Name = "new.txt", Change = added, Parent = folder };
    var modifiedNode = new ReleaseFileNode { Name = "old.txt", Change = modified, Parent = folder };
    folder.Children.Add(addedNode);
    folder.Children.Add(modifiedNode);
    folder.SetSelected(false);
    Assert(!added.Selected && !modified.Selected && folder.Selected == false, "folder unchecks files");
    addedNode.SetSelected(true);
    Assert(added.Selected && !modified.Selected && folder.Selected is null, "mixed folder selection");
    folder.SetSelected(true);
    Assert(added.Selected && modified.Selected && folder.Selected == true, "folder checks files");
    Assert(addedNode.IsAdded && modifiedNode.IsModified && !addedNode.IsDeleted, "file status colors map to changes");

    var bundled = Path.Combine(root, "bundle");
    var bundledReleases = Path.Combine(bundled, "Releases");
    Directory.CreateDirectory(Path.Combine(bundled, "Config"));
    Directory.CreateDirectory(Path.Combine(bundled, "Data"));
    Directory.CreateDirectory(Path.Combine(bundled, "Logs"));
    Directory.CreateDirectory(Path.Combine(bundled, "runtimes"));
    File.WriteAllText(Path.Combine(bundled, "Config", "publish.json"), "secret-key");
    File.WriteAllText(Path.Combine(bundled, "Data", "local.db"), "user-data");
    File.WriteAllText(Path.Combine(bundled, "Logs", "trace.log"), "log");
    File.WriteAllText(Path.Combine(bundled, "runtimes", "runtime.dll"), "runtime");
    File.WriteAllText(Path.Combine(bundled, "scratch.txt"), "scratch");
    File.WriteAllText(Path.Combine(bundled, "Launcher.exe"), "program");
    versions.CreateVersion(bundledReleases, "v0.1.0");
    var exclusions = new List<string>(config.ExcludePaths) { "scratch.txt" };
    var bundledChanges = packages.FindChanges(bundledReleases, "v0.1.0", bundled, "windows", "amd64", exclusions);
    Assert(bundledChanges.Count == 1 && bundledChanges[0].Path == "Launcher.exe",
        "configured files, directories, and nested Releases excluded from scan");
    packages.BuildArchives(bundledReleases, "v0.1.0", bundled, "windows", "amd64",
        ["Launcher.exe"], true, true, exclusions);
    Assert(versions.GetManifest(bundledReleases, "v0.1.0").Packages[0].Inventory.Count == 1,
        "nested Releases excluded during packaging");
    var archiveRoot = Path.Combine(bundledReleases, "v0.1.0", "archives", "windows", "amd64");
    var fullRoot = Path.Combine(bundledReleases, "v0.1.0", "full", "windows", "amd64", "files");
    Assert(File.ReadAllText(Path.Combine(fullRoot, "Launcher.exe")) == "program" &&
        !File.Exists(Path.Combine(fullRoot, "Config", "publish.json")),
        "full package copies the same included files");
    using (var upgradeArchive = ZipFile.OpenRead(Path.Combine(archiveRoot, "upgrade.zip")))
    {
        Assert(upgradeArchive.GetEntry("files/Launcher.exe") is not null &&
            upgradeArchive.GetEntry("manifest.json") is not null, "upgrade archive contains files and manifest");
    }

    using (var fullArchive = ZipFile.OpenRead(Path.Combine(archiveRoot, "full.zip")))
    {
        Assert(fullArchive.GetEntry("Launcher.exe") is not null &&
            fullArchive.GetEntry("Config/publish.json") is null &&
            fullArchive.GetEntry("Releases/v0.1.0/manifest.json") is null,
            "full archive excludes local configuration and release output");
    }

    File.Delete(Path.Combine(archiveRoot, "full.zip"));
    packages.BuildArchives(bundledReleases, "v0.1.0", bundled, "windows", "amd64",
        [], false, true, exclusions);
    Assert(File.Exists(Path.Combine(archiveRoot, "full.zip")),
        "full archive can be built without selected changes");
    File.WriteAllText(Path.Combine(bundled, "Launcher.exe"), "revised");
    packages.BuildArchives(bundledReleases, "v0.1.0", bundled, "windows", "amd64",
        [], false, true, exclusions);
    Assert(File.ReadAllText(Path.Combine(fullRoot, "Launcher.exe")) == "revised",
        "full rebuild replaces copied files");
    using (var rebuiltArchive = ZipFile.OpenRead(Path.Combine(archiveRoot, "full.zip")))
    using (var entry = new StreamReader(rebuiltArchive.GetEntry("Launcher.exe")!.Open()))
    {
        Assert(entry.ReadToEnd() == "revised", "full rebuild replaces archive contents");
    }
    var withoutConfigEntry = packages.FindChanges(bundledReleases, "v0.1.0", bundled, "windows", "amd64",
        ["Data", "Logs", "runtimes", "scratch.txt"]);
    var unexpectedChanges = new List<string>();
    foreach (var change in withoutConfigEntry)
    {
        unexpectedChanges.Add(change.Path);
    }

    Assert(withoutConfigEntry.Count == 1 && withoutConfigEntry[0].Path == "Launcher.exe",
        "Config remains excluded without config entry: " + string.Join(", ", unexpectedChanges));
    var invalidExclusionRejected = false;
    try
    {
        packages.FindChanges(bundledReleases, "v0.1.0", bundled, "windows", "amd64", ["../outside"]);
    }
    catch (InvalidDataException)
    {
        invalidExclusionRejected = true;
    }

    Assert(invalidExclusionRejected, "excluded paths must stay under scan directory");
    versions.CreateVersion(releases, "v0.1.0");
    var firstChanges = packages.FindChanges(releases, "v0.1.0", source, "windows", "amd64");
    Assert(firstChanges.Count == 2, "first version changes");
    packages.BuildVersion(releases, "v0.1.0", source, "windows", "amd64",
        ["a.txt", "sub/b.txt"]);
    index.WriteLocalIndex(releases, "v0.1.0", true, config.IndexTitle);
    Assert(File.Exists(Path.Combine(releases, "v0.1.0", "upgrade", "windows", "amd64", "files", "a.txt")), "first update file");

    File.WriteAllText(Path.Combine(source, "a.txt"), "two");
    File.Delete(Path.Combine(source, "sub", "b.txt"));
    File.WriteAllText(Path.Combine(source, "c.txt"), "unselected");
    versions.CreateVersion(releases, "v0.1.1");
    Assert(versions.GetNextVersion(releases) == "v0.1.2", "next revision suggested");
    var changes = packages.FindChanges(releases, "v0.1.1", source, "windows", "amd64");
    Assert(changes.Count == 3, "changed, new, and deleted files");
    var manifest = packages.BuildVersion(releases, "v0.1.1", source, "windows", "amd64",
        ["a.txt", "sub/b.txt"]);
    index.WriteLocalIndex(releases, "v0.1.1", false, config.IndexTitle);
    var updateRoot = Path.Combine(releases, "v0.1.1", "upgrade", "windows", "amd64", "files");
    Assert(File.ReadAllText(Path.Combine(updateRoot, "a.txt")) == "two", "selected change copied");
    Assert(!File.Exists(Path.Combine(updateRoot, "c.txt")), "unselected change excluded");
    Assert(!File.Exists(Path.Combine(updateRoot, "sub", "b.txt")), "deleted file not copied");
    Assert(manifest.Packages.Count == 1, "upgrade package only");
    Assert(manifest.Packages[0].Deleted?.Count == 1, "deletion recorded");
    Assert(manifest.Packages[0].Inventory.Count == 1, "complete inventory retained");
    var html = File.ReadAllText(Path.Combine(releases, "index.html"));
    Assert(html.Contains("showHistory=false") && !html.Contains("pack.files") &&
        !html.Contains("Deleted:") && !html.Contains("platform.files"),
        "download page shows archives without individual update files");
    Assert(html.Contains("<title>Downloads &lt;safe&gt;</title>") &&
        html.Contains("<h1>Downloads &lt;safe&gt;</h1>"), "configured title is HTML escaped");

    packages.BuildVersion(releases, "v0.1.1", source, "windows", "amd64",
        ["a.txt", "sub/b.txt", "c.txt"]);
    index.WriteLocalIndex(releases, "v0.1.1", true, config.IndexTitle);
    Assert(File.Exists(Path.Combine(updateRoot, "c.txt")), "repackage refreshes update package");

    using var downloadClient = new HttpClient(new FakeHandler(request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/releases/channels/stable.json")
        {
            return Json(new ReleaseChannel { Version = "v0.1.1", Manifest = "https://example.com/releases/v0.1.1/manifest.json" },
                UpdaterJsonContext.Default.ReleaseChannel);
        }

        var local = Path.Combine(releases, path["/releases/".Length..].Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(local))
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(local)) };
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }));
    var downloader = new UpdateService(downloadClient);
    var firstInstalled = await downloader.GetAsync("https://example.com/releases", "v0.1.0", root, CancellationToken.None);
    var installed = await downloader.GetAsync("https://example.com/releases", null, root, CancellationToken.None);
    var repeated = await downloader.GetAsync("https://example.com/releases", null, root, CancellationToken.None);
    Assert(firstInstalled.Status == "updated" && installed.Status == "updated" && repeated.Status == "up-to-date", "update-only download");
    var installedRoot = Path.Combine(root, "release", "current", "windows", "amd64");
    Assert(File.ReadAllText(Path.Combine(installedRoot, "a.txt")) == "two" &&
        !File.Exists(Path.Combine(installedRoot, "sub", "b.txt")), "changed and deleted files applied");
    Directory.CreateDirectory(Path.Combine(installedRoot, "sub"));
    File.WriteAllText(Path.Combine(installedRoot, "sub", "b.txt"), "stale");
    var repaired = await downloader.GetAsync("https://example.com/releases", null, root, CancellationToken.None);
    Assert(repaired.Status == "updated" && !File.Exists(Path.Combine(installedRoot, "sub", "b.txt")), "deleted file repaired");

    packages.BuildArchives(releases, "v0.1.1", source, "windows", "amd64",
        ["a.txt", "sub/b.txt", "c.txt"], true, true);

    var uploaded = new HashSet<string>(StringComparer.Ordinal);
    byte[]? publishedManifest = null;
    byte[]? publishedIndex = null;
    var remoteManifest = new VersionManifest { SchemaVersion = 2, Version = "v0.1.1" };
    remoteManifest.Packages.Add(new VersionPackage
    {
        Type = "upgrade", OS = "linux", Arch = "amd64", Published = true,
        Files = [new VersionFile { Path = "linux.bin", Size = 1, SHA256 = new string('a', 64) }]
    });
    using var publishClient = new HttpClient(new FakeHandler(request =>
    {
        Assert(request.Headers.Contains("Authorization"), "COS request signature");
        if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/releases/index.json")
        {
            return Json(new ReleaseIndex { Stable = "v0.1.0" }, UpdaterJsonContext.Default.ReleaseIndex);
        }

        if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/releases/v0.1.1/manifest.json")
        {
            return Json(remoteManifest, UpdaterJsonContext.Default.VersionManifest);
        }

        if (request.Method == HttpMethod.Head || request.Method == HttpMethod.Get)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        uploaded.Add(request.RequestUri!.AbsolutePath);
        if (request.RequestUri.AbsolutePath == "/releases/v0.1.1/manifest.json")
        {
            publishedManifest = request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        }
        if (request.RequestUri.AbsolutePath == "/releases/index.json")
        {
            publishedIndex = request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        }
        return new HttpResponseMessage(HttpStatusCode.OK);
    }));
    var publisher = new PublishService(publishClient, versions);
    var published = await publisher.PublishVersionAsync(releases, "v0.1.1", "windows", "amd64",
        "https://example.com/releases", "https://bucket.example.com", "test-id", "test-key", "",
        true, CancellationToken.None, true, true, config.IndexTitle);
    Assert(published.Status == "published" && uploaded.Contains("/releases/index.json") &&
        uploaded.Contains("/releases/index.html") && uploaded.Contains("/releases/channels/stable.json"), "publish index and channel");
    Assert(uploaded.Contains("/releases/v0.1.1/upgrade/windows/amd64/files/a.txt") &&
        uploaded.Contains("/releases/v0.1.1/full/windows/amd64/files/a.txt") &&
        uploaded.Contains("/releases/v0.1.1/archives/windows/amd64/upgrade.zip") &&
        uploaded.Contains("/releases/v0.1.1/archives/windows/amd64/full.zip"),
        "selected upgrade, full, and archive files uploaded");
    var publishedReleaseIndex = JsonSerializer.Deserialize(publishedIndex!, UpdaterJsonContext.Default.ReleaseIndex);
    Assert(publishedReleaseIndex?.Versions[0].Archives?.Count == 2,
        "published index links both archive types");
    var merged = JsonSerializer.Deserialize(publishedManifest!, UpdaterJsonContext.Default.VersionManifest);
    Assert(merged?.Packages.Count == 2 && versions.GetManifest(releases, "v0.1.1").Packages[0].Published,
        "selected platform published without dropping remote platform");
    uploaded.Clear();
    await publisher.PublishVersionAsync(releases, "v0.1.1", "windows", "amd64",
        "https://example.com/releases", "https://bucket.example.com", "test-id", "test-key", "",
        true, CancellationToken.None, false, true, config.IndexTitle);
    Assert(uploaded.Contains("/releases/v0.1.1/full/windows/amd64/files/a.txt") &&
        uploaded.Contains("/releases/v0.1.1/archives/windows/amd64/full.zip") &&
        !uploaded.Contains("/releases/v0.1.1/archives/windows/amd64/upgrade.zip") &&
        !uploaded.Contains("/releases/channels/stable.json"),
        "full-only publish leaves update channel unchanged");
    var publishedProtected = false;
    try
    {
        versions.DeleteVersion(releases, "v0.1.1");
    }
    catch (InvalidOperationException)
    {
        publishedProtected = true;
    }

    Assert(publishedProtected, "published version protected from local deletion");
    var dependencyProtected = false;
    try
    {
        versions.DeleteVersion(releases, "v0.1.0");
    }
    catch (InvalidOperationException)
    {
        dependencyProtected = true;
    }

    Assert(dependencyProtected, "earlier version needed by later update protected");
    dependencyProtected = false;
    try
    {
        versions.DeletePlatform(releases, "v0.1.0", "windows", "amd64");
    }
    catch (InvalidOperationException)
    {
        dependencyProtected = true;
    }

    Assert(dependencyProtected, "earlier platform needed by later update protected");

    versions.CreateVersion(releases, "v0.1.3");
    versions.AddPlatform(releases, "v0.1.3", "windows", "amd64");
    var draftPlatform = Path.Combine(releases, "v0.1.3", "upgrade", "windows", "amd64");
    var draftArchives = Path.Combine(releases, "v0.1.3", "archives", "windows", "amd64");
    Directory.CreateDirectory(draftPlatform);
    Directory.CreateDirectory(draftArchives);
    File.WriteAllText(Path.Combine(draftPlatform, "test.bin"), "draft");
    File.WriteAllText(Path.Combine(draftArchives, "full.zip"), "draft");
    versions.DeletePlatform(releases, "v0.1.3", "windows", "amd64");
    Assert(versions.GetManifest(releases, "v0.1.3").Packages.Count == 0 &&
        !Directory.Exists(draftPlatform) && !Directory.Exists(draftArchives),
        "draft platform and files deleted");
    versions.DeleteVersion(releases, "v0.1.3");
    index.RebuildLocalIndex(releases, true, config.IndexTitle);
    Assert(!Directory.Exists(Path.Combine(releases, "v0.1.3")) &&
        !File.ReadAllText(Path.Combine(releases, "index.json")).Contains("v0.1.3"), "draft version removed from local index");

    versions.CreateVersion(releases, "v0.1.2");
    var invalidManifest = Path.Combine(releases, "v0.1.2", "manifest.json");
    File.WriteAllText(invalidManifest, "{\"version\":\"v0.1.2\",\"packages\":[]}");
    var rejected = false;
    try
    {
        versions.HasPackage(releases, "v0.1.2", "windows", "amd64");
    }
    catch (InvalidDataException)
    {
        rejected = true;
    }

    Assert(rejected, "manifest without schema rejected");
    Console.WriteLine("Updater tests passed");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static void Assert(bool valid, string message)
{
    if (!valid)
    {
        throw new InvalidOperationException(message);
    }
}

static HttpResponseMessage Json<T>(T value, JsonTypeInfo<T> jsonType)
{
    return new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, jsonType))
    };
}

sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(respond(request));
    }
}

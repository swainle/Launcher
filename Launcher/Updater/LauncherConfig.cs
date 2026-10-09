using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.Updater;

public sealed class LauncherConfig
{
    [JsonPropertyName("releasesDirectory")]
    public string ReleasesDirectory { get; set; } = "Releases";

    [JsonPropertyName("sourceDirectory")]
    public string SourceDirectory { get; set; } = ".";

    [JsonPropertyName("platformSources")]
    public Dictionary<string, string> PlatformSources { get; set; } = [];

    [JsonPropertyName("excludePaths")]
    public List<string> ExcludePaths { get; set; } = ["Releases", "release", "Config", "Data", "Logs", "runtimes"];

    [JsonPropertyName("releaseEndpoint")]
    public string ReleaseEndpoint { get; set; } = string.Empty;

    [JsonPropertyName("cosBucketURL")]
    public string CosBucketURL { get; set; } = string.Empty;

    [JsonPropertyName("secretID")]
    public string SecretID { get; set; } = string.Empty;

    [JsonPropertyName("secretKey")]
    public string SecretKey { get; set; } = string.Empty;

    [JsonPropertyName("sessionToken")]
    public string SessionToken { get; set; } = string.Empty;

    [JsonPropertyName("showHistory")]
    public bool ShowHistory { get; set; } = true;

    [JsonPropertyName("indexTitle")]
    public string IndexTitle { get; set; } = "Flori Starter: downloads";

    public static LauncherConfig Load(string baseDirectory)
    {
        return Read(baseDirectory, "publish.json");
    }

    public static LauncherConfig LoadLaunch(string baseDirectory)
    {
        return Read(baseDirectory, "launcher.json");
    }

    private static LauncherConfig Read(string baseDirectory, string fileName)
    {
        var path = Path.Combine(baseDirectory, "Config", fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Configuration file not found.", path);
        }

        var config = JsonSerializer.Deserialize(File.ReadAllBytes(path), UpdaterJsonContext.Default.LauncherConfig) ??
            throw new InvalidDataException("Configuration file is empty.");
        if (config.ExcludePaths is null)
        {
            throw new InvalidDataException("excludePaths cannot be null.");
        }

        return config;
    }

    public string GetReleasesDirectory(string baseDirectory)
    {
        return Resolve(baseDirectory, this.ReleasesDirectory);
    }

    public string GetSourceDirectory(string baseDirectory, string osName, string arch)
    {
        var key = osName + "/" + arch;
        var path = this.SourceDirectory;
        if (this.PlatformSources.TryGetValue(key, out var configured))
        {
            path = configured;
        }

        return Resolve(baseDirectory, path);
    }

    private static string Resolve(string baseDirectory, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException("Configuration directory cannot be empty.");
        }

        return Path.GetFullPath(Path.Combine(baseDirectory, path));
    }
}

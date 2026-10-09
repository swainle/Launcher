using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.Updater;

public sealed class ReleaseChannel
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("manifest")]
    public string Manifest { get; set; } = string.Empty;
}

public sealed class ReleaseManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("platforms")]
    public List<ReleasePlatform> Platforms { get; set; } = [];
}

public sealed class ReleaseIndex
{
    [JsonPropertyName("stable")]
    public string Stable { get; set; } = string.Empty;

    [JsonPropertyName("versions")]
    public List<ReleaseIndexVersion> Versions { get; set; } = [];
}

public sealed class ReleaseIndexVersion
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("manifest")]
    public string Manifest { get; set; } = string.Empty;

    [JsonPropertyName("platforms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ReleasePlatform>? Platforms { get; set; }

    [JsonPropertyName("packages")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ReleaseIndexPackage>? Packages { get; set; }

    [JsonPropertyName("archives")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, ReleaseAsset>? Archives { get; set; }
}

[JsonConverter(typeof(ReleasePlatformConverter))]
public sealed class ReleasePlatform
{
    public string OS { get; set; } = string.Empty;

    public string Arch { get; set; } = string.Empty;

    public Dictionary<string, ReleaseAsset> Files { get; set; } = [];
}

public sealed class ReleaseAsset
{
    [JsonPropertyName("url")]
    public string URL { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string SHA256 { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonIgnore]
    public string Target { get; set; } = string.Empty;
}

public sealed class UpdateResult
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Version { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Message { get; set; }
}

public sealed class PublishResult
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Version { get; set; }

    [JsonPropertyName("uploaded")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Uploaded { get; set; }

    [JsonPropertyName("skipped")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Skipped { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Message { get; set; }
}

internal sealed class ReleasePlatformConverter : JsonConverter<ReleasePlatform>
{
    public override ReleasePlatform Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var platform = new ReleasePlatform();

        platform.OS = root.GetProperty("os").GetString() ?? string.Empty;
        platform.Arch = root.GetProperty("arch").GetString() ?? string.Empty;

        // 旧清单把文件直接放在平台对象下，新清单使用 files。
        if (root.TryGetProperty("files", out var files))
        {
            platform.Files = files.Deserialize(UpdaterJsonContext.Default.DictionaryStringReleaseAsset) ?? [];
        }
        else
        {
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name is "os" or "arch")
                {
                    continue;
                }

                var asset = property.Value.Deserialize(UpdaterJsonContext.Default.ReleaseAsset);
                if (asset is null)
                {
                    throw new JsonException($"Invalid asset: {property.Name}");
                }

                var target = property.Name;
                if (Path.GetExtension(target).Length == 0)
                {
                    target += Path.GetExtension(new Uri(asset.URL).AbsolutePath);
                }

                platform.Files.Add(target, asset);
            }
        }

        foreach (var file in platform.Files)
        {
            file.Value.Target = file.Key;
        }

        return platform;
    }

    public override void Write(Utf8JsonWriter writer, ReleasePlatform value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("os", value.OS);
        writer.WriteString("arch", value.Arch);
        writer.WritePropertyName("files");
        JsonSerializer.Serialize(writer, value.Files, UpdaterJsonContext.Default.DictionaryStringReleaseAsset);
        writer.WriteEndObject();
    }
}

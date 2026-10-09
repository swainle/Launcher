using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.Updater;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(LauncherConfig))]
[JsonSerializable(typeof(VersionManifest))]
[JsonSerializable(typeof(ReleaseIndex))]
[JsonSerializable(typeof(ReleaseChannel))]
[JsonSerializable(typeof(ReleaseManifest))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(Dictionary<string, ReleaseAsset>))]
[JsonSerializable(typeof(ReleaseAsset))]
public partial class UpdaterJsonContext : JsonSerializerContext
{
}

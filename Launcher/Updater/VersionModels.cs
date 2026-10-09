using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Launcher.Updater;

public sealed class VersionManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("packages")]
    public List<VersionPackage> Packages { get; set; } = [];

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

public sealed class VersionPackage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("os")]
    public string OS { get; set; } = string.Empty;

    [JsonPropertyName("arch")]
    public string Arch { get; set; } = string.Empty;

    [JsonPropertyName("files")]
    public List<VersionFile> Files { get; set; } = [];

    [JsonPropertyName("inventory")]
    public List<VersionFile> Inventory { get; set; } = [];

    [JsonPropertyName("published")]
    public bool Published { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("deleted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Deleted { get; set; }
}

public class VersionFile
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string SHA256 { get; set; } = string.Empty;
}

public sealed class ReleaseIndexPackage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("os")]
    public string OS { get; set; } = string.Empty;

    [JsonPropertyName("arch")]
    public string Arch { get; set; } = string.Empty;

    [JsonPropertyName("files")]
    public List<ReleaseIndexFile> Files { get; set; } = [];

    [JsonPropertyName("deleted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Deleted { get; set; }
}

public sealed class ReleaseIndexFile : VersionFile
{
    [JsonPropertyName("url")]
    public string URL { get; set; } = string.Empty;
}

public sealed class ReleaseChange
{
    public string Path { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public bool Selected { get; set; } = true;
}

public sealed class ReleaseFileNode : INotifyPropertyChanged
{
    private bool? _selected = true;

    public string Name { get; set; } = string.Empty;
    public ReleaseFileNode? Parent { get; set; }
    public ReleaseChange? Change { get; set; }
    public ObservableCollection<ReleaseFileNode> Children { get; } = [];
    public bool IsAdded => this.Change?.Kind == "新增";
    public bool IsModified => this.Change?.Kind == "修改";
    public bool IsDeleted => this.Change?.Kind == "删除";

    public bool? Selected
    {
        get => this._selected;
        set
        {
            if (value.HasValue)
            {
                this.SetSelected(value.Value);
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetSelected(bool selected)
    {
        this._selected = selected;
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.Selected)));
        if (this.Change is not null)
        {
            this.Change.Selected = selected;
        }

        foreach (var child in this.Children)
        {
            child.SetSelected(selected);
        }

        this.Parent?.RefreshSelection();
    }

    private void RefreshSelection()
    {
        var anySelected = false;
        var anyUnselected = false;
        foreach (var child in this.Children)
        {
            if (child.Selected == true)
            {
                anySelected = true;
            }
            else if (child.Selected == false)
            {
                anyUnselected = true;
            }
            else
            {
                anySelected = true;
                anyUnselected = true;
            }
        }

        if (anySelected && anyUnselected)
        {
            this._selected = null;
        }
        else
        {
            this._selected = anySelected;
        }
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.Selected)));
        this.Parent?.RefreshSelection();
    }
}

public sealed class ReleaseChoice
{
    public string Name { get; set; } = string.Empty;
    public bool Published { get; set; }
}


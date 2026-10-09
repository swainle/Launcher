using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Launcher.Updater;

namespace Launcher;

public partial class MainWindow
{
    private LauncherConfig _config = new();
    private bool _configLoaded;
    private readonly ReleaseIndexService _indexService;
    private readonly PackageService _packages;
    private readonly PublishService _publisher;
    private readonly ObservableCollection<ReleaseChoice> _versions = [];
    private readonly ObservableCollection<ReleaseChoice> _platforms = [];
    private readonly ObservableCollection<ReleaseChange> _changes = [];
    private readonly ObservableCollection<ReleaseFileNode> _fileRoots = [];
    private bool _releaseBusy;
    private int _scanRevision;
    private string _suggestedVersion = string.Empty;

    private string ReleasesDirectory
    {
        get
        {
            if (!this._configLoaded)
            {
                throw new InvalidOperationException("Configure Config/publish.json first.");
            }

            return this._config.GetReleasesDirectory(AppContext.BaseDirectory);
        }
    }

    private void InitializeReleasePage()
    {
        this.VersionList.ItemsSource = this._versions;
        this.PlatformList.ItemsSource = this._platforms;
        this.ChangeTree.ItemsSource = this._fileRoots;
        this.TargetOS.ItemsSource = new[] { "windows", "linux", "darwin" };
        this.TargetArch.ItemsSource = new[] { "amd64", "arm64", "386", "arm" };
        this.TargetOS.SelectedItem = UpdateService.GetOS();
        this.TargetArch.SelectedItem = UpdateService.GetArch();
        this.PackageButton.IsEnabled = false;
        try
        {
            this._config = LauncherConfig.Load(AppContext.BaseDirectory);
            this._configLoaded = true;
            this.ShowHistory.IsChecked = this._config.ShowHistory;
            this.RefreshVersions();
        }
        catch (Exception error)
        {
            this.ReleaseStatus.Text = error.Message;
            this.PackageButton.IsEnabled = false;
            this.PublishButton.IsEnabled = false;
        }
    }

    private ReleaseChoice SelectedVersion()
    {
        return this.VersionList.SelectedItem as ReleaseChoice ??
            throw new InvalidOperationException("Select a version first.");
    }

    private ReleaseChoice SelectedPlatform()
    {
        return this.PlatformList.SelectedItem as ReleaseChoice ??
            throw new InvalidOperationException("Select a platform first.");
    }

    private void RefreshPublishButton()
    {
        this.PublishButton.IsEnabled = false;
        if (this.VersionList.SelectedItem is not ReleaseChoice version ||
            this.PlatformList.SelectedItem is not ReleaseChoice platform || !this._configLoaded)
        {
            return;
        }

        var (osName, arch) = ParsePlatform(platform.Name);
        var root = Path.Combine(this.ReleasesDirectory, version.Name);
        var archive = Path.Combine(root, "archives", osName, arch);
        var upgrade = this.BuildUpgrade.IsChecked == true;
        var full = this.BuildFull.IsChecked == true;
        this.PublishButton.IsEnabled = (upgrade || full) &&
            (!upgrade || (!platform.Published &&
                this._versionsService.HasPackage(this.ReleasesDirectory, version.Name, osName, arch) &&
                File.Exists(Path.Combine(archive, "upgrade.zip")))) &&
            (!full || (Directory.Exists(Path.Combine(root, "full", osName, arch, "files")) &&
                File.Exists(Path.Combine(archive, "full.zip"))));
    }

    private static (string OS, string Arch) ParsePlatform(string name)
    {
        var parts = name.Split('/');
        if (parts.Length != 2)
        {
            throw new InvalidDataException("invalid platform");
        }

        return (parts[0], parts[1]);
    }

    private void RefreshVersions()
    {
        var previousSuggestion = this.NewVersionName.Text;
        this._versions.Clear();
        var versions = this._versionsService.GetVersions(this.ReleasesDirectory);
        for (var index = versions.Count - 1; index >= 0; index--)
        {
            var version = versions[index];
            var manifest = this._versionsService.GetManifest(this.ReleasesDirectory, version);
            var published = false;
            foreach (var package in manifest.Packages)
            {
                if (package.Published)
                {
                    published = true;
                    break;
                }
            }

            this._versions.Add(new ReleaseChoice
            {
                Name = version,
                Published = published
            });
        }

        if (string.IsNullOrWhiteSpace(previousSuggestion) || previousSuggestion == this._suggestedVersion)
        {
            this._suggestedVersion = this._versionsService.GetNextVersion(this.ReleasesDirectory);
            this.NewVersionName.Text = this._suggestedVersion;
        }
    }

    private void RefreshVersions_OnClick(object? sender, RoutedEventArgs args)
    {
        try
        {
            this.RefreshVersions();
            this.ReleaseStatus.Text = "Version list refreshed.";
        }
        catch (Exception error)
        {
            this.ReleaseStatus.Text = error.Message;
        }
    }

    private void CreateVersion_OnClick(object? sender, RoutedEventArgs args)
    {
        if (this._releaseBusy)
        {
            return;
        }

        try
        {
            var version = this.NewVersionName.Text?.Trim() ?? string.Empty;
            this._versionsService.CreateVersion(this.ReleasesDirectory, version);
            this.NewVersionName.Text = string.Empty;
            this.RefreshVersions();
            foreach (var item in this._versions)
            {
                if (item.Name == version)
                {
                    this.VersionList.SelectedItem = item;
                    break;
                }
            }

            this.ReleaseStatus.Text = "Created " + version + ". Add a platform next.";
        }
        catch (Exception error)
        {
            this.ReleaseStatus.Text = error.Message;
        }
    }

    private void CreatePlatform_OnClick(object? sender, RoutedEventArgs args)
    {
        if (this._releaseBusy)
        {
            return;
        }

        try
        {
            var version = this.SelectedVersion();
            var osName = this.TargetOS.SelectedItem as string ?? string.Empty;
            var arch = this.TargetArch.SelectedItem as string ?? string.Empty;
            this._versionsService.AddPlatform(this.ReleasesDirectory, version.Name, osName, arch);
            this.RefreshPlatforms(version.Name);
            foreach (var item in this._platforms)
            {
                if (item.Name == osName + "/" + arch)
                {
                    this.PlatformList.SelectedItem = item;
                    break;
                }
            }

            this.ReleaseStatus.Text = "Created platform " + osName + "/" + arch + ".";
        }
        catch (Exception error)
        {
            this.ReleaseStatus.Text = error.Message;
        }
    }

    private async void DeleteVersions_OnClick(object? sender, RoutedEventArgs args)
    {
        if (this._releaseBusy)
        {
            return;
        }

        try
        {
            if (this.VersionList.SelectedItem is not ReleaseChoice selected)
            {
                this.ReleaseStatus.Text = "Select a version to delete.";
                return;
            }

            if (selected.Published)
            {
                this.ReleaseStatus.Text = "Published versions cannot be deleted.";
                return;
            }

            var names = new List<string> { selected.Name };
            if (!await this.ConfirmDeletionAsync("Delete version", names))
            {
                return;
            }

            this._releaseBusy = true;
            this.VersionList.IsEnabled = false;
            this.PlatformList.IsEnabled = false;
            var releases = this.ReleasesDirectory;
            var showHistory = this.ShowHistory.IsChecked == true;
            await Task.Run(() =>
            {
                try
                {
                    foreach (var name in names)
                    {
                        this._versionsService.DeleteVersion(releases, name);
                    }
                }
                finally
                {
                    this._indexService.RebuildLocalIndex(releases, showHistory, this._config.IndexTitle);
                }
            });
            this.RefreshVersions();
            this.ReleaseStatus.Text = "Deleted " + names.Count + " local unpublished version(s).";
        }
        catch (Exception error)
        {
            this.ReleaseStatus.Text = error.Message;
            this.RefreshVersions();
        }
        finally
        {
            this._releaseBusy = false;
            this.VersionList.IsEnabled = true;
            this.PlatformList.IsEnabled = true;
        }
    }

    private async void DeletePlatforms_OnClick(object? sender, RoutedEventArgs args)
    {
        if (this._releaseBusy)
        {
            return;
        }

        try
        {
            var version = this.SelectedVersion();
            if (this.PlatformList.SelectedItem is not ReleaseChoice selected)
            {
                this.ReleaseStatus.Text = "Select a platform to delete.";
                return;
            }

            if (selected.Published)
            {
                this.ReleaseStatus.Text = "Published platforms cannot be deleted.";
                return;
            }

            var names = new List<string> { selected.Name };
            if (!await this.ConfirmDeletionAsync("Delete platform: " + version.Name, names))
            {
                return;
            }

            this._releaseBusy = true;
            this.VersionList.IsEnabled = false;
            this.PlatformList.IsEnabled = false;
            var releases = this.ReleasesDirectory;
            var showHistory = this.ShowHistory.IsChecked == true;
            await Task.Run(() =>
            {
                try
                {
                    foreach (var name in names)
                    {
                        var (osName, arch) = ParsePlatform(name);
                        this._versionsService.DeletePlatform(releases, version.Name, osName, arch);
                    }
                }
                finally
                {
                    this._indexService.RebuildLocalIndex(releases, showHistory, this._config.IndexTitle);
                }
            });
            this.RefreshPlatforms(version.Name);
            this.ReleaseStatus.Text = "Deleted " + names.Count + " local unpublished platform(s).";
        }
        catch (Exception error)
        {
            this.ReleaseStatus.Text = error.Message;
        }
        finally
        {
            this._releaseBusy = false;
            this.VersionList.IsEnabled = true;
            this.PlatformList.IsEnabled = true;
        }
    }

    private async Task<bool> ConfirmDeletionAsync(string title, List<string> names)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = 260,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var content = new StackPanel { Margin = new Thickness(18), Spacing = 10 };
        content.Children.Add(new TextBlock { Text = "Only local unpublished content will be deleted." });
        content.Children.Add(new ScrollViewer
        {
            Height = 135,
            Content = new TextBlock { Text = string.Join(Environment.NewLine, names), TextWrapping = TextWrapping.Wrap }
        });
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        var cancel = new Button { Content = "Cancel" };
        var delete = new Button { Content = "Delete" };
        cancel.Click += (_, _) => dialog.Close(false);
        delete.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(cancel);
        buttons.Children.Add(delete);
        content.Children.Add(buttons);
        dialog.Content = content;
        return await dialog.ShowDialog<bool>(this);
    }

    private void RefreshPlatforms(string version)
    {
        this._platforms.Clear();
        var manifest = this._versionsService.GetManifest(this.ReleasesDirectory, version);
        foreach (var package in manifest.Packages)
        {
            this._platforms.Add(new ReleaseChoice
            {
                Name = package.OS + "/" + package.Arch,
                Published = package.Published
            });
        }
    }

    private void VersionList_OnSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        this.SelectedContext.Text = "Changed Files · Select a version and platform";
        this._scanRevision++;
        this.ScanIndicator.IsVisible = false;
        this._changes.Clear();
        this._fileRoots.Clear();
        this._platforms.Clear();
        this.PackageButton.IsEnabled = false;
        this.PublishButton.IsEnabled = false;
        if (this.VersionList.SelectedItem is ReleaseChoice version)
        {
            this.SelectedContext.Text = "Changed Files · " + version.Name + " · Select a platform";
            try
            {
                this.RefreshPlatforms(version.Name);
            }
            catch (Exception error)
            {
                this.ReleaseStatus.Text = error.Message;
            }
        }
    }

    private async void PlatformList_OnSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        this._scanRevision++;
        this.ScanIndicator.IsVisible = false;
        this._changes.Clear();
        this._fileRoots.Clear();
        this.PackageButton.IsEnabled = false;
        this.PublishButton.IsEnabled = false;
        if (this.PlatformList.SelectedItem is not ReleaseChoice platform ||
            this.VersionList.SelectedItem is not ReleaseChoice version)
        {
            return;
        }

        this.SelectedContext.Text = "Changed Files · " + version.Name + " · " + platform.Name;
        try
        {
            var (osName, arch) = ParsePlatform(platform.Name);
            this.RefreshPublishButton();
            await this.ScanChangesAsync();
        }
        catch (Exception error)
        {
            this.ReleaseStatus.Text = error.Message;
        }
    }

    private async void Scan_OnClick(object? sender, RoutedEventArgs args)
    {
        await this.ScanChangesAsync();
    }

    private async Task ScanChangesAsync()
    {
        var revision = ++this._scanRevision;
        this.ScanIndicator.IsVisible = true;
        this.PackageButton.IsEnabled = false;
        try
        {
            var version = this.SelectedVersion();
            var platform = this.SelectedPlatform();
            var (osName, arch) = ParsePlatform(platform.Name);
            var source = this._config.GetSourceDirectory(AppContext.BaseDirectory, osName, arch);
            var excludePaths = this._config.ExcludePaths;
            this.ReleaseStatus.Text = "Scanning " + source + " …";
            var changes = await Task.Run(() => this._packages.FindChanges(
                this.ReleasesDirectory, version.Name, source, osName, arch, excludePaths));
            if (revision != this._scanRevision)
            {
                return;
            }

            this._changes.Clear();
            this._fileRoots.Clear();
            foreach (var change in changes)
            {
                this._changes.Add(change);
                this.AddFileNode(change);
            }

            this.PackageButton.IsEnabled =
                (!platform.Published && changes.Count > 0 && this.BuildUpgrade.IsChecked == true) ||
                this.BuildFull.IsChecked == true;
            if (changes.Count == 0)
            {
                this.ReleaseStatus.Text = "No changes to package.";
            }
            else
            {
                this.ReleaseStatus.Text = "Found " + changes.Count + " changed file(s).";
            }
        }
        catch (Exception error)
        {
            if (revision == this._scanRevision)
            {
                this.ReleaseStatus.Text = error.Message;
            }
        }
        finally
        {
            if (revision == this._scanRevision)
            {
                this.ScanIndicator.IsVisible = false;
            }
        }
    }

    private void ChangeSelection_OnClick(object? sender, RoutedEventArgs args)
    {
        if (sender is CheckBox { DataContext: ReleaseFileNode node } checkbox)
        {
            node.SetSelected(checkbox.IsChecked == true);
        }

        this.PackageButton.IsEnabled = this.BuildFull.IsChecked == true &&
            this.PlatformList.SelectedItem is ReleaseChoice;
        foreach (var change in this._changes)
        {
            if (change.Selected && this.BuildUpgrade.IsChecked == true &&
                this.PlatformList.SelectedItem is ReleaseChoice { Published: false })
            {
                this.PackageButton.IsEnabled = true;
                break;
            }
        }

        if (this.BuildUpgrade.IsChecked == true)
        {
            this.PublishButton.IsEnabled = false;
        }
        else
        {
            this.RefreshPublishButton();
        }
        this.ReleaseStatus.Text = "File selection changed. Rebuild the update package.";
    }

    private void PackageOptions_OnClick(object? sender, RoutedEventArgs args)
    {
        this.PackageButton.IsEnabled = this.PlatformList.SelectedItem is ReleaseChoice platform &&
            (this.BuildFull.IsChecked == true ||
             (!platform.Published && this.BuildUpgrade.IsChecked == true && this._changes.Count > 0));
        this.RefreshPublishButton();
    }

    private void AddFileNode(ReleaseChange change)
    {
        var parts = change.Path.Split('/');
        var children = this._fileRoots;
        ReleaseFileNode? parent = null;
        for (var index = 0; index < parts.Length; index++)
        {
            ReleaseFileNode? node = null;
            if (index < parts.Length - 1)
            {
                foreach (var existing in children)
                {
                    if (existing.Name == parts[index] && existing.Change is null)
                    {
                        node = existing;
                        break;
                    }
                }
            }

            if (node is null)
            {
                node = new ReleaseFileNode { Name = parts[index], Parent = parent };
                children.Add(node);
            }

            if (index == parts.Length - 1)
            {
                node.Change = change;
            }

            parent = node;
            children = node.Children;
        }
    }

    private async void Package_OnClick(object? sender, RoutedEventArgs args)
    {
        if (this._releaseBusy)
        {
            return;
        }

        this._releaseBusy = true;
        this.PublishButton.IsEnabled = false;
        try
        {
            var version = this.SelectedVersion();
            var platform = this.SelectedPlatform();
            if (platform.Published && this.BuildUpgrade.IsChecked == true)
            {
                throw new InvalidOperationException("This version or platform cannot be packaged.");
            }

            var selected = new List<string>();
            foreach (var change in this._changes)
            {
                if (change.Selected)
                {
                    selected.Add(change.Path);
                }
            }

            var buildUpgrade = this.BuildUpgrade.IsChecked == true;
            var buildFull = this.BuildFull.IsChecked == true;
            if (!buildUpgrade && !buildFull)
            {
                this.ReleaseStatus.Text = "Select at least one package type.";
                return;
            }

            if (buildUpgrade && selected.Count == 0)
            {
                this.ReleaseStatus.Text = "Select changed files to package.";
                return;
            }

            var (osName, arch) = ParsePlatform(platform.Name);
            var source = this._config.GetSourceDirectory(AppContext.BaseDirectory, osName, arch);
            var excludePaths = this._config.ExcludePaths;
            this.ReleaseStatus.Text = "Building update package…";
            this.ReleaseProgress.IsVisible = true;
            await Task.Run(() => this._packages.BuildArchives(
                this.ReleasesDirectory, version.Name, source, osName, arch, selected,
                buildUpgrade, buildFull, excludePaths));
            this._indexService.WriteLocalIndex(
                this.ReleasesDirectory, version.Name, this.ShowHistory.IsChecked == true, this._config.IndexTitle);

            this.RefreshPublishButton();
            this.ReleaseStatus.Text = "Built ZIP archive(s) for " + version.Name + " · " + platform.Name + ".";
        }
        catch (Exception error)
        {
            this.ReleaseStatus.Text = error.Message;
        }
        finally
        {
            this.ReleaseProgress.IsVisible = false;
            this._releaseBusy = false;
        }
    }

    private async void Publish_OnClick(object? sender, RoutedEventArgs args)
    {
        if (this._releaseBusy)
        {
            return;
        }

        this._releaseBusy = true;
        this.PublishButton.IsEnabled = false;
        try
        {
            var version = this.SelectedVersion();
            var platform = this.SelectedPlatform();
            var (osName, arch) = ParsePlatform(platform.Name);
            var bucket = this._config.CosBucketURL;
            if (string.IsNullOrWhiteSpace(bucket))
            {
                bucket = this._config.ReleaseEndpoint;
            }

            this.ReleaseStatus.Text = "Publishing " + version.Name + " · " + platform.Name + " …";
            this.ReleaseProgress.IsVisible = true;
            var result = await this._publisher.PublishVersionAsync(
                this.ReleasesDirectory, version.Name, osName, arch,
                this._config.ReleaseEndpoint, bucket,
                this._config.SecretID, this._config.SecretKey, this._config.SessionToken,
                this.ShowHistory.IsChecked == true,
                CancellationToken.None, this.BuildUpgrade.IsChecked == true, this.BuildFull.IsChecked == true,
                this._config.IndexTitle);
            this.RefreshVersions();
            foreach (var item in this._versions)
            {
                if (item.Name == version.Name)
                {
                    this.VersionList.SelectedItem = item;
                    this.RefreshPlatforms(version.Name);
                    foreach (var choice in this._platforms)
                    {
                        if (choice.Name == platform.Name)
                        {
                            this.PlatformList.SelectedItem = choice;
                            break;
                        }
                    }

                    break;
                }
            }

            this.ReleaseStatus.Text = "Published: " + result.Uploaded + " uploaded, " + result.Skipped + " skipped.";
        }
        catch (Exception error)
        {
            this.ReleaseStatus.Text = error.Message;
        }
        finally
        {
            this.ReleaseProgress.IsVisible = false;
            this._releaseBusy = false;
            try
            {
                if (this.VersionList.SelectedItem is ReleaseChoice version &&
                    this.PlatformList.SelectedItem is ReleaseChoice platform)
                {
                    this.RefreshPublishButton();
                }
            }
            catch (Exception error)
            {
                this.PublishButton.IsEnabled = false;
                this.ReleaseStatus.Text = error.Message;
            }
        }
    }

    private void Preview_OnClick(object? sender, RoutedEventArgs args)
    {
        try
        {
            var path = Path.Combine(this.ReleasesDirectory, "index.html");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Build an update package first.", path);
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            this.ReleaseStatus.Text = error.Message;
        }
    }
}

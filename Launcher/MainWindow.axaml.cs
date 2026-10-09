using Avalonia.Controls;

using System;
using System.IO;
using System.Threading;
using Launcher.Updater;

namespace Launcher;

public partial class MainWindow : Window
{
    private static readonly System.Net.Http.HttpClient _client = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    private readonly UpdateService _updates = new(_client);
    private readonly VersionService _versionsService = new();
    private LauncherConfig _launchConfig = new();
    private bool _busy;

    public MainWindow()
    {
        this._indexService = new ReleaseIndexService(this._versionsService);
        this._packages = new PackageService(this._versionsService);
        this._publisher = new PublishService(_client, this._versionsService);
        InitializeComponent();
        this.LaunchTab.IsVisible = File.Exists(Path.Combine(AppContext.BaseDirectory, "Config", "launcher.json"));
        this.PublishTab.IsVisible = File.Exists(Path.Combine(AppContext.BaseDirectory, "Config", "publish.json"));
        if (this.LaunchTab.IsVisible)
        {
            this.MainTabs.SelectedItem = this.LaunchTab;
            try
            {
                this._launchConfig = LauncherConfig.LoadLaunch(AppContext.BaseDirectory);
            }
            catch (Exception error)
            {
                this.UpdateStatus.Text = error.Message;
            }
        }

        if (this.PublishTab.IsVisible)
        {
            if (!this.LaunchTab.IsVisible)
            {
                this.MainTabs.SelectedItem = this.PublishTab;
            }

            this.InitializeReleasePage();
        }
    }

    private async void Check_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (this._busy)
        {
            return;
        }

        this._busy = true;
        try
        {
            this.UpdateStatus.Text = "Checking for updates…";
            var result = await this._updates.CheckAsync(this._launchConfig.ReleaseEndpoint, System.Threading.CancellationToken.None);
            this.UpdateStatus.Text = "Latest stable version: " + result.Version;
        }
        catch (System.Exception error)
        {
            this.UpdateStatus.Text = error.Message;
        }
        finally
        {
            this._busy = false;
        }
    }

    private async void Get_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (this._busy)
        {
            return;
        }

        this._busy = true;
        try
        {
            this.UpdateStatus.Text = "Downloading and verifying…";
            var result = await this._updates.GetAsync(
                this._launchConfig.ReleaseEndpoint,
                null,
                AppContext.BaseDirectory,
                System.Threading.CancellationToken.None);
            this.UpdateStatus.Text = result.Status + ": " + result.Version + " (release/" + result.Version + ")";
        }
        catch (System.Exception error)
        {
            this.UpdateStatus.Text = error.Message;
        }
        finally
        {
            this._busy = false;
        }
    }

}

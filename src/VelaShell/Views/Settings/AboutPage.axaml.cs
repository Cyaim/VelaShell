using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Diagnostics;
using VelaShell.Services.Update;
using VelaShell.ViewModels;
using Avalonia.Interactivity;
using FireAndForget = VelaShell.Services.FireAndForget;

namespace VelaShell.Views.Settings;

/// <summary>关于页:展示版本、依赖项目及其许可证链接。</summary>
public partial class AboutPage : UserControl
{
    /// <summary>初始化关于页并加载 XAML 组件。</summary>
    public AboutPage() => InitializeComponent();

    private bool _showingChangelog;

    private void Changelog_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (Application.Current is App app && app.Services?.GetService<IReleaseNotesSource>() is { } source)
        {
            await ShowChangelogAsync(source);
        }
    });

    internal async Task ShowChangelogAsync(IReleaseNotesSource source)
    {
        if (_showingChangelog || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }
        _showingChangelog = true;
        try
        {
            if (DataContext is SettingsViewModel updates)
            {
                await new ReleaseNotesDialog(source, updates).ShowDialog(owner);
            }
            else
            {
                await new ReleaseNotesDialog(source).ShowDialog(owner);
            }
        }
        finally
        {
            _showingChangelog = false;
        }
    }

    private void ExportDiagnostics_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (DataContext is not SettingsViewModel viewModel || TopLevel.GetTopLevel(this) is not { } top
            || sender is not Button button)
        {
            return;
        }
        IStorageFile? file = await top.StorageProvider.SaveFilePickerAsync(new()
        {
            Title = Strings.Get("SetAbout_ExportDiagnosticsDialogTitle"),
            SuggestedFileName = $"velashell-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            SuggestedStartLocation = await StorageDefaults.DownloadsAsync(top),
            DefaultExtension = "zip"
        });
        if (file?.TryGetLocalPath() is not { Length: > 0 } path)
        {
            return; // 用户取消了,不算失败,也不用回显。
        }
        button.IsEnabled = false;
        try
        {
            DiagnosticBundleResult result = await viewModel.ExportDiagnosticsAsync(path);
            viewModel.DiagnosticsStatus = Strings.Format("SetAbout_ExportDiagnosticsDone", path, result.LogFiles);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            viewModel.DiagnosticsStatus = Strings.Format("SetAbout_ExportDiagnosticsFailed", ex.Message);
        }
        finally
        {
            button.IsEnabled = true;
        }
    });

    /// <summary>点击依赖项目名/许可证时,用系统默认浏览器打开对应链接(URL 存放在控件 Tag)。</summary>
    private void OnOpenLink(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (sender is not Control { Tag: string url } || string.IsNullOrWhiteSpace(url))
        {
            return;
        }
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            await top.Launcher.LaunchUriAsync(uri);
        }
    });
}

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

    /// <summary>导出诊断包:先勾要另外收进包的几类日志(默认一个都不勾),再选保存位置。</summary>
    private void ExportDiagnostics_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (DataContext is not SettingsViewModel viewModel || TopLevel.GetTopLevel(this) is not Window top
            || sender is not Button button)
        {
            return;
        }
        var picker = new DiagnosticExportView
        {
            DataContext = new DiagnosticExportViewModel(await viewModel.FindDiagnosticLogOptionsAsync())
        };
        if (await picker.ShowDialog<IReadOnlyList<DiagnosticLogOption>?>(top) is not { } optional)
        {
            return; // 取消了。
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
        await ExportDiagnosticsToAsync(viewModel, button, path, optional);
    });

    /// <summary>勾好、选好位置之后的那一段:写包,结果回显在按钮下面。测试从这里进,绕开勾选框与系统的保存对话框。</summary>
    internal static async Task ExportDiagnosticsToAsync(SettingsViewModel viewModel, Button button, string path,
        IReadOnlyList<DiagnosticLogOption> optional)
    {
        // 上一次的「已保存到 …」先清掉:这次还没出结果、或者失败了,都不能让旧路径留在页面上冒充这一次
        viewModel.DiagnosticsStatus = string.Empty;
        button.IsEnabled = false;
        try
        {
            DiagnosticBundleResult result = await viewModel.ExportDiagnosticsAsync(path, optional);
            viewModel.DiagnosticsStatus = Strings.Format("SetAbout_ExportDiagnosticsDone", path, result.LogFiles);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 不只 IO 错:读连接配置、脱敏也会抛别的。漏给 FireAndForget 只进日志,用户看到的是点了没反应
            viewModel.DiagnosticsStatus = Strings.Format("SetAbout_ExportDiagnosticsFailed", ex.Message);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using VelaShell.Core.Data;
using VelaShell.Core.Recording;
using VelaShell.Services;
using VelaShell.ViewModels;

namespace VelaShell.Views.Settings;

/// <summary>安全审计设置页:提供会话录制回放、审计日志等安全审计相关入口。</summary>
public partial class SecurityAuditPage : UserControl
{
    /// <summary>回放中心窗口尺寸与位置在文档存储里的键(见 <see cref="WindowLayoutStore" />)。</summary>
    private const string RecordingPlayerLayoutKey = "recordingPlayer";

    /// <summary>审计日志窗口尺寸与位置在文档存储里的键。</summary>
    private const string AuditLogLayoutKey = "auditLog";

    /// <summary>初始化安全审计设置页并加载 XAML 组件。</summary>
    public SecurityAuditPage() => InitializeComponent();

    /// <summary>打开会话录制回放中心(设计 NceE6),非模态独立窗口。</summary>
    private void OpenRecordingPlayer_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(OpenRecordingPlayerAsync);

    private async Task OpenRecordingPlayerAsync()
    {
        if (Application.Current is not App app || app.Services is null)
        {
            return;
        }
        if (app.Services.GetService<ISessionRecordingStore>() is not { } store)
        {
            return;
        }
        var viewModel = new RecordingPlayerViewModel(store, app.Services.GetService<ISettingsService>());
        var window = new RecordingPlayerView { DataContext = viewModel };
        await ShowRememberedAsync(app.Services, RecordingPlayerLayoutKey, window);
        _ = viewModel.InitializeAsync();
    }

    /// <summary>打开审计日志窗口,非模态(与回放中心同样的开法)。</summary>
    private void OpenAuditLog_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(OpenAuditLogAsync);

    private async Task OpenAuditLogAsync()
    {
        if (Application.Current is not App { Services: { } services }
            || services.GetService<IAuditLogService>() is not { } auditLog)
        {
            return;
        }
        var viewModel = new AuditLogViewModel(auditLog, services.GetService<ISessionRepository>());
        var window = new AuditLogView { DataContext = viewModel };
        // 「打开配置」借主窗口的编辑框:保存后要刷新会话树、按「连接」要开标签,这些都在主窗口那边。
        if ((Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is MainWindow main)
        {
            viewModel.OpenProfile = profile => main.EditProfileAsync(profile, window);
        }
        await ShowRememberedAsync(services, AuditLogLayoutKey, window);
        FireAndForget.Run(() => viewModel.LoadAsync());
    }

    /// <summary>
    /// 套上次的尺寸与位置(多屏用户摆到副屏上的,下次还在那儿;那块屏没了就照窗口自己的启动位置居中),
    /// 再以设置窗口为属主非模态地打开。
    /// </summary>
    private async Task ShowRememberedAsync(IServiceProvider services, string layoutKey, Window window)
    {
        if (services.GetService<WindowLayoutStore>() is { } layoutStore)
        {
            await layoutStore.RememberAsync(layoutKey, window);
        }
        // 读布局那一下之后再取属主:设置窗口可能已经关掉了。
        if (TopLevel.GetTopLevel(this) is Window { IsVisible: true } owner)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
    }
}

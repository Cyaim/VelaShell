using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Plugins;

namespace VelaShell.Views;

/// <summary>
/// 插件敏感能力的授权弹窗:展示插件、目标与内容预览,给出
/// 终端回写与开会话提供仅本次 / 本次运行 / 始终 / 拒绝四种选择。
/// 文件上传仅允许批准当前任务或拒绝，预览固定的本地源、远端目标和覆盖策略。
/// </summary>
public partial class PluginPermissionDialog : Window
{
    /// <summary>设计器构造;带参构造也经由它初始化组件并按平台装上对话框外框。</summary>
    public PluginPermissionDialog()
    {
        InitializeComponent();
        WindowChrome.Apply(this, WindowChromeKind.Dialog);
    }

    private PluginPermissionDialog(string title, string message, string preview, string iconKey) : this()
    {
        TitleText.Text = title;
        MessageText.Text = message;
        PreviewText.Text = preview;
        if (Avalonia.Application.Current?.TryFindResource(iconKey, out object? icon) == true
            && icon is Avalonia.Media.Geometry geometry)
        {
            HeaderIcon.Data = geometry;
        }
        OnceButton.Content = Strings.Get("PluginPerm_AllowOnce");
        SessionButton.Content = Strings.Get("PluginPerm_AllowSession");
        AlwaysButton.Content = Strings.Get("PluginPerm_AllowAlways");
        DenyButton.Content = Strings.Get("PluginPerm_Deny");
    }

    /// <summary>终端回写:在主窗口上模态弹出,返回用户裁决(取消/关闭 = 拒绝)。</summary>
    public static Task<PluginPermissionDecision> AskAsync(string pluginId, string sessionLabel, string inputPreview)
        => ShowAsync(Strings.Get("PluginPerm_Title"),
            Strings.Format("PluginPerm_Message", pluginId, sessionLabel), inputPreview, "Icon.terminal");

    /// <summary>
    /// 按已保存配置开会话:同一个窗口,预览框里放插件给的<b>理由</b>。
    /// </summary>
    /// <remarks>
    /// 理由原样显示,一个字都不改写 —— 用户就是照着它决定点不点同意的。
    /// 插件写成"插件需要连接"这种废话,后果由它自己承担:确认框会照实显示,
    /// 而不是由宿主替它补一句像样的说明。
    /// </remarks>
    public static Task<PluginPermissionDecision> AskSessionOpenAsync(string pluginId, string target, string reason)
        => ShowAsync(Strings.Get("PluginPermSession_Title"),
            Strings.Format("PluginPermSession_Message", pluginId, target), reason, "Icon.plug");

    /// <summary>下载可信确认只批准当前任务，不授予其它路径或后续下载的永久权限。</summary>
    public static Task<PluginPermissionDecision> AskFileDownloadAsync(string pluginId, string sessionLabel,
        string remotePath, string localPath, bool overwrite, CancellationToken cancellationToken = default)
        => ShowAsync(Strings.Get("PluginPermDownload_Title"),
            Strings.Format("PluginPermDownload_Message", EscapeUploadField(pluginId), EscapeUploadField(sessionLabel)),
            Strings.Format("PluginPermDownload_Preview", EscapeUploadField(remotePath), EscapeUploadField(localPath),
                Strings.Get(overwrite ? "PluginPermUpload_Replace" : "PluginPermUpload_NoReplace")),
            "Icon.download", uploadOnly: true, cancellationToken, downloadOnly: true);

    /// <summary>上传可信确认只批准当前任务，不授予其它路径或后续上传的永久权限。</summary>
    public static Task<PluginPermissionDecision> AskFileUploadAsync(string pluginId, string sessionLabel,
        string localPath, string remotePath, bool overwrite, CancellationToken cancellationToken = default)
        => ShowAsync(Strings.Get("PluginPermUpload_Title"),
            Strings.Format("PluginPermUpload_Message", EscapeUploadField(pluginId), EscapeUploadField(sessionLabel)),
            Strings.Format("PluginPermUpload_Preview", EscapeUploadField(localPath), EscapeUploadField(remotePath),
                Strings.Get(overwrite ? "PluginPermUpload_Replace" : "PluginPermUpload_NoReplace")),
            "Icon.upload", uploadOnly: true, cancellationToken);

    private static async Task<PluginPermissionDecision> ShowAsync(string title, string message, string preview,
        string iconKey, bool uploadOnly = false, CancellationToken cancellationToken = default, bool downloadOnly = false)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return await Dispatcher.UIThread.InvokeAsync(() => ShowAsync(title, message, preview, iconKey, uploadOnly, cancellationToken, downloadOnly));
        }
        if (cancellationToken.IsCancellationRequested)
            return PluginPermissionDecision.Deny;
        if ((Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow is not { } owner)
        {
            return PluginPermissionDecision.Deny;
        }
        var dialog = new PluginPermissionDialog(title, message, preview, iconKey);
        if (uploadOnly) dialog.ConfigureUploadOnly();
        if (downloadOnly) dialog.OnceButton.Content = Strings.Get("PluginPermDownload_AllowOnce");
        Task<PluginPermissionDecision> decision = dialog.ShowDialog<PluginPermissionDecision>(owner);
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            Dispatcher.UIThread.Post(() =>
            {
                if (dialog.IsVisible) dialog.Close(PluginPermissionDecision.Deny);
            }));
        PluginPermissionDecision result = await decision;
        return cancellationToken.IsCancellationRequested ? PluginPermissionDecision.Deny : result;
    }

    // 仅转义可信确认的显示值，不改变宿主实际审批的源路径和目标路径。
    internal static string EscapeUploadField(string value)
    {
        var result = new StringBuilder(value.Length + 2);
        result.Append('"');
        foreach (char c in value)
        {
            if (c is '\\' or '"') result.Append('\\').Append(c);
            else if (char.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format
                     or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                result.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            else result.Append(c);
        }
        return result.Append('"').ToString();
    }

    internal void ConfigureUploadOnly()
    {
        PreviewScroll.MaxHeight = 240;
        PreviewScroll.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        PreviewText.TextWrapping = Avalonia.Media.TextWrapping.NoWrap;
        MessageScroll.MaxHeight = 120;
        SessionButton.IsVisible = false;
        AlwaysButton.IsVisible = false;
        AlwaysButton.IsDefault = false;
        OnceButton.Content = Strings.Get("PluginPermUpload_AllowOnce");
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            this.BeginWindowMoveDrag(e);
        }
    }

    private void Once_Click(object? sender, RoutedEventArgs e) => Close(PluginPermissionDecision.AllowOnce);
    private void Session_Click(object? sender, RoutedEventArgs e) => Close(PluginPermissionDecision.AllowSession);
    private void Always_Click(object? sender, RoutedEventArgs e) => Close(PluginPermissionDecision.AllowAlways);
    private void Deny_Click(object? sender, RoutedEventArgs e) => Close(PluginPermissionDecision.Deny);

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close(PluginPermissionDecision.Deny);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}

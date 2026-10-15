using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;

namespace VelaShell.Views;

/// <summary>宿主专用 sudo 弹窗，不接受远端可伪造的来源或密码提示文字。</summary>
public static class SudoPromptDialog
{
    /// <summary>建立包含真实来源、目标及完整 root 命令的可信内容。</summary>
    public static StackPanel CreateContent(SudoExecutionContext context, string command, int? attempt, out TextBox? password)
    {
        StackPanel content = new() { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = Strings.Format("Sudo_Source", context.SourcePluginId), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = Strings.Format("Sudo_Target", context.Target), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = Strings.Get("Sudo_RootWarning"), TextWrapping = TextWrapping.Wrap });
        // 只读、可选择并滚动的完整命令；不省略可能藏着重定向的末尾。
        content.Children.Add(new TextBox { Text = command, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 240 });
        password = null;
        if (attempt.HasValue)
        {
            if (attempt.Value > 1)
            {
                content.Children.Add(new TextBlock { Text = Strings.Get("Sudo_Retry"), TextWrapping = TextWrapping.Wrap });
            }
            content.Children.Add(new TextBlock { Text = Strings.Get("Sudo_Password"), TextWrapping = TextWrapping.Wrap });
            password = new TextBox { Name = "SudoPasswordInput", PasswordChar = '●', AcceptsReturn = false, IsUndoEnabled = false, MaxLength = 4096 };
            AutomationProperties.SetName(password, Strings.Get("Sudo_Password"));
            TextBox input = password;
            input.Loaded += (_, _) => input.Focus();
            content.Children.Add(input);
            content.Children.Add(new TextBlock { Text = Strings.Get("Sudo_NoStorage"), TextWrapping = TextWrapping.Wrap });
        }
        return content;
    }

    /// <summary>逐次审批，即使 sudo 不需要密码也必须取得批准。</summary>
    public static Task<bool> ConfirmAsync(Window owner, SudoExecutionContext context, string command, CancellationToken cancellationToken) =>
        ShowConfirmationAsync(owner, context, command, cancellationToken);

    private static async Task<bool> ShowConfirmationAsync(Window owner, SudoExecutionContext context, string command, CancellationToken cancellationToken)
    {
        StackPanel content = CreateContent(context, command, null, out _);
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        content.Loaded += (_, _) =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                (TopLevel.GetTopLevel(content) as Window)?.Close(false);
            }
        };
        await using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            Dispatcher.UIThread.Post(() => (TopLevel.GetTopLevel(content) as Window)?.Close(false)));
        return await MessageDialog.ShowCustomAsync(owner, Strings.Get("Sudo_Title"), content,
            Strings.Get("Sudo_Approve"), kind: MessageDialogKind.Warning) && !cancellationToken.IsCancellationRequested;
    }

    /// <summary>支持 UTF-8 的遮罩输入，关闭或取出后清空控件及撤销记录。</summary>
    public static async Task<SudoPassword?> RequestPasswordAsync(Window owner, SudoExecutionContext context, string command, int attempt, CancellationToken cancellationToken)
    {
        StackPanel content = CreateContent(context, command, attempt, out TextBox? password);
        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        content.Loaded += (_, _) =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                (TopLevel.GetTopLevel(content) as Window)?.Close(false);
            }
        };
        await using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            Dispatcher.UIThread.Post(() => (TopLevel.GetTopLevel(content) as Window)?.Close(false)));
        try
        {
            bool confirmed = await MessageDialog.ShowCustomAsync(owner, Strings.Get("Sudo_Title"), content,
                Strings.Get("Sudo_Submit"), kind: MessageDialogKind.Question);
            if (!confirmed || cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            try
            {
                return new SudoPassword((password!.Text ?? string.Empty).AsSpan());
            }
            catch (ArgumentException)
            {
                // 不支持的控制字符直接拒绝，绝不悄悄改写密码再发送。
                return null;
            }
        }
        finally
        {
            password!.Text = string.Empty;
            password.IsUndoEnabled = false;
        }
    }
}

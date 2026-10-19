using System.Globalization;
using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;

namespace VelaShell.Views;

/// <summary>宿主专用 sudo 弹窗，不接受远端可伪造的来源或密码提示文字。</summary>
public static class SudoPromptDialog
{
    /// <summary>单行命令超过这个长度（文本元素）就在命令框下注明行数与长度，提醒滚动看完。</summary>
    internal const int LongCommandLength = 400;

    /// <summary>
    /// 把命令里看不见的字符写成 <c>⟨U+XXXX⟩</c>：控制字符（换行、制表符除外）、格式字符（双向控制、零宽字符等）、
    /// 行/段分隔符、普通空格以外的空白，以及几个常被拿来冒充空白的「空字母」。
    /// </summary>
    internal static string RevealCommand(string command, out int hidden)
    {
        StringBuilder shown = new(command.Length);
        hidden = 0;
        foreach (Rune rune in command.EnumerateRunes())
        {
            if (IsInvisible(rune))
            {
                hidden++;
                shown.Append(CultureInfo.InvariantCulture, $"⟨U+{rune.Value:X4}⟩");
            }
            else
            {
                shown.Append(rune.ToString());
            }
        }
        return shown.ToString();
    }

    private static bool IsInvisible(Rune rune) => rune.Value switch
    {
        '\n' or '\t' or ' ' => false,
        0x115F or 0x1160 or 0x3164 or 0xFFA0 or 0x2800 => true,
        _ => Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.SpaceSeparator
    };

    /// <summary>建立包含真实来源、目标及完整 root 命令的可信内容。</summary>
    public static StackPanel CreateContent(SudoExecutionContext context, string command, int? attempt, out TextBox? password)
    {
        StackPanel content = new() { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = Strings.Format("Sudo_Source", context.SourcePluginId), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = Strings.Format("Sudo_Target", context.Target), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = Strings.Get("Sudo_RootWarning"), TextWrapping = TextWrapping.Wrap });
        // 只读、可选择并滚动的完整命令；不省略可能藏着重定向的末尾。
        // 控制字符、双向控制符与零宽字符原样放进去是看不见的（U+202E 能把末尾倒过来显示），一律写成 ⟨U+XXXX⟩。
        string shown = RevealCommand(command, out int hidden);
        content.Children.Add(new TextBox { Name = "SudoCommandText", Text = shown, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 240 });
        int lines = command.AsSpan().Count('\n') + 1;
        int length = new StringInfo(command).LengthInTextElements;
        if (lines > 1 || length > LongCommandLength)
        {
            // 几百个换行就能把恶意的末尾推出可视区：先告诉用户还有多少没看到。
            content.Children.Add(new TextBlock { Text = Strings.Format("Sudo_CommandSize", lines, length), TextWrapping = TextWrapping.Wrap });
        }
        if (hidden > 0)
        {
            content.Children.Add(new TextBlock { Text = Strings.Format("Sudo_CommandHidden", hidden), TextWrapping = TextWrapping.Wrap });
        }
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
        // 审批框会在 Bridge/MCP 或免确认模式下异步弹出，用户手上可能正往终端里敲回车：
        // 回车与初始焦点都落在取消上，批准必须是一次有意的点击或 Tab 过去再确认。
        return await MessageDialog.ShowCustomAsync(owner, Strings.Get("Sudo_Title"), content,
            Strings.Get("Sudo_Approve"), kind: MessageDialogKind.Warning, cancelIsDefault: true) && !cancellationToken.IsCancellationRequested;
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

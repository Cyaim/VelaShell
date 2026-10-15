using VelaShell.Core.Ssh;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 认证时服务端发来的横幅(法律声明、PAM 的「密码将于 3 天后过期」):连接时收下,开第一个 shell 时作为提示写进终端。
/// </summary>
/// <remarks>
/// 曾经宿主不设 <c>BannerHandler</c>,横幅被静默丢掉 —— 而那常常正是服务端唯一一次告诉用户「密码快过期了」。
/// 横幅来自<b>未认证</b>的对端,是注入面:逐行把控制字符与双向控制符换成 ?(终端转义序列就此失效,规则是库的 PeerText)再交出去,
/// 行数与行长都有上限。跳板链上每一跳的横幅都收,按到达的先后。
/// <para>
/// 版本交换之前的前导行(有的设备在标识串之前就打印法律声明、维护公告,规格 02 §3)也收进来,
/// 排在那一跳认证横幅的前面 —— 曾经库把它们收集起来却没人接,用户看不到。
/// </para>
/// <para>
/// 服务端标着「一定要给用户看」的调试消息(<c>SSH_MSG_DEBUG</c> 的 always_display,规格 05 §八)同样收进来:
/// OpenSSH 的服务端用它说明 authorized_keys 里的限制之类。开过 shell 之后才到的不再显示(没有地方摆)。
/// </para>
/// </remarks>
public sealed class SshServerBanners
{
    /// <summary>最多交出几行(多出来的折成一行「…」)。</summary>
    internal const int MaxLines = 64;

    /// <summary>每行最多多少字符。</summary>
    internal const int MaxLineLength = 512;

    /// <summary>最多存几段横幅(只建链不开 shell 时没人来取,别无限攒)。</summary>
    internal const int MaxTexts = 16;

    private readonly List<string> _texts = [];

    /// <summary>当成 <c>SshConnectionOptions.BannerHandler</c> 用。</summary>
    public ValueTask OnBannerAsync(string text, CancellationToken _)
    {
        lock (_texts)
        {
            if (_texts.Count < MaxTexts)
            {
                _texts.Add(TailscaleSshCheckUri.RedactVerificationUris(text));
            }
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>当成 <c>SshConnectionOptions.PreAuthBannerHandler</c> 用:标识串之前的前导行,当成一段横幅收下。</summary>
    public ValueTask OnPreAuthBannerAsync(IReadOnlyList<string> lines, CancellationToken cancellationToken) =>
        OnBannerAsync(string.Join('\n', lines), cancellationToken);

    /// <summary>取走已收到的横幅,一行一条提示;只交出一次(之后再开的 shell 不重复显示)。</summary>
    public IReadOnlyList<ShellStreamNotice> TakeNotices()
    {
        string[] texts;
        lock (_texts)
        {
            texts = [.. _texts];
            _texts.Clear();
        }

        List<ShellStreamNotice> notices = [];
        foreach (string text in texts)
        {
            foreach (string raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                string line = KeyboardInteractiveResponder.Clean(raw, MaxLineLength);
                if (line.Length == 0)
                {
                    continue;
                }
                if (notices.Count == MaxLines)
                {
                    notices.Add(new("…", IsWarning: false));
                    return notices;
                }
                notices.Add(new(line, IsWarning: false));
            }
        }
        return notices;
    }
}

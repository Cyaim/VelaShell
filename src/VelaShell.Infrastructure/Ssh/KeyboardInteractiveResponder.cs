using System.Text.RegularExpressions;
using VelaShell.Core.Net;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 一次认证里的 keyboard-interactive 应答:认得出的口令提示用已有的密码答,其余弹框问用户。
/// </summary>
/// <remarks>
/// <para>
/// 最常见的 2FA 形状是 PAM 先问 <c>Password:</c> 再问 <c>Verification code:</c>,两轮各一条不回显提示。
/// 库自带的「密码兼答 keyboard-interactive」只看形状不看内容,会把密码也填进验证码那一轮 ——
/// 所以接上这个应答器时要关掉那一路(见 <see cref="SshConnectionAssembler.BuildCredentialsAsync" />)。
/// </para>
/// <para>
/// <b>私钥 / 证书 / agent 那几路不回退到口令</b>(<paramref name="password" /> 为 <see langword="null" />):
/// 只有一条的口令提示直接答空串让服务端拒掉,只有验证码之类的才弹框 ——
/// 那是 <c>AuthenticationMethods publickey,keyboard-interactive</c>(钥 + 动态码)的第二步。
/// 钥被拒之后冒出一个密码框,与「只用用户选的那一种认证方式」相悖。
/// </para>
/// </remarks>
/// <param name="prompt">界面。</param>
/// <param name="target">给用户看的连接目标。</param>
/// <param name="password">用户选的是密码认证时它的密码(可能为空串);其余认证方式为 <see langword="null" />。</param>
internal sealed partial class KeyboardInteractiveResponder(IKeyboardInteractivePrompt prompt, string target, string? password)
{
    private const int MaxNameLength = 128;
    private const int MaxInstructionLength = 2048;
    private const int MaxPromptLength = 256;

    private bool _passwordUsed;
    private string? _pendingNotice;

    /// <summary>按连接信息建应答器:密码认证把它的密码交给应答器代答口令提示,其余认证方式不代答。</summary>
    public static KeyboardInteractiveResponder For(Core.Models.ConnectionInfo info, IKeyboardInteractivePrompt prompt) =>
        new(prompt, HostAddress.FormatTarget(info.Username, info.Host, info.Port),
            info.AuthMethod == Core.Models.AuthMethod.Password ? info.Password ?? "" : null);

    /// <summary>包成库的凭据。</summary>
    public KeyboardInteractiveCredential ToCredential() => new(RespondAsync, "keyboard-interactive");

    /// <summary>用户在「修改密码」框上点了取消(装配处据此报「已取消修改密码」而不是「已取消两步验证」)。</summary>
    public bool PasswordChangeCancelled { get; private set; }

    /// <summary>
    /// 服务端要求先改密码(规格 04 §5.1):弹框让用户把新密码输两遍,对上了交给库(<see cref="PasswordCredential.NewPasswordProvider" />)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 协议里没有「再输一次」这一步,输错了的新密码会直接成为账户的密码 —— 所以两遍对不上(或空着)就再问,不交给库。
    /// 用的是动态码那个框(两条不回显的提示),标题、说明换成改密码的。
    /// </para>
    /// <para>
    /// 用户点了取消与动态码那一问同一个口径:抛 <see cref="OperationCanceledException" />,库以 <c>Aborted</c> 结束。
    /// 改成之后,连接里保存着的旧密码就过时了:下次用它登录被拒,走登录框重输、存回的那条路。框里先说一声。
    /// </para>
    /// </remarks>
    public async ValueTask<string?> AskNewPasswordAsync(SshPasswordChangeRequest request, CancellationToken cancellationToken)
    {
        string server = Clean(request.Prompt, MaxInstructionLength);
        string notice = Strings.Get(request.Attempt == 1 ? "SshPwdChange_Required" : "SshPwdChange_Rejected");
        KeyboardInteractiveField[] fields =
        [
            new(Strings.Get("SshPwdChange_NewPassword"), Echo: false),
            new(Strings.Get("SshPwdChange_ConfirmPassword"), Echo: false),
        ];

        while (true)
        {
            string instruction = server.Length == 0 ? notice : $"{notice}\n{server}";
            IReadOnlyList<string>? answers = await prompt
                .AskAsync(new(target, Strings.Get("SshPwdChange_Title"), instruction, fields), cancellationToken)
                .ConfigureAwait(false);
            if (answers is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PasswordChangeCancelled = true;
                throw new OperationCanceledException(Strings.Get("SshErr_PasswordChangeCancelled"));
            }
            if (answers is [{ Length: > 0 } first, { } second] && first == second)
            {
                return first;
            }
            notice = Strings.Get("SshPwdChange_Mismatch");
        }
    }

    /// <summary>应答一轮询问。</summary>
    /// <exception cref="OperationCanceledException">
    /// 用户在框上点了取消。库据此以 <c>Aborted</c> 结束这次连接(调用方没取消、认证计时器也没到点的取消,
    /// 就是回调自己不连了,规格 08 §2.1),装配处再把它报成「已取消」—— 而不是认证失败或超时。
    /// </exception>
    public async ValueTask<IReadOnlyList<string>> RespondAsync(SshKeyboardChallenge challenge, CancellationToken cancellationToken)
    {
        if (challenge.IsInformationalOnly)
        {
            // 纯展示的一轮不弹框(规格 04 §6.4),说明攒到下一个要输入的框里一起显示 ——
            // 那往往就是「请在手机上确认」之后紧跟着的那一问。
            string notice = Clean(challenge.Instruction, MaxInstructionLength);
            if (notice.Length > 0)
            {
                _pendingNotice = _pendingNotice is null ? notice : $"{_pendingNotice}\n{notice}";
            }
            return [];
        }

        if (challenge.Prompts is [{ Echo: false } only] && LooksLikePasswordPrompt(only.Text))
        {
            if (password is null)
            {
                return [string.Empty];
            }
            // 只代答一次:同一次认证里再问口令,说明刚才那个不对,交给用户重输。
            if (!_passwordUsed && password.Length > 0)
            {
                _passwordUsed = true;
                return [password];
            }
        }

        string instruction = Clean(challenge.Instruction, MaxInstructionLength);
        if (_pendingNotice is not null)
        {
            instruction = instruction.Length == 0 ? _pendingNotice : $"{_pendingNotice}\n{instruction}";
            _pendingNotice = null;
        }
        KeyboardInteractiveField[] fields =
        [
            .. challenge.Prompts.Select(p => new KeyboardInteractiveField(Clean(p.Text, MaxPromptLength), p.Echo))
        ];
        IReadOnlyList<string>? answers = await prompt
            .AskAsync(new(target, Clean(challenge.Name, MaxNameLength), instruction, fields), cancellationToken)
            .ConfigureAwait(false);
        if (answers is null)
        {
            // 令牌触发(关了标签、认证超时)带着令牌抛,库按它自己的口径处理;
            // 令牌没触发的取消就是用户在框上点了取消 —— 库认得出这一种(报 Aborted),不必在这里另记一笔。
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(Strings.Get("SshErr_KbdAuthCancelled"));
        }
        // 条数对不上时库会报协议错误并卡在半截 —— 界面层的错不该变成一句看不懂的协议异常。
        return answers.Count == fields.Length
            ? answers
            : throw new InvalidOperationException($"keyboard-interactive: expected {fields.Length} answers, got {answers.Count}.");
    }

    /// <summary>是不是一条单纯要口令的提示(带验证码字样的不算:那要的往往是「口令 + 动态码」拼起来的串)。</summary>
    internal static bool LooksLikePasswordPrompt(string text) =>
        PasswordWords().IsMatch(text) && !OneTimeCodeWords().IsMatch(text);

    /// <summary>
    /// 净化来自尚未认证的对端的文字：规则用库的 <see cref="PeerText.Sanitize"/>（控制字符、<c>DEL</c>、C1 控制码与双向文本控制符换成 <c>?</c>）。
    /// </summary>
    /// <remarks>
    /// 曾经这里另写了一份规则（把它们删掉），与库的那一份各管各的 —— 库把净化器公开出来，正是为了使用者不必各写一份。
    /// 换成 <c>?</c> 而不是删掉，被塞了控制字符这件事在界面上看得见。这里只多做两件库不做的事：
    /// 保留换行（多行的说明要分行显示）、制表符换成空格；再整体去掉首尾空白、限长。
    /// </remarks>
    internal static string Clean(string text, int maxLength)
    {
        string[] lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\t', ' ')
            .Split('\n');
        string cleaned = string.Join('\n', lines.Select(line => PeerText.Sanitize(line, line.Length))).Trim();
        return cleaned.Length <= maxLength ? cleaned : string.Concat(cleaned.AsSpan(0, maxLength - 1), "…");
    }

    [GeneratedRegex(@"\bpass(word|phrase)?\b|密码|口令|密碼|パスワード|비밀번호|암호", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PasswordWords();

    [GeneratedRegex(@"\b(otp|totp|token|code|verification|one[- ]?time|pin|2fa|mfa)\b|验证码|動態|动态|令牌|一次性|驗證碼|認証コード|ワンタイム|인증 ?코드|일회용", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OneTimeCodeWords();
}

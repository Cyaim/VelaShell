using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Infrastructure.Ssh;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Forwarding;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Sftp;

namespace VelaShell.Core.Tests.Ssh;

/// <summary>
/// 锁定 <see cref="SshInterop.Translate" /> 的映射:调用方主动取消必须保留
/// <see cref="OperationCanceledException" />,不得被翻译为超时;各类库异常要落到对的
/// Core 中立类型上 —— 上层的自动重连与错误提示全靠这层分流。
/// </summary>
/// <remarks>
/// 上一版这里只能测取消那两条:底层库的异常类型构造函数都是 <c>internal</c>,
/// 造不出实例,映射只能靠 switch 的编译期类型检查"保证"——
/// 而那保证不了枚举值落错分支。现在异常是公开可构造的,映射能逐条断言。
/// </remarks>
[TestClass]
public sealed class SshInteropTests
{
    // ------------------------------------------------------------ 取消 / 超时

    [TestMethod]
    public void Translate_CallerCancelled_ReturnsNull_PreservingCancellationSemantics()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Exception? translated = SshInterop.Translate(new OperationCanceledException(cts.Token), cts.Token);

        Assert.IsNull(translated, "调用方主动取消时应原样上抛 OperationCanceledException,而不是翻译成超时。");
    }

    [TestMethod]
    public void Translate_CallerCancelled_TaskCanceledException_ReturnsNull()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // TaskCanceledException 派生自 OperationCanceledException,同样必须保留取消语义
        Exception? translated = SshInterop.Translate(new TaskCanceledException(), cts.Token);

        Assert.IsNull(translated);
    }

    [TestMethod]
    public void Translate_CancellationWithoutCallerCancel_MapsToTimeout()
    {
        Exception? translated = SshInterop.Translate(new OperationCanceledException(), CancellationToken.None);

        Assert.IsInstanceOfType<VelaSshOperationTimeoutException>(translated);
    }

    [TestMethod]
    public void Translate_CancellationWithLiveCallerToken_MapsToTimeout()
    {
        using var cts = new CancellationTokenSource();

        // 调用方 token 存在但未取消 → 取消来自库内部超时
        Exception? translated = SshInterop.Translate(new OperationCanceledException(), cts.Token);

        Assert.IsInstanceOfType<VelaSshOperationTimeoutException>(translated);
    }

    [TestMethod]
    public void Translate_UnknownException_ReturnsNull() =>
        Assert.IsNull(SshInterop.Translate(new InvalidOperationException("boom")));

    [TestMethod]
    public void Translate_PreservesInnerException()
    {
        var original = new OperationCanceledException("timed out");

        Exception? translated = SshInterop.Translate(original);

        Assert.IsNotNull(translated);
        Assert.AreSame(original, translated.InnerException);
    }

    // ------------------------------------------------------------ 建链失败的分流

    /// <summary>
    /// <b>「连不上」与「认证不过」必须分开。</b>
    /// </summary>
    /// <remarks>
    /// 主窗口的自动重连只该对前者生效:连不上值得再试一次,认证不过再试一百次也一样,
    /// 而且每次都会在服务端留一条失败登录。
    /// </remarks>
    /// <summary>密钥交换的失败(对端公开值不合法)是建连失败,不是一般的客户端错误。</summary>
    [TestMethod]
    public void Translate_KeyExchangeFailure_MapsToConnectionException() =>
        Assert.IsInstanceOfType<VelaSshConnectionException>(
            SshInterop.Translate(new VelaShell.Ssh.Crypto.Kex.SshKeyExchangeException("公开值长度不对")));

    [TestMethod]
    [DataRow(SshFailureReason.TcpRefused)]
    [DataRow(SshFailureReason.DnsFailure)]
    [DataRow(SshFailureReason.TcpUnreachable)]
    [DataRow(SshFailureReason.ProxyRefused)]
    public void Translate_ConnectFailure_MapsToConnectionException(SshFailureReason reason)
    {
        Exception? translated = SshInterop.Translate(
            new SshConnectException(reason, SshPhase.Dialing, "nope"));

        Assert.IsInstanceOfType<VelaSshConnectionException>(translated);
        Assert.IsNotInstanceOfType<VelaSshOperationTimeoutException>(translated);
    }

    [TestMethod]
    [DataRow(SshFailureReason.Timeout)]
    [DataRow(SshFailureReason.TcpTimeout)]
    [DataRow(SshFailureReason.KeepAliveTimeout)]
    public void Translate_ConnectTimeout_MapsToTimeoutException(SshFailureReason reason)
    {
        Exception? translated = SshInterop.Translate(
            new SshConnectException(reason, SshPhase.Dialing, "too slow"));

        Assert.IsInstanceOfType<VelaSshOperationTimeoutException>(translated);
    }

    /// <summary>认证失败要带上**逐条尝试记录**,而不只是一句"认证失败"。</summary>
    /// <remarks>
    /// 没有这份记录时,用户看不出是密钥被跳过了、服务端根本不接受这种方法、
    /// 还是口令真的错了 —— 三种情况的下一步完全不同。
    /// </remarks>
    [TestMethod]
    public void Translate_AuthenticationFailure_MapsToAuthException_WithAttempts()
    {
        SshAuthenticationException original = new(
            SshFailureReason.AuthenticationMethodExhausted,
            "认证失败。",
            [new SshAuthAttempt("publickey", "~/.ssh/id_ed25519", SshAuthOutcome.Failure, ["publickey"], "服务端不接受这把钥")],
            ["publickey", "keyboard-interactive"],
            partialSuccessAchieved: false);

        Exception? translated = SshInterop.Translate(original);

        Assert.IsInstanceOfType<VelaSshAuthenticationException>(translated);
        Assert.Contains("id_ed25519", translated.Message,
            "逐条尝试记录必须进到消息里 —— 它是用户唯一能据以判断下一步的东西");
        Assert.StartsWith(Strings.Format("SshErr_AuthExhausted", "publickey, keyboard-interactive"), translated.Message,
            "主句按原因码用界面语言说,带上服务端接受的方法");
    }

    [TestMethod]
    public void Translate_TwoFactorRequired_UsesLocalizedTextAndPartialSuccess()
    {
        SshAuthenticationException original = new(
            SshFailureReason.TwoFactorRequired, "服务端要求键盘交互式认证。", [], ["keyboard-interactive"], partialSuccessAchieved: true);

        string message = SshInterop.Translate(original)!.Message;

        Assert.StartsWith(Strings.Format("SshErr_TwoFactorRequired", "keyboard-interactive"), message);
        Assert.Contains(Strings.Get("SshErr_AuthPartialSuccess"), message);
        Assert.Contains("[TwoFactorRequired @ Authenticating]", message);
    }

    [TestMethod]
    public void Translate_PasswordExpired_UsesLocalizedText()
    {
        SshAuthenticationException original = new(
            SshFailureReason.PasswordExpired, "服务端要求先修改密码。", [], ["password"], partialSuccessAchieved: false);

        Assert.StartsWith(Strings.Get("SshErr_PasswordExpired"), SshInterop.Translate(original)!.Message);
    }

    /// <summary>
    /// 算法协商失败要把**双方的完整名单**摊开。
    /// </summary>
    /// <remarks>
    /// 上一版为了同一件事要另开一条 TCP 回探对端的 KEXINIT(384 行),
    /// 因为上游没把对端名单暴露出来。现在异常自带,这里只要确认没把它丢掉。
    /// </remarks>
    [TestMethod]
    public void Translate_Negotiation_IncludesBothAlgorithmLists()
    {
        SshNegotiationException original = new(
            SshNegotiationCategory.EncryptionServerToClient,
            ["aes128-ctr"],
            ["aes256-gcm@openssh.com", "chacha20-poly1305@openssh.com"],
            "SSH-2.0-OpenSSH_7.4");

        Exception? translated = SshInterop.Translate(original);

        Assert.IsInstanceOfType<VelaSshConnectionException>(translated);
        Assert.Contains("aes128-ctr", translated.Message, "对端提供了什么必须说出来");
        Assert.Contains("aes256-gcm@openssh.com", translated.Message, "本端支持什么也必须说出来");
        Assert.Contains("OpenSSH_7.4", translated.Message, "对端版本串是判断「这台设备太老」的依据");
    }

    /// <summary>对端提供的算法里有本版实现了、只是没放开的:点名,并指到连接配置里去。</summary>
    [TestMethod]
    public void Translate_Negotiation_NamesWhatCanBeEnabled()
    {
        SshNegotiationException original = new(
            SshNegotiationCategory.MacClientToServer,
            ["hmac-md5", "hmac-sha1"],
            ["hmac-sha2-256-etm@openssh.com", "hmac-sha2-256"],
            "SSH-2.0-Cisco-1.25");

        string message = SshInterop.Translate(original)!.Message;

        Assert.Contains(Strings.Format("Ssh_AlgoMismatchEnable", "hmac-sha1"), message,
            "hmac-md5 本版没实现,不该出现在「可以放开」里");
    }

    /// <summary>对端这一类只剩本版没实现的(典型是只有 CBC 的老设备):如实说放开也没用。</summary>
    [TestMethod]
    public void Translate_Negotiation_SaysWhenNothingCanBeEnabled()
    {
        SshNegotiationException original = new(
            SshNegotiationCategory.EncryptionClientToServer,
            ["aes128-cbc", "3des-cbc"],
            ["aes256-gcm@openssh.com"],
            "SSH-2.0-OpenSSH_5.3");

        string message = SshInterop.Translate(original)!.Message;

        Assert.Contains(Strings.Get("Ssh_AlgoMismatchUnsupported"), message);
    }

    // ------------------------------------------------------------ SFTP 的分流

    /// <summary>
    /// SFTP 要分出「没这个文件」与「没权限」—— 上层据此决定是提示用户还是静默跳过。
    /// </summary>
    [TestMethod]
    public void Translate_SftpNotFound_MapsToPathNotFound()
    {
        Exception? translated = SshInterop.Translate(
            new SftpException(SftpStatusCode.NoSuchFile, "No such file", "/tmp/nope", SftpOperation.Open));

        Assert.IsInstanceOfType<VelaSftpPathNotFoundException>(translated);
    }

    /// <summary>写入中断带着精确的续传点过来:翻译之后它还在,上层据此续传而不是盲退一个在途窗口。</summary>
    [TestMethod]
    public void Translate_SftpTransferInterrupted_KeepsDurableLength()
    {
        Exception? translated = SshInterop.Translate(new SftpTransferInterruptedException(123_456, "中断"));

        VelaSftpTransferInterruptedException interrupted = Assert.IsInstanceOfType<VelaSftpTransferInterruptedException>(translated);
        Assert.AreEqual(123_456, interrupted.DurableLength);
        Assert.StartsWith(Strings.Format("SftpErr_TransferInterrupted", 123_456), interrupted.Message);
    }

    /// <summary>服务端拒写引起的中断:带上 SFTP 的那一句(「磁盘满」与「断线」的下一步不一样)。</summary>
    [TestMethod]
    public void Translate_SftpTransferInterruptedByRejectedWrite_IncludesServerReason()
    {
        SftpException rejected = new(SftpStatusCode.Failure, "No space left on device", "/data/big.iso", SftpOperation.Write);

        string message = SshInterop.Translate(new SftpTransferInterruptedException(4096, "中断", rejected))!.Message;

        Assert.Contains("No space left on device", message);
        Assert.Contains(Strings.Get("SftpErr_Failure"), message);
    }

    /// <summary>
    /// 服务端拒写与断线分得开:传输失败重试只重试后者(磁盘满再试几轮也是磁盘满)。
    /// </summary>
    [TestMethod]
    public void Translate_SftpTransferInterrupted_MarksServerRefusalOnly()
    {
        SftpException rejected = new(SftpStatusCode.Failure, "No space left on device", "/data/big.iso", SftpOperation.Write);
        SshConnectionClosedException dropped = new(SshFailureReason.ClosedByPeer, SshPhase.Open, "对端关闭了连接。");

        var refused = (VelaSftpTransferInterruptedException)SshInterop.Translate(new SftpTransferInterruptedException(4096, "中断", rejected))!;
        var lostLink = (VelaSftpTransferInterruptedException)SshInterop.Translate(new SftpTransferInterruptedException(4096, "中断", dropped))!;
        var bare = (VelaSftpTransferInterruptedException)SshInterop.Translate(new SftpTransferInterruptedException(4096, "中断"))!;

        Assert.IsTrue(refused.RefusedByServer);
        Assert.IsFalse(lostLink.RefusedByServer);
        Assert.IsFalse(bare.RefusedByServer, "没有内层异常时按断线算(库的约定)");
    }

    [TestMethod]
    public void Translate_SftpUnavailable_UsesLocalizedText()
    {
        Exception? translated = SshInterop.Translate(new SftpUnavailableException("服务端没有 SFTP 子系统。"));

        Assert.IsInstanceOfType<VelaSshClientException>(translated);
        Assert.StartsWith(Strings.Get("SftpErr_Unavailable"), translated!.Message);
    }

    [TestMethod]
    public void Translate_ProtocolError_UsesLocalizedText()
    {
        SshProtocolException original = new(SshPhase.KeyExchange, "启用严格 KEX 时，对端的第一个报文必须是 KEXINIT。");

        Exception? translated = SshInterop.Translate(original);

        Assert.IsInstanceOfType<VelaSshConnectionException>(translated);
        Assert.StartsWith(Strings.Get("SshErr_ProtocolError"), translated!.Message);
        Assert.AreSame(original, translated.InnerException, "具体是哪一条协议违规留在内层");
    }

    [TestMethod]
    [DataRow(SshFailureReason.LimitExceeded, "SshErr_LimitExceeded")]
    [DataRow(SshFailureReason.Aborted, "SshErr_Aborted")]
    public void Translate_FallbackReasons_UseLocalizedText(SshFailureReason reason, string key)
    {
        Exception? translated = SshInterop.Translate(new SshForwardException(reason, "转发通道数到了上限。"));

        Assert.StartsWith(Strings.Get(key), translated!.Message);
    }

    /// <summary>v4 起的「路径不存在」(10):多版本服务端在 v3 会话里也会回它,同样分到「没这个文件」。</summary>
    [TestMethod]
    public void Translate_SftpNoSuchPath_MapsToPathNotFound()
    {
        SftpException original = new(SftpStatusCode.NoSuchPath, "No such path", "/tmp/a/b", SftpOperation.Open);

        Assert.IsTrue(original.IsNotFound);
        Assert.IsInstanceOfType<VelaSftpPathNotFoundException>(SshInterop.Translate(original));
    }

    [TestMethod]
    public void Translate_SftpPermissionDenied_MapsToPermissionDenied()
    {
        Exception? translated = SshInterop.Translate(
            new SftpException(SftpStatusCode.PermissionDenied, "Permission denied", "/root/x", SftpOperation.Open));

        Assert.IsInstanceOfType<VelaSftpPermissionDeniedException>(translated);
    }

    /// <summary>
    /// 码 4(Failure)承载了绝大多数真实错误,**服务端原话是唯一能区分它们的信息**。
    /// </summary>
    /// <remarks>
    /// 「目录非空」「文件已存在」「磁盘满」「配额超限」全是同一个状态码 ——
    /// 把服务端那段文本丢掉,用户就只剩一句"操作失败"。
    /// </remarks>
    [TestMethod]
    public void Translate_SftpGenericFailure_KeepsServerMessage()
    {
        Exception? translated = SshInterop.Translate(
            new SftpException(SftpStatusCode.Failure, "Disk quota exceeded", "/home/joe/big.bin", SftpOperation.Write));

        Assert.IsInstanceOfType<VelaSftpOperationException>(translated);
        Assert.Contains("Disk quota exceeded", translated.Message);
    }

    /// <summary>
    /// 服务端原话只出现一次,而且清洗过;前半句按状态码换成界面语言。
    /// </summary>
    /// <remarks>
    /// 曾经在库的消息(里面已经有清洗过的「服务端说:…」)后面再追加一遍原文:同一句话显示两遍,
    /// 第二遍绕过了清洗,终端转义序列照样进了界面。
    /// </remarks>
    [TestMethod]
    public void Translate_SftpServerMessage_ShownOnceAndSanitized()
    {
        const string hostile = "quota\u001b]52;c;cm0=\u0007 exceeded";
        Exception? translated = SshInterop.Translate(
            new SftpException(SftpStatusCode.Failure, hostile, "/home/joe/\u001b[2Jbig.bin", SftpOperation.Write));

        string message = translated!.Message;
        Assert.StartsWith(Strings.Get("SftpErr_Failure"), message);
        Assert.AreEqual(1, message.Split("quota").Length - 1, "服务端原话只出现一次");
        foreach (char c in message)
        {
            Assert.IsFalse(char.IsControl(c), $"消息里不该有控制字符 U+{(int)c:X4}:{message}");
        }
        Assert.Contains("big.bin", message, "路径照样带上");
    }

    // ------------------------------------------------------------ 界面语言

    /// <summary>
    /// 原因码足以说清楚的失败换成界面语言的一句话;库的中文原文留在 <see cref="Exception.InnerException" />。
    /// </summary>
    [TestMethod]
    public void Translate_PassphraseIncorrect_UsesLocalizedText()
    {
        SshPrivateKeyException original = new(SshFailureReason.KeyPassphraseIncorrect, "私钥解不开 —— 口令多半不对。");

        Exception? translated = SshInterop.Translate(original);

        Assert.IsInstanceOfType<VelaSshAuthenticationException>(translated);
        Assert.StartsWith(Strings.Get("SshErr_KeyPassphraseIncorrect"), translated.Message);
        Assert.Contains("[KeyPassphraseIncorrect @ None]", translated.Message, "原因码尾巴不翻译,跨语言一致才好搜");
        Assert.AreSame(original, translated.InnerException);
    }

    [TestMethod]
    public void Translate_ChannelRequestRejected_UsesLocalizedText()
    {
        Exception? translated = SshInterop.Translate(
            new SshChannelException(SshFailureReason.ChannelRequestRejected, "服务端拒绝分配伪终端。"));

        Assert.IsInstanceOfType<VelaSshClientException>(translated);
        Assert.StartsWith(Strings.Get("SshErr_ChannelRequestRejected"), translated!.Message);
    }

    /// <summary>
    /// 库的消息带着具体信息(文件路径、指纹、端口)时照用原文 —— 换成一句通用文案反而帮不上忙。
    /// </summary>
    [TestMethod]
    public void Translate_KeyFormatInvalid_KeepsLibraryMessageWithPath()
    {
        Exception? translated = SshInterop.Translate(
            new SshPrivateKeyException(SshFailureReason.KeyFormatInvalid, "私钥格式不对(~/.ssh/id_broken)。"));

        Assert.IsInstanceOfType<VelaSshAuthenticationException>(translated);
        Assert.Contains("id_broken", translated.Message);
    }
}

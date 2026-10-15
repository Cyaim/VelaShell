// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 测试服务端的**认证侧**。它接在 TestSshServer.HandshakeAsync 之后，
// 在同一条传输上跑 RFC 4252 / RFC 4256 的服务端一半。
//
// ⚠️ **只为测试存在，绝不发布。**
//    它按配置放行或拒绝，没有任何速率限制，还刻意保留了「可配置地做错事」的开关。

using System.Buffers;
using System.Buffers.Binary;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Keys;
using VelaShell.Ssh.Protocol;
using VelaShell.Ssh.Transport;

namespace VelaShell.Ssh.Tests.TestKit;

/// <summary>一轮 keyboard-interactive 的服务端剧本。</summary>
internal sealed record TestKeyboardRound
{
    /// <summary>对话框标题。</summary>
    public string Name { get; init; } = "";

    /// <summary>说明文字。</summary>
    public string Instruction { get; init; } = "";

    /// <summary>这一轮的提示。空列表表示**纯展示轮**。</summary>
    public IReadOnlyList<(string Text, bool Echo)> Prompts { get; init; } = [];

    /// <summary>期望收到的答案。条数须与 <see cref="Prompts"/> 相同。</summary>
    public IReadOnlyList<string> ExpectedAnswers { get; init; } = [];
}

/// <summary>测试服务端的认证策略。</summary>
internal sealed record TestAuthPolicy
{
    /// <summary>期望的用户名；<see langword="null"/> 时不校验。</summary>
    public string? ExpectedUserName { get; init; }

    /// <summary>初次宣告的可用方法。</summary>
    public IReadOnlyList<string> OfferedMethods { get; init; } =
        [SshProtocolNames.AuthPublicKey, SshProtocolNames.AuthPassword];

    /// <summary>
    /// **必须全部通过**的方法集合。
    /// </summary>
    /// <remarks>
    /// 有一个以上元素时就是多因素：每通过一个，服务端回
    /// <c>USERAUTH_FAILURE</c> 且 <c>partial_success = true</c>，
    /// 并把剩下的方法列出来 —— 这就是 SSH 里 2FA 的全部表达方式。
    /// </remarks>
    public IReadOnlyList<string> RequiredMethods { get; init; } = [SshProtocolNames.AuthPassword];

    /// <summary>接受的密码；<see langword="null"/> 表示任何密码都不接受。</summary>
    public string? AcceptPassword { get; init; }

    /// <summary>接受的公钥 blob。</summary>
    public IReadOnlyList<byte[]> AcceptedPublicKeys { get; init; } = [];

    /// <summary>对认得的公钥的探测（不带签名）直接回 <c>SUCCESS</c> —— 不合规，个别实现会这样。</summary>
    public bool SucceedOnPublicKeyProbe { get; init; }

    /// <summary>设了就在 <c>PK_OK</c> 里回显这个公钥 blob，而不是请求里的那个。</summary>
    public byte[]? PublicKeyOkEchoBlob { get; init; }

    /// <summary>设了就在 <c>PK_OK</c> 里回显这个算法名，而不是请求里的那个。</summary>
    public string? PublicKeyOkEchoAlgorithm { get; init; }

    /// <summary>keyboard-interactive 的剧本，按顺序发出。</summary>
    public IReadOnlyList<TestKeyboardRound> KeyboardRounds { get; init; } = [];

    /// <summary>认证开始前发出的横幅文本。</summary>
    public IReadOnlyList<string> Banners { get; init; } = [];

    /// <summary>认证开始前发出的 <c>SSH_MSG_DEBUG</c>（是否 always_display、正文）。</summary>
    public IReadOnlyList<(bool AlwaysDisplay, string Text)> DebugMessages { get; init; } = [];

    /// <summary>
    /// 紧挨着 <c>USERAUTH_SUCCESS</c> 之前发出的横幅 —— 此时客户端的请求已经发出、正在等应答。
    /// </summary>
    public IReadOnlyList<string> BannersBeforeSuccess { get; init; } = [];

    /// <summary>横幅发出后、成功前等待外部批准；返回 false 时拒绝这次请求。</summary>
    public Func<CancellationToken, Task<bool>>? BeforeSuccess { get; init; }

    /// <summary>紧挨着 <c>USERAUTH_SUCCESS</c> 之前再发一个 <c>EXT_INFO</c>（RFC 8308 §2.4 的第二个合法位置）。</summary>
    public IReadOnlyList<string>? ExtInfoBeforeSuccess { get; init; }

    /// <summary>第一次回 <c>USERAUTH_FAILURE</c> 之前插一个 <c>EXT_INFO</c>（不合法的位置）。</summary>
    public IReadOnlyList<string>? ExtInfoBeforeFirstFailure { get; init; }

    /// <summary>
    /// 回 <c>USERAUTH_SUCCESS</c> 之前由服务端发起一次密钥重协商（模拟用户输动态码太久、服务端按时间 RekeyLimit 发起）。
    /// 要求认证服务端拿得到 <see cref="TestAuthServer.SshServer"/>。
    /// </summary>
    public bool RekeyBeforeSuccess { get; init; }

    /// <summary>
    /// 通过 <c>EXT_INFO</c> 宣告的 <c>server-sig-algs</c>；
    /// <see langword="null"/> 表示不发 <c>EXT_INFO</c>。
    /// </summary>
    public IReadOnlyList<string>? ServerSignatureAlgorithms { get; init; }

    /// <summary>密码认证（不是改密码请求时）一律回 <c>PASSWD_CHANGEREQ</c>（60）。</summary>
    public bool RequestPasswordChange { get; init; }

    /// <summary>头几次改密码请求嫌新密码太简单，再回 <c>PASSWD_CHANGEREQ</c>（RFC 4252 §8）。</summary>
    public int RejectNewPasswords { get; init; }

    /// <summary>改密码请求一律回 <c>FAILURE</c>：不支持改密码（RFC 4252 §8，与「旧密码不对」同一个应答）。</summary>
    public bool PasswordChangeUnsupported { get; init; }

    /// <summary>是否真的验签。</summary>
    public bool VerifyPublicKeySignature { get; init; } = true;

    /// <summary>
    /// 公钥认证只认这些签名算法（探测与签名请求都看）；<see langword="null"/> 表示都认。
    /// </summary>
    /// <remarks>模拟只认 <c>ssh-rsa</c>（SHA-1）的老服务器：它不认 rsa-sha2-*，也不发 server-sig-algs。</remarks>
    public IReadOnlyList<string>? AcceptedPublicKeyAlgorithms { get; init; }
}

/// <summary>认证过程中服务端观察到的事实，供断言使用。</summary>
internal sealed class TestAuthObservation
{
    /// <summary>收到的 <c>USERAUTH_REQUEST</c> 方法名，按顺序。</summary>
    public List<string> RequestedMethods { get; } = [];

    /// <summary>收到的用户名（去重后按顺序）。</summary>
    public List<string> UserNames { get; } = [];

    /// <summary>公钥认证里客户端用过的签名算法。</summary>
    public List<string> PublicKeySignatureAlgorithms { get; } = [];

    /// <summary>收到过几次不带签名的公钥探测（两段式的第一段）。</summary>
    public int PublicKeyProbeCount { get; set; }

    /// <summary>收到过几次带签名的公钥请求。</summary>
    public int PublicKeySignedCount { get; set; }

    /// <summary>已经通过的方法，按顺序。</summary>
    public List<string> PassedMethods { get; } = [];

    /// <summary>keyboard-interactive 收到的答案，按轮。</summary>
    public List<IReadOnlyList<string>> KeyboardAnswers { get; } = [];

    /// <summary>收到的改密码请求（旧密码、新密码），按顺序。</summary>
    public List<(string Old, string New)> PasswordChanges { get; } = [];

    /// <summary>收到的签名是否全部验证通过。</summary>
    public bool AllSignaturesValid { get; set; } = true;

    /// <summary>认证期间客户端发来 <c>DISCONNECT</c> 时的原因码。</summary>
    public uint? ClientDisconnectReason { get; set; }

    /// <summary>认证期间服务端发起并做完的重协商次数。</summary>
    public int RekeysDuringAuth { get; set; }
}

/// <summary>测试服务端的认证侧。</summary>
/// <remarks>在一条已完成密钥交换的传输上建立认证服务端。</remarks>
internal sealed class TestAuthServer(SshPacketTransport transport, byte[] sessionId, TestAuthPolicy? policy = null)
{
    private const int MaxField = 256 * 1024;

    private readonly SshPacketTransport _transport = transport;
    private readonly byte[] _sessionId = sessionId;
    private readonly TestAuthPolicy _policy = policy ?? new TestAuthPolicy();
    // string 的默认相等比较器就是序数比较。
    private readonly HashSet<string> _passed = [];

    /// <summary>这一轮请求服务端已经自己回过报文了，外层不要再补一个 FAILURE。</summary>
    private bool _alreadyAnswered;

    /// <summary>服务端这一侧观察到的事实。</summary>
    public TestAuthObservation Observation { get; } = new();

    /// <summary>做握手的那个服务端（<see cref="TestAuthPolicy.RekeyBeforeSuccess"/> 要用它发起重协商）。</summary>
    public TestSshServer? SshServer { get; init; }

    /// <summary>跑完认证的服务端一侧。</summary>
    /// <returns>认证是否成功。</returns>
    public async Task<bool> RunAsync(CancellationToken cancellationToken = default)
    {
        // EXT_INFO 在首次 NEWKEYS 之后立刻发 —— 这是真实服务器的位置，
        // 也让客户端在第一次公钥认证之前就拿到 server-sig-algs（RFC 8308 §2.4）。
        if (_policy.ServerSignatureAlgorithms is { } algorithms)
        {
            await SendExtensionInfoAsync(algorithms, cancellationToken);
        }

        await ExpectServiceRequestAsync(cancellationToken);

        foreach ((bool alwaysDisplay, string text) in _policy.DebugMessages)
        {
            await SendDebugAsync(_transport, alwaysDisplay, text, cancellationToken);
        }

        foreach (string banner in _policy.Banners)
        {
            await SendBannerAsync(banner, cancellationToken);
        }

        while (true)
        {
            SshInboundPacket packet = await _transport.ReadPacketAsync(cancellationToken);
            if (packet.IsEndOfStream || packet.MessageNumber == SshMessageNumber.Disconnect)
            {
                if (!packet.IsEndOfStream)
                {
                    Observation.ClientDisconnectReason = BinaryPrimitives.ReadUInt32BigEndian(packet.Payload.Span[1..]);
                }
                return false;   // 客户端放弃了
            }

            if (packet.MessageNumber is SshMessageNumber.Ignore or SshMessageNumber.Debug)
            {
                continue;
            }

            if (packet.MessageNumber != SshMessageNumber.UserAuthRequest)
            {
                throw new InvalidOperationException($"认证期间期望 UserAuthRequest，收到 {packet.MessageNumber}。");
            }

            if (await HandleRequestAsync(packet.Payload.ToArray(), cancellationToken))
            {
                return true;
            }
        }
    }

    private async Task<bool> HandleRequestAsync(byte[] payload, CancellationToken cancellationToken)
    {
        ParsedAuthRequest request = ParseRequest(payload);
        _alreadyAnswered = false;

        if (request.Service != SshProtocolNames.ServiceConnection)
        {
            throw new InvalidOperationException(
                $"认证请求里的服务名应当是 ssh-connection，收到 {request.Service}。");
        }

        Observation.RequestedMethods.Add(request.Method);
        if (!Observation.UserNames.Contains(request.UserName, StringComparer.Ordinal))
        {
            Observation.UserNames.Add(request.UserName);
        }

        bool accepted;
        if (_policy.ExpectedUserName is { } expected && request.UserName != expected)
        {
            accepted = false;
        }
        else
        {
            accepted = request.Method switch
            {
                // 通常拒绝 —— 但策略把 none 列进必须通过的方法时，就是「配了无认证」。
                // 客户端不能假设 none 一定失败。
                SshProtocolNames.AuthNone =>
                    _policy.RequiredMethods.Contains(SshProtocolNames.AuthNone, StringComparer.Ordinal),
                SshProtocolNames.AuthPassword => await HandlePasswordAsync(request, cancellationToken),
                SshProtocolNames.AuthPublicKey => await HandlePublicKeyAsync(request, payload, cancellationToken),
                SshProtocolNames.AuthKeyboardInteractive => await HandleKeyboardInteractiveAsync(cancellationToken),
                _ => false,
            };
        }

        if (!accepted)
        {
            if (!_alreadyAnswered)
            {
                await SendFailureAsync(partialSuccess: false, cancellationToken);
            }
            return false;
        }

        _passed.Add(request.Method);
        Observation.PassedMethods.Add(request.Method);

        // 要求的方法还没全过 —— 回 FAILURE 但把 partial_success 置真。
        if (_policy.RequiredMethods.Any(m => !_passed.Contains(m)))
        {
            await SendFailureAsync(partialSuccess: true, cancellationToken);
            return false;
        }

        foreach (string banner in _policy.BannersBeforeSuccess)
        {
            await SendBannerAsync(banner, cancellationToken);
        }

        if (_policy.BeforeSuccess is { } beforeSuccess && !await beforeSuccess(cancellationToken))
        {
            await SendFailureAsync(partialSuccess: false, cancellationToken);
            return false;
        }

        if (_policy.ExtInfoBeforeSuccess is { } lateExtInfo)
        {
            await SendExtensionInfoAsync(lateExtInfo, cancellationToken);
        }

        if (_policy.RekeyBeforeSuccess && SshServer is { } ssh)
        {
            byte[] ours = await ssh.BeginRekeyAsync(SendRawAsync, cancellationToken);
            SshInboundPacket clientKexInit = await ReadSkippingNoiseAsync(cancellationToken);
            _ = await ssh.CompleteRekeyAsync(ours, clientKexInit.Payload.ToArray(), SendRawAsync, ReadRawAsync, cancellationToken);
            Observation.RekeysDuringAuth++;
        }

        _transport.WritePacket([(byte)SshMessageNumber.UserAuthSuccess]);
        await _transport.FlushAsync(cancellationToken);
        return true;
    }

    // ------------------------------------------------------------ 解析

    /// <summary>一条已解开的认证请求。</summary>
    /// <remarks>
    /// 解析单独放在同步方法里：<c>SshDataReader</c> 是 ref struct，
    /// 既不能当 async 方法的参数，也不能当 async 方法的局部变量。
    /// </remarks>
    private sealed record ParsedAuthRequest
    {
        public required string UserName { get; init; }
        public required string Service { get; init; }
        public required string Method { get; init; }

        public bool PasswordIsChange { get; init; }
        public string Password { get; init; } = "";
        public string NewPassword { get; init; } = "";

        public bool HasSignature { get; init; }
        public string Algorithm { get; init; } = "";
        public byte[] KeyBlob { get; init; } = [];
        public byte[] Signature { get; init; } = [];
    }

    private static ParsedAuthRequest ParseRequest(byte[] payload)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.UserAuthRequest);
        string userName = reader.ReadUtf8String(MaxField);
        string service = reader.ReadUtf8String(MaxField);
        string method = reader.ReadUtf8String(MaxField);

        if (method == SshProtocolNames.AuthPassword)
        {
            bool isChange = reader.ReadBoolean();
            string password = reader.ReadUtf8String(MaxField);
            return new ParsedAuthRequest
            {
                UserName = userName,
                Service = service,
                Method = method,
                PasswordIsChange = isChange,
                Password = password,
                NewPassword = isChange ? reader.ReadUtf8String(MaxField) : "",
            };
        }

        if (method == SshProtocolNames.AuthPublicKey)
        {
            bool hasSignature = reader.ReadBoolean();
            string algorithm = reader.ReadUtf8String(MaxField);
            byte[] keyBlob = reader.ReadStringAsArray(MaxField);
            return new ParsedAuthRequest
            {
                UserName = userName,
                Service = service,
                Method = method,
                HasSignature = hasSignature,
                Algorithm = algorithm,
                KeyBlob = keyBlob,
                Signature = hasSignature ? reader.ReadStringAsArray(MaxField) : [],
            };
        }

        return new ParsedAuthRequest { UserName = userName, Service = service, Method = method };
    }

    // ------------------------------------------------------------ 各方法

    private async Task<bool> HandlePasswordAsync(ParsedAuthRequest request, CancellationToken cancellationToken)
    {
        if (request.PasswordIsChange)
        {
            // RFC 4252 §8：FAILURE（不带 partial）= 没改成（不支持，或旧密码不对）；PASSWD_CHANGEREQ = 新密码不行；放行 = 改成了。
            Observation.PasswordChanges.Add((request.Password, request.NewPassword));
            if (_policy.PasswordChangeUnsupported || _policy.AcceptPassword != request.Password)
            {
                return false;
            }
            if (_newPasswordsRejected < _policy.RejectNewPasswords)
            {
                _newPasswordsRejected++;
                await SendPasswordChangeRequestAsync("新密码太简单。", cancellationToken);
                return false;
            }
            return true;
        }

        if (_policy.RequestPasswordChange)
        {
            await SendPasswordChangeRequestAsync("你的密码已过期。", cancellationToken);
            return false;
        }

        return _policy.AcceptPassword is { } expected && request.Password == expected;
    }

    private int _newPasswordsRejected;

    private async Task SendPasswordChangeRequestAsync(string prompt, CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter w = new(buffer);
        w.WriteByte(60);                       // SSH_MSG_USERAUTH_PASSWD_CHANGEREQ
        w.WriteUtf8String(prompt);
        w.WriteUtf8String("");                 // 语言标记
        _transport.WritePacket(buffer.WrittenSpan);
        await _transport.FlushAsync(cancellationToken);
        _alreadyAnswered = true;
    }

    private async Task<bool> HandlePublicKeyAsync(
        ParsedAuthRequest request, byte[] payload, CancellationToken cancellationToken)
    {
        bool known = _policy.AcceptedPublicKeys.Any(k => k.AsSpan().SequenceEqual(request.KeyBlob))
            && (_policy.AcceptedPublicKeyAlgorithms is null
                || _policy.AcceptedPublicKeyAlgorithms.Contains(request.Algorithm, StringComparer.Ordinal));

        if (!request.HasSignature)
        {
            Observation.PublicKeyProbeCount++;
            if (!known)
            {
                return false;
            }

            if (_policy.SucceedOnPublicKeyProbe)
            {
                return true;   // 回 SUCCESS
            }

            // SSH_MSG_USERAUTH_PK_OK：认这把钥，去签吧。
            ArrayBufferWriter<byte> buffer = new();
            SshDataWriter w = new(buffer);
            w.WriteByte(60);
            w.WriteUtf8String(_policy.PublicKeyOkEchoAlgorithm ?? request.Algorithm);
            w.WriteString(_policy.PublicKeyOkEchoBlob ?? request.KeyBlob);
            _transport.WritePacket(buffer.WrittenSpan);
            await _transport.FlushAsync(cancellationToken);

            _alreadyAnswered = true;
            return false;   // 探测本身不算认证通过
        }

        Observation.PublicKeySignedCount++;
        Observation.PublicKeySignatureAlgorithms.Add(request.Algorithm);

        if (!known)
        {
            return false;
        }

        if (!_policy.VerifyPublicKeySignature)
        {
            return true;
        }

        // 被签名的数据 = string session_id ‖ 「签名字段之前」的整段请求载荷。
        // 「签名字段之前」= 整个载荷去掉末尾那个签名 string（4 字节长度 + 内容）。
        int requestLength = payload.Length - 4 - request.Signature.Length;
        ArrayBufferWriter<byte> signed = new();
        SshDataWriter signedWriter = new(signed);
        signedWriter.WriteString(_sessionId);
        signedWriter.WriteRaw(payload.AsSpan(0, requestLength));

        // 证书出示的 blob 是整张证书，而签名是里面那把普通钥出的 ——
        // 真服务端也是这么拆的，测试桩不这么做就只能测「非证书」那一半。
        SshPublicKey verifier = request.Algorithm.EndsWith(
            SshAlgorithmNames.CertificateSuffix, StringComparison.Ordinal)
            ? OpenSshCertificate.Decode(request.KeyBlob).Key
            : SshPublicKey.Decode(request.KeyBlob);

        bool valid = verifier.VerifySignature(request.Signature, signed.WrittenSpan, request.Algorithm);

        if (!valid)
        {
            Observation.AllSignaturesValid = false;
        }
        return valid;
    }

    private async Task<bool> HandleKeyboardInteractiveAsync(CancellationToken cancellationToken)
    {
        if (_policy.KeyboardRounds.Count == 0)
        {
            return false;
        }

        bool allMatched = true;

        foreach (TestKeyboardRound round in _policy.KeyboardRounds)
        {
            ArrayBufferWriter<byte> buffer = new();
            SshDataWriter w = new(buffer);
            w.WriteByte(60);                       // SSH_MSG_USERAUTH_INFO_REQUEST
            w.WriteUtf8String(round.Name);
            w.WriteUtf8String(round.Instruction);
            w.WriteUtf8String("");                 // 语言标记
            w.WriteUInt32((uint)round.Prompts.Count);
            foreach ((string text, bool echo) in round.Prompts)
            {
                w.WriteUtf8String(text);
                w.WriteBoolean(echo);
            }
            _transport.WritePacket(buffer.WrittenSpan);
            await _transport.FlushAsync(cancellationToken);

            SshInboundPacket response = await _transport.ReadPacketAsync(cancellationToken);
            if (response.IsEndOfStream)
            {
                throw new InvalidOperationException("客户端在 keyboard-interactive 中途关闭了连接。");
            }

            List<string> answers = ParseInfoResponse(response.Payload.ToArray());
            Observation.KeyboardAnswers.Add(answers);

            if (!answers.SequenceEqual(round.ExpectedAnswers, StringComparer.Ordinal))
            {
                allMatched = false;
            }
        }

        return allMatched;
    }

    private static List<string> ParseInfoResponse(byte[] payload)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        byte number = reader.ReadByte();
        if (number != 61)
        {
            throw new InvalidOperationException($"期望 INFO_RESPONSE(61)，收到 {number}。");
        }

        uint count = reader.ReadUInt32();
        List<string> answers = [];
        for (uint i = 0; i < count; i++)
        {
            answers.Add(reader.ReadUtf8String(MaxField));
        }
        return answers;
    }

    // ------------------------------------------------------------ 发送

    private async Task ExpectServiceRequestAsync(CancellationToken cancellationToken)
    {
        SshInboundPacket packet = await _transport.ReadPacketAsync(cancellationToken);
        if (packet.MessageNumber != SshMessageNumber.ServiceRequest)
        {
            throw new InvalidOperationException($"期望 ServiceRequest，收到 {packet.MessageNumber}。");
        }

        string service = ParseServiceRequest(packet.Payload.ToArray());
        if (service != SshProtocolNames.ServiceUserAuth)
        {
            throw new InvalidOperationException($"期望 ssh-userauth，收到 {service}。");
        }

        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter w = new(buffer);
        w.WriteMessageNumber(SshMessageNumber.ServiceAccept);
        w.WriteUtf8String(service);
        _transport.WritePacket(buffer.WrittenSpan);
        await _transport.FlushAsync(cancellationToken);
    }

    private static string ParseServiceRequest(byte[] payload)
    {
        SshDataReader reader = new(new ReadOnlySequence<byte>(payload));
        reader.ReadMessageNumber(SshMessageNumber.ServiceRequest);
        return reader.ReadUtf8String(MaxField);
    }

    private async Task SendExtensionInfoAsync(IReadOnlyList<string> algorithms, CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter w = new(buffer);
        w.WriteMessageNumber(SshMessageNumber.ExtInfo);
        w.WriteUInt32(1);
        w.WriteUtf8String(SshProtocolNames.ExtServerSigAlgs);
        w.WriteUtf8String(string.Join(',', algorithms));
        _transport.WritePacket(buffer.WrittenSpan);
        await _transport.FlushAsync(cancellationToken);
    }

    private async ValueTask SendRawAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        _transport.WritePacket(packet.Span);
        await _transport.FlushAsync(cancellationToken);
    }

    private ValueTask<SshInboundPacket> ReadRawAsync(CancellationToken cancellationToken) =>
        _transport.ReadPacketAsync(cancellationToken);

    private async ValueTask<SshInboundPacket> ReadSkippingNoiseAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            SshInboundPacket packet = await _transport.ReadPacketAsync(cancellationToken);
            if (packet.MessageNumber is not (SshMessageNumber.Ignore or SshMessageNumber.Debug))
            {
                return packet;
            }
        }
    }

    /// <summary>发一条 <c>SSH_MSG_DEBUG</c>。</summary>
    internal static async Task SendDebugAsync(SshPacketTransport transport, bool alwaysDisplay, string text, CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter w = new(buffer);
        w.WriteMessageNumber(SshMessageNumber.Debug);
        w.WriteBoolean(alwaysDisplay);
        w.WriteUtf8String(text);
        w.WriteUtf8String("");   // 语言标记
        transport.WritePacket(buffer.WrittenSpan);
        await transport.FlushAsync(cancellationToken);
    }

    internal async Task SendBannerAsync(string text, CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter w = new(buffer);
        w.WriteMessageNumber(SshMessageNumber.UserAuthBanner);
        w.WriteUtf8String(text);
        w.WriteUtf8String("");   // 语言标记
        _transport.WritePacket(buffer.WrittenSpan);
        await _transport.FlushAsync(cancellationToken);
    }

    private bool _failureSent;

    private async Task SendFailureAsync(bool partialSuccess, CancellationToken cancellationToken)
    {
        if (!_failureSent && _policy.ExtInfoBeforeFirstFailure is { } misplaced)
        {
            await SendExtensionInfoAsync(misplaced, cancellationToken);
        }
        _failureSent = true;

        // partial_success 为真时，只列还没过的那些 —— 客户端据此挑下一个方法。
        string[] remaining = partialSuccess
            ? [.. _policy.RequiredMethods.Where(m => !_passed.Contains(m))]
            : [.. _policy.OfferedMethods];

        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter w = new(buffer);
        w.WriteMessageNumber(SshMessageNumber.UserAuthFailure);
        w.WriteNameList(remaining);
        w.WriteBoolean(partialSuccess);
        _transport.WritePacket(buffer.WrittenSpan);
        await _transport.FlushAsync(cancellationToken);
    }
}

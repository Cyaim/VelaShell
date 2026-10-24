using VelaShell.Core.Net;
using VelaShell.Core.Ssh;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Crypto;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Session;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 库的连接对象 → Core 的连接信息快照(<see cref="SshConnectionDetails" />)。
/// </summary>
/// <remarks>
/// 只读 <see cref="SshConnection" /> 公开的只读属性,不发任何报文:往返时间取 <see cref="SshConnection.LastRoundTrip" />
/// (状态栏的延迟测量与保活顺手量的),而不是现量一次 —— 面板不能自己成为一次探测。
/// 库类型只在这里流动,换库时替换这一份映射。
/// </remarks>
internal static class SshConnectionDetailsBuilder
{
    /// <summary>取一份快照。</summary>
    /// <param name="connection">这条连接(最外一跳)。</param>
    /// <param name="target">给人看的目标(<c>用户@主机:端口</c>)。</param>
    /// <param name="route">经过的代理与跳板;没有记录时为 <see langword="null" />。</param>
    /// <param name="obscureKeystrokeTiming">这条连接的配置开了按键时序混淆。</param>
    public static SshConnectionDetails Build(
        SshConnection connection, string target, SshRouteRecorder? route, bool obscureKeystrokeTiming)
    {
        ArgumentNullException.ThrowIfNull(connection);
        SshConnectTimings timings = connection.ConnectTimings;
        return new()
        {
            Target = target,
            CapturedAt = DateTimeOffset.Now,
            IsAlive = connection.IsAlive,
            PeerVersion = connection.PeerVersion,
            HostKey = HostKeyOf(connection.HostKey),
            SessionId = Convert.ToHexStringLower(connection.SessionId.Span),
            PeerSupportsPing = connection.PeerSupportsPing,
            Algorithms = AlgorithmsOf(connection.Algorithms),
            AuthenticationMethod = connection.AuthenticationMethod,
            ServerSignatureAlgorithms = [.. connection.ServerSignatureAlgorithms],
            Timings = new(timings.Dialing, timings.VersionExchange, timings.KeyExchange, timings.Authentication),
            LastRoundTrip = connection.LastRoundTrip,
            RekeyCount = connection.RekeyCount,
            LastRekey = connection.LastRekey is { } cause
                ? new SshRekeyDetails(ReasonOf(cause.Trigger), cause.Observed, cause.Threshold, connection.LastRekeyDuration)
                : null,
            BytesSent = connection.BytesSent,
            BytesReceived = connection.BytesReceived,
            PacketsSent = connection.PacketsSent,
            PacketsReceived = connection.PacketsReceived,
            Channels = [.. connection.Channels.Select(ChannelOf)],
            Proxy = route?.Proxy,
            Hops = [.. (route?.Hops ?? []).Select(HopOf)],
            ObscureKeystrokeTiming = obscureKeystrokeTiming,
        };
    }

    private static SshHostKeyDetails HostKeyOf(SshPublicKey key) =>
        new(key.KeyType, key.KeyBits, key.Sha256Fingerprint, key.IsCertificate);

    private static SshAlgorithmDetails AlgorithmsOf(SshNegotiatedAlgorithms algorithms) => new()
    {
        KeyExchange = algorithms.KeyExchange,
        HostKey = algorithms.HostKey,
        EncryptionClientToServer = algorithms.EncryptionClientToServer,
        EncryptionServerToClient = algorithms.EncryptionServerToClient,
        MacClientToServer = algorithms.MacClientToServer,
        MacServerToClient = algorithms.MacServerToClient,
        CompressionClientToServer = algorithms.CompressionClientToServer,
        CompressionServerToClient = algorithms.CompressionServerToClient,
        StrictKeyExchange = algorithms.StrictKeyExchange,
        ExtensionNegotiation = algorithms.PeerSupportsExtensionInfo,
    };

    private static SshChannelDetails ChannelOf(SshChannelSnapshot channel) =>
        new(channel.LocalId, channel.ChannelType, PhaseOf(channel.State), channel.OpenedAt, channel.BytesSent, channel.BytesReceived);

    private static SshHopDetails HopOf(SshRouteRecorder.RecordedHop hop)
    {
        SshConnection connection = hop.Connection;
        SshNegotiatedAlgorithms algorithms = connection.Algorithms;
        return new(
            HostAddress.FormatTarget(hop.Info.Username, hop.Info.Host, hop.Info.Port),
            connection.PeerVersion,
            algorithms.KeyExchange,
            algorithms.HostKey,
            algorithms.EncryptionClientToServer,
            connection.AuthenticationMethod,
            connection.IsAlive);
    }

    /// <summary>通道状态折成四步:EOF 的三种(本端、对端、两端)对看面板的人是一回事 —— 还没关,但不再双向收发了。</summary>
    private static SshChannelPhase PhaseOf(SshChannelState state) => state switch
    {
        SshChannelState.Opening => SshChannelPhase.Opening,
        SshChannelState.Open => SshChannelPhase.Open,
        SshChannelState.LocalEof or SshChannelState.RemoteEof or SshChannelState.BothEof => SshChannelPhase.HalfClosed,
        _ => SshChannelPhase.Closing,
    };

    private static SshRekeyReason ReasonOf(SshRekeyTrigger trigger) => trigger switch
    {
        SshRekeyTrigger.Peer => SshRekeyReason.Peer,
        SshRekeyTrigger.Requested => SshRekeyReason.Requested,
        SshRekeyTrigger.Bytes => SshRekeyReason.Bytes,
        SshRekeyTrigger.Packets => SshRekeyReason.Packets,
        SshRekeyTrigger.Interval => SshRekeyReason.Interval,
        SshRekeyTrigger.PacketHardLimit => SshRekeyReason.PacketHardLimit,
        _ => SshRekeyReason.Unknown,
    };
}

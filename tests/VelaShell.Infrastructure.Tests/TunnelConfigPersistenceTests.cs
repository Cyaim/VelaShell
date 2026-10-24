using System.Text.Json;
using VelaShell.Core.Import;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Infrastructure.Tests;

/// <summary>
/// 隧道配置的新字段(限速、远程动态的放行名单、两头的 Unix 套接字路径)与新类型在三种落盘形状里往返:
/// 本机库(<c>SonnetDbJson</c>,枚举存数值)、导出文件(<c>SessionArchiveJson</c>,枚举写名字)、
/// 以及旧版写下的、缺这些字段的配置。
/// </summary>
/// <remarks>
/// 隧道配置是整个对象序列化的(本机库、云同步、导出都是),没有逐字段手抄的那一步 —— 但新字段的默认值要让旧配置读出来
/// 还是原来的样子,而不带新字段的配置写出去也要与旧版的形状一致(值为空的不写)。
/// </remarks>
[TestClass]
[TestCategory("Tunnel")]
public sealed class TunnelConfigPersistenceTests
{
    private static List<TunnelConfig> Sample() =>
    [
        new()
        {
            Type = TunnelType.RemoteDynamicForward,
            Name = "socks-back",
            LocalHost = "127.0.0.1",
            LocalPort = 0,
            RemoteHost = "127.0.0.1",
            RemotePort = 1080,
            PermitRemoteOpen = ["localhost:8080", "*.corp.example:443"],
            MaxBytesPerSecond = 512 * 1024,
            AutoStart = true
        },
        new()
        {
            Type = TunnelType.LocalForward,
            Name = "docker",
            LocalHost = "127.0.0.1",
            LocalPort = 2375,
            RemoteHost = "127.0.0.1",
            RemotePort = 2375,
            LocalSocketPath = @"C:\Users\me\docker.sock",
            RemoteSocketPath = "/var/run/docker.sock"
        }
    ];

    [TestMethod]
    public void LocalStore_RoundTripsTheNewFields()
    {
        List<TunnelConfig> back = SonnetDbJson.Deserialize<List<TunnelConfig>>(SonnetDbJson.Serialize(Sample()))!;

        AssertSameAsSample(back);
    }

    [TestMethod]
    public void ArchiveFile_RoundTripsTheNewFields()
    {
        string json = JsonSerializer.Serialize(Sample(), SessionArchiveJson.Options);
        StringAssert.Contains(json, "\"remoteDynamicForward\"", "导出文件里的类型写名字。");

        AssertSameAsSample(JsonSerializer.Deserialize<List<TunnelConfig>>(json, SessionArchiveJson.Options)!);
    }

    /// <summary>旧版写下的配置没有这些字段:读出来是不限速、不用套接字、没有放行名单 —— 与原来的行为一样。</summary>
    [TestMethod]
    public void OldConfigs_ReadAsBefore_AndPlainConfigsKeepTheOldShape()
    {
        const string old = """[{"type":2,"name":"socks","localHost":"127.0.0.1","localPort":1080,"remoteHost":"","remotePort":0,"autoReconnect":true,"autoStart":false}]""";

        TunnelConfig config = SonnetDbJson.Deserialize<List<TunnelConfig>>(old)!.Single();

        Assert.AreEqual(TunnelType.DynamicForward, config.Type);
        Assert.IsNull(config.MaxBytesPerSecond);
        Assert.IsNull(config.PermitRemoteOpen);
        Assert.IsNull(config.LocalSocketPath);
        Assert.IsNull(config.RemoteSocketPath);
        Assert.AreEqual(old, SonnetDbJson.Serialize(new List<TunnelConfig> { config }), "不带新字段的配置写出去与旧版同一个形状。");
    }

    private static void AssertSameAsSample(List<TunnelConfig> back)
    {
        Assert.HasCount(2, back);
        Assert.AreEqual(TunnelType.RemoteDynamicForward, back[0].Type);
        Assert.AreSequenceEqual(["localhost:8080", "*.corp.example:443"], [.. back[0].PermitRemoteOpen!]);
        Assert.AreEqual(512 * 1024L, back[0].MaxBytesPerSecond);
        Assert.IsTrue(back[0].AutoStart);
        Assert.IsNull(back[0].LocalSocketPath);
        Assert.AreEqual(TunnelType.LocalForward, back[1].Type);
        Assert.AreEqual(@"C:\Users\me\docker.sock", back[1].LocalSocketPath);
        Assert.AreEqual("/var/run/docker.sock", back[1].RemoteSocketPath);
        Assert.IsNull(back[1].MaxBytesPerSecond);
        Assert.IsNull(back[1].PermitRemoteOpen);
    }
}

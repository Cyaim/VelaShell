// SPDX-License-Identifier: MIT
// Copyright 2026 VelaShell Labs
//
// 被测规格: velashell-docs/zh/ssh/spec/07-forwarding.md §7.2.1
//
// 确认框里摆出来的「以谁登录、登录哪台」全来自远端 —— 这一组盯的是：
// 只有签名本身绑死的东西才摆出来，远端编的一律不认。

using System.Buffers;
using System.Text;
using VelaShell.Ssh.Auth;
using VelaShell.Ssh.Forwarding;
using VelaShell.Ssh.HostKeys;
using VelaShell.Ssh.Protocol;

namespace VelaShell.Ssh.Tests.Forwarding;

[TestClass]
[TestCategory("Forwarding")]
public sealed class AgentSignedDataTests
{
    [TestMethod]
    public async Task 验过的会话声明认得出目的主机()
    {
        using var key = InMemorySshSigner.GenerateEd25519();
        using var host = InMemorySshSigner.GenerateEd25519();
        byte[] sessionId = SessionId();

        AgentSignedData.SessionBinding? binding = AgentSignedData.TryReadSessionBinding(
            SessionBind(host.PublicKey, sessionId, await host.SignAsync(sessionId, SshAlgorithmNames.SshEd25519)));
        Assert.IsNotNull(binding);

        AgentSignatureRequest request = AgentSignedData.Describe(
            key.PublicKey, "k", UserAuth(sessionId, "alice", key.PublicKey), [binding]);

        Assert.AreEqual("alice", request.UserName);
        Assert.AreSequenceEqual(host.PublicKey.Blob.ToArray(), request.DestinationHostKey?.Blob.ToArray());
    }

    [TestMethod]
    public async Task 签名验不过的会话声明不认()
    {
        // 远端随手拿一把「你信任的主机」的公钥来声明，签名却不是那把主机密钥签的。
        using var trusted = InMemorySshSigner.GenerateEd25519();
        using var attacker = InMemorySshSigner.GenerateEd25519();
        byte[] sessionId = SessionId();
        byte[] forged = await attacker.SignAsync(sessionId, SshAlgorithmNames.SshEd25519);

        Assert.IsNull(AgentSignedData.TryReadSessionBinding(SessionBind(trusted.PublicKey, sessionId, forged)));
    }

    [TestMethod]
    public async Task 会话标识对不上的声明不算这次登录的目的地()
    {
        using var key = InMemorySshSigner.GenerateEd25519();
        using var host = InMemorySshSigner.GenerateEd25519();
        byte[] boundSession = SessionId();

        AgentSignedData.SessionBinding? binding = AgentSignedData.TryReadSessionBinding(
            SessionBind(host.PublicKey, boundSession, await host.SignAsync(boundSession, SshAlgorithmNames.SshEd25519)));
        Assert.IsNotNull(binding);

        AgentSignatureRequest request = AgentSignedData.Describe(
            key.PublicKey, "k", UserAuth(SessionId(), "alice", key.PublicKey), [binding]);

        Assert.AreEqual("alice", request.UserName, "用户名照样认得出");
        Assert.IsNull(request.DestinationHostKey, "另一个会话的声明说明不了这次登录去哪");
    }

    [TestMethod]
    public void Hostbound方法的目的主机取自签名输入本身()
    {
        using var key = InMemorySshSigner.GenerateEd25519();
        using var host = InMemorySshSigner.GenerateEd25519();

        AgentSignatureRequest request = AgentSignedData.Describe(
            key.PublicKey, "k", UserAuth(SessionId(), "bob", key.PublicKey, hostBound: host.PublicKey), []);

        Assert.AreEqual("bob", request.UserName);
        Assert.AreSequenceEqual(host.PublicKey.Blob.ToArray(), request.DestinationHostKey?.Blob.ToArray());
    }

    [TestMethod]
    public void 登录请求里出示的不是这把钥时不认()
    {
        using var key = InMemorySshSigner.GenerateEd25519();
        using var other = InMemorySshSigner.GenerateEd25519();

        AgentSignatureRequest request = AgentSignedData.Describe(
            key.PublicKey, "k", UserAuth(SessionId(), "root", other.PublicKey), []);

        Assert.IsFalse(request.IsUserAuthentication, "那份签名哪儿也登录不了，摆出里面的用户名只会误导人");
    }

    [TestMethod]
    public void 用户名里的控制字符被清掉()
    {
        using var key = InMemorySshSigner.GenerateEd25519();

        AgentSignatureRequest request = AgentSignedData.Describe(
            key.PublicKey, "k", UserAuth(SessionId(), "ro\u001b[2Jot‮", key.PublicKey), []);

        Assert.AreEqual("ro?[2Jot?", request.UserName);
    }

    [TestMethod]
    public void SSHSIG签名认得出命名空间()
    {
        using var key = InMemorySshSigner.GenerateEd25519();

        ArrayBufferWriter<byte> buffer = new();
        buffer.Write("SSHSIG"u8);
        SshDataWriter writer = new(buffer);
        writer.WriteUtf8String("git");
        writer.WriteUtf8String("");
        writer.WriteUtf8String("sha512");
        writer.WriteString(new byte[64]);

        AgentSignatureRequest request = AgentSignedData.Describe(key.PublicKey, "k", buffer.WrittenSpan, []);

        Assert.AreEqual("git", request.SignatureNamespace);
        Assert.IsFalse(request.IsUserAuthentication);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("x")]
    [DataRow("hello world, not an ssh structure")]
    public void 认不出来的数据什么都不填(string text)
    {
        using var key = InMemorySshSigner.GenerateEd25519();

        AgentSignatureRequest request = AgentSignedData.Describe(key.PublicKey, "k", Encoding.UTF8.GetBytes(text), []);

        Assert.AreEqual(new AgentSignatureRequest(key.PublicKey, "k"), request);
    }

    private static byte[] SessionId()
    {
        byte[] id = new byte[32];
        Random.Shared.NextBytes(id);
        return id;
    }

    private static byte[] SessionBind(SshPublicKey hostKey, byte[] sessionId, byte[] signature)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteByte(27);
        writer.WriteUtf8String("session-bind@openssh.com");
        writer.WriteString(hostKey.Blob.Span);
        writer.WriteString(sessionId);
        writer.WriteString(signature);
        writer.WriteBoolean(false);
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] UserAuth(byte[] sessionId, string user, SshPublicKey key, SshPublicKey? hostBound = null)
    {
        ArrayBufferWriter<byte> buffer = new();
        SshDataWriter writer = new(buffer);
        writer.WriteString(sessionId);
        writer.WriteByte(50);
        writer.WriteUtf8String(user);
        writer.WriteUtf8String("ssh-connection");
        writer.WriteUtf8String(hostBound is null ? "publickey" : "publickey-hostbound-v00@openssh.com");
        writer.WriteBoolean(true);
        writer.WriteUtf8String(key.KeyType);
        writer.WriteString(key.Blob.Span);
        if (hostBound is not null)
        {
            writer.WriteString(hostBound.Blob.Span);
        }
        return buffer.WrittenSpan.ToArray();
    }
}

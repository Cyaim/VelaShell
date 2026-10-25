using NSubstitute;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Ssh;
using VelaShell.Presentation.Services;

namespace VelaShell.Presentation.Tests.Services;

/// <summary>
/// SSH 工作流里的共享凭据(#550):每一跳各自解析、明文只进 ConnectionInfo、跳板取不到凭据不退回登录框。
/// </summary>
[TestClass]
public sealed class ConnectionWorkflowCredentialTests
{
    private readonly ISessionRepository _sessionRepository = Substitute.For<ISessionRepository>();
    private readonly ISshConnectionService _sshConnectionService = Substitute.For<ISshConnectionService>();
    private readonly ICredentialResolver _resolver = Substitute.For<ICredentialResolver>();
    private ConnectionInfo? _connected;

    public ConnectionWorkflowCredentialTests()
    {
        _sshConnectionService.ConnectAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>())
                             .Returns(call =>
                             {
                                 _connected = (ConnectionInfo)call[0];
                                 return new SshSession { ConnectionInfo = _connected, Status = SessionStatus.Connected };
                             });
        // 默认:没引用的原样交回,引用了的填上 root / s3cret(模拟解析器的契约)。
        _resolver.ResolveAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                 .Returns(call =>
                 {
                     var profile = (SessionProfile)call[0];
                     if (profile.CredentialSource is null)
                     {
                         return profile;
                     }
                     SessionProfile effective = profile.Clone();
                     effective.Username = string.IsNullOrWhiteSpace(profile.Username) ? "root" : profile.Username;
                     effective.Password = "s3cret";
                     return effective;
                 });
    }

    private ConnectionWorkflowService CreateService() =>
        new(_sessionRepository, _sshConnectionService, credentialResolver: _resolver);

    private static SessionProfile Referencing(string name = "web-01") =>
        new()
        {
            Name = name,
            Host = $"{name}.example.com",
            CredentialSource = CredentialReference.ForShared(Guid.NewGuid())
        };

    [TestMethod]
    public async Task ReferencingProfile_ConnectsWithResolvedCredential_AndSavesWithoutIt()
    {
        SessionProfile profile = Referencing();
        _sessionRepository.GetSessionAsync(profile.Id).Returns(profile);   // 已保存的配置:连接成功后才回写

        await CreateService().ConnectProfileAsync(profile);

        Assert.IsNotNull(_connected);
        Assert.AreEqual("root", _connected.Username);
        Assert.AreEqual("s3cret", _connected.Password);
        // 存回去的是调用方那一份:凭据的明文到不了仓储,连接自己的用户名也没被悄悄填上。
        await _sessionRepository.Received(1).SaveSessionAsync(Arg.Is<SessionProfile>(p =>
            p.Id == profile.Id && p.Password == null && p.Username == string.Empty));
    }

    /// <summary>引用共享凭据的配置可以不填用户名、不填密码:保存与连接前的校验都不该拦它。</summary>
    [TestMethod]
    public async Task ReferencingProfile_PassesValidationWithoutUsernameOrPassword()
    {
        SessionProfile profile = Referencing();
        _sessionRepository.GetSessionAsync(profile.Id).Returns(profile);   // 已保存的配置:连接成功后才回写

        await CreateService().SaveProfileAsync(profile);
        await CreateService().ConnectProfileAsync(profile);

        await _sessionRepository.Received(2).SaveSessionAsync(Arg.Any<SessionProfile>());
    }

    [TestMethod]
    public async Task EveryHopOfTheJumpChain_ResolvesItsOwnCredential()
    {
        SessionProfile jump = Referencing("bastion");
        SessionProfile target = Referencing("web-01");
        target.JumpHostProfileId = jump.Id;
        _sessionRepository.GetSessionAsync(jump.Id).Returns(jump);

        await CreateService().ConnectProfileAsync(target);

        await _resolver.Received(1).ResolveAsync(Arg.Is<SessionProfile>(p => p.Id == target.Id), Arg.Any<CancellationToken>());
        await _resolver.Received(1).ResolveAsync(Arg.Is<SessionProfile>(p => p.Id == jump.Id), Arg.Any<CancellationToken>());
        Assert.AreEqual("s3cret", _connected!.JumpHost!.Password);
    }

    /// <summary>
    /// 跳板取不到凭据:报一个说明是哪一跳的普通连接错误,而不是凭据异常 ——
    /// 后者会让连接流程退回登录框,可那个框问的是目标机的凭据。
    /// </summary>
    [TestMethod]
    public async Task JumpHopCredentialFailure_IsAConnectionError_NamingTheHop()
    {
        SessionProfile jump = Referencing("bastion");
        SessionProfile target = new() { Name = "web-01", Host = "h", Username = "root", Password = "pw", JumpHostProfileId = jump.Id };
        _sessionRepository.GetSessionAsync(jump.Id).Returns(jump);
        _resolver.ResolveAsync(Arg.Is<SessionProfile>(p => p.Id == jump.Id), Arg.Any<CancellationToken>())
                 .Returns<Task<SessionProfile>>(_ => throw new CredentialProviderException(CredentialFailure.NotFound, "shared", "gone"));

        InvalidOperationException ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => CreateService().ConnectProfileAsync(target));

        Assert.Contains("bastion", ex.Message);
        Assert.Contains("gone", ex.Message);
        Assert.IsInstanceOfType<CredentialProviderException>(ex.InnerException);
    }

    /// <summary>目标机自己取不到凭据:原样抛凭据异常,让连接流程退回登录框。</summary>
    [TestMethod]
    public async Task TargetCredentialFailure_PropagatesForTheLoginPrompt()
    {
        SessionProfile target = Referencing();
        _resolver.ResolveAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                 .Returns<Task<SessionProfile>>(_ => throw new CredentialProviderException(CredentialFailure.MissingSecret, "shared", "no password"));

        await Assert.ThrowsExactlyAsync<CredentialProviderException>(() => CreateService().ConnectProfileAsync(target));
        await _sshConnectionService.DidNotReceive().ConnectAsync(Arg.Any<ConnectionInfo>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task TestConnection_ReportsCredentialFailureAsTheResult()
    {
        _resolver.ResolveAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                 .Returns<Task<SessionProfile>>(_ => throw new CredentialProviderException(CredentialFailure.NotFound, "shared", "gone"));

        ConnectionTestResult result = await CreateService().TestConnectionAsync(Referencing());

        Assert.IsFalse(result.Success);
        Assert.AreEqual("gone", result.ErrorMessage);
    }
}

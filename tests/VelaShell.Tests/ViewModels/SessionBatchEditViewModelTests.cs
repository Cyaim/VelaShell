using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Security;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>批量修改对话框(#571):不勾不改、勾了要填对,点之前就说清楚会改几条、跳过几条。</summary>
[TestClass]
[TestCategory("SessionBatchEdit")]
public class SessionBatchEditViewModelTests
{
    private readonly ISessionRepository _repository = Substitute.For<ISessionRepository>();
    private readonly SessionProfile _a = new() { Name = "a", Host = "10.0.0.1", Username = "root", Port = 2222 };
    private readonly SessionProfile _b = new() { Name = "b", Host = "10.0.0.2", Username = "root", Port = 2222 };
    private readonly SessionProfile _ftp = new() { Name = "ftp", Host = "10.0.0.3", Username = "u", ConnectionType = ConnectionType.FTP, Port = 21 };
    private readonly SessionProfile _bastion = new() { Name = "bastion", Host = "203.0.113.1", Username = "jump" };
    private readonly SessionProfile _redis = new() { Name = "redis", Host = "10.0.0.9", ConnectionType = ConnectionType.Plugin, PluginProtocolId = "velashell.redis" };
    private readonly SharedCredential _keyCredential = new() { Id = Guid.NewGuid(), Name = "ops key", AuthMethod = AuthMethod.PrivateKey };

    private SessionBatchEditViewModel Create(params SessionProfile[] targets) =>
        new(targets, [_a, _b, _ftp, _bastion, _redis], [_keyCredential], _repository);

    [TestMethod]
    public void StartingValues_UseWhatTheTargetsShare()
    {
        SessionBatchEditViewModel same = Create(_a, _b);
        SessionBatchEditViewModel mixed = Create(_a, _ftp);

        Assert.AreEqual("root", same.Username);
        Assert.AreEqual(2222m, same.Port);
        Assert.AreEqual(string.Empty, mixed.Username, "各不相同就不替人猜");
        Assert.AreEqual(22m, mixed.Port);
        Assert.Contains("2", same.Summary);
    }

    [TestMethod]
    public async Task NothingTicked_CannotApply()
    {
        SessionBatchEditViewModel vm = Create(_a, _b);

        Assert.IsFalse(vm.HasAnyChange);
        Assert.IsFalse(await vm.ApplyCommand.CanExecute.FirstAsync());
        Assert.AreEqual(string.Empty, vm.PreviewText);
    }

    [TestMethod]
    public async Task Validation_BlocksBadPortsAndMissingChoices()
    {
        SessionBatchEditViewModel vm = Create(_a, _b);

        vm.ChangePort = true;
        vm.Port = 0;
        Assert.IsNotNull(vm.ValidationError);
        Assert.IsFalse(await vm.ApplyCommand.CanExecute.FirstAsync());
        vm.Port = 2200;
        Assert.IsNull(vm.ValidationError);

        vm.ChangeAuth = true;
        Assert.IsNotNull(vm.ValidationError, "选了共享凭据却没挑哪一条");
        vm.SelectedCredential = vm.CredentialOptions.Single();
        Assert.IsNull(vm.ValidationError);

        vm.AuthKind = SessionBatchAuthKind.PrivateKey;
        Assert.IsNotNull(vm.ValidationError, "私钥要给文件");
        vm.PrivateKeyPath = "~/.ssh/id_ed25519";
        Assert.IsNull(vm.ValidationError);
        Assert.IsTrue(await vm.ApplyCommand.CanExecute.FirstAsync());
    }

    [TestMethod]
    public void Preview_SaysWhatWillBeSkipped_BeforeApplying()
    {
        SessionBatchEditViewModel vm = Create(_a, _ftp);

        vm.ChangeAuth = true;
        vm.AuthKind = SessionBatchAuthKind.Agent;

        Assert.Contains(Strings.Format("BatchEdit_PreviewChangedFmt", 1), vm.PreviewText);
        Assert.Contains(Strings.Format("BatchEdit_PreviewAuthSkippedFmt", 1), vm.PreviewText,
            "FTP 那条用不了 Agent:预览里要有一句跳过的说明");
    }

    [TestMethod]
    public void JumpHostChoices_ExcludeTheTargetsAndNonSshConnections()
    {
        SessionBatchEditViewModel vm = Create(_a, _b);

        List<Guid?> ids = [.. vm.JumpHostOptions.Select(static o => o.Id)];
        Assert.IsNull(ids[0], "第一项是直连");
        Assert.AreSequenceEqual([null, _bastion.Id], [.. ids], SequenceOrder.InAnyOrder, "要改的这几条自己不能当跳板,FTP 与插件协议也不能");
    }

    [TestMethod]
    public async Task Apply_AStoreFailure_StaysInTheDialog_AndMarksThatSomethingWasWritten()
    {
        _repository.SaveSessionAsync(Arg.Any<SessionProfile>()).Returns<Task>(_ => throw new System.Data.DataException("segment locked"));
        SessionBatchEditViewModel vm = Create(_a, _b);
        vm.ChangeUsername = true;
        vm.Username = "deploy";

        SessionBatchEditOutcome? outcome = await vm.ApplyCommand.Execute().FirstAsync();

        Assert.IsNull(outcome);
        Assert.Contains("segment locked", vm.ErrorMessage!);
        Assert.IsTrue(vm.HasWritten);
        Assert.IsFalse(vm.IsBusy);
    }

    [TestMethod]
    public async Task Apply_SavesOnlyTheChangedProfiles_AndReportsSkips()
    {
        SessionBatchEditViewModel vm = Create(_a, _b, _ftp);
        vm.ChangeUsername = true;
        vm.Username = "deploy";
        vm.ChangeAuth = true;
        vm.AuthKind = SessionBatchAuthKind.Password;
        vm.Password = SecureStringConvert.FromPlaintext("pw");
        vm.ChangeJumpHost = true;
        vm.SelectedJumpHost = vm.JumpHostOptions.Single(o => o.Id == _bastion.Id);

        SessionBatchEditOutcome? outcome = await vm.ApplyCommand.Execute().FirstAsync();

        Assert.IsNotNull(outcome);
        Assert.AreEqual(3, outcome.Changed);
        Assert.AreEqual(0, outcome.AuthSkipped, "密码 FTP 也能用");
        Assert.AreEqual(1, outcome.JumpHostSkipped, "FTP 不走跳板");
        await _repository.Received(1).SaveSessionAsync(Arg.Is<SessionProfile>(p =>
            p.Id == _a.Id && p.Username == "deploy" && p.Password == "pw" && p.JumpHostProfileId == _bastion.Id));
        await _repository.Received(1).SaveSessionAsync(Arg.Is<SessionProfile>(p => p.Id == _ftp.Id && p.JumpHostProfileId == null));
        Assert.AreEqual("root", _a.Username, "保存的是副本,传进来的对象不动");
    }

    [TestMethod]
    public async Task Tailscale_ClearsOldSecretsAndReference_AndSkipsNonSshTargets()
    {
        _a.CredentialSource = CredentialReference.ForShared(_keyCredential.Id);
        _a.Password = "old-password";
        _a.PrivateKeyPath = "/old-key";
        _a.PrivateKeyPassphrase = "old-passphrase";
        _a.CertificatePath = "/old-cert";
        _a.RememberPassword = true;
        var sftp = new SessionProfile { Name = "sftp", Host = "files", Username = "ops", ConnectionType = ConnectionType.SFTP };
        SessionBatchEditViewModel vm = Create(_a, sftp, _ftp, _redis);
        vm.ChangeAuth = true;
        vm.SelectedCredential = vm.CredentialOptions.Single();
        vm.Password = SecureStringConvert.FromPlaintext("hidden-password");
        vm.PrivateKeyPath = "/hidden-key";
        vm.PrivateKeyPassphrase = "hidden-passphrase";
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        await vm.SelectAuthKindCommand.Execute(SessionBatchAuthKind.Tailscale).FirstAsync();

        Assert.IsTrue(vm.IsTailscaleAuth);
        Assert.IsFalse(vm.IsPasswordAuth);
        Assert.IsFalse(vm.IsKeyAuth);
        Assert.IsFalse(vm.IsAgentAuth);
        Assert.IsFalse(vm.IsSharedCredentialAuth);
        Assert.Contains(nameof(SessionBatchEditViewModel.IsTailscaleAuth), raised);
        Assert.IsNull(vm.ValidationError);
        Assert.IsTrue(await vm.ApplyCommand.CanExecute.FirstAsync());
        Assert.IsNull(vm.BuildEdit().Auth!.Credential);
        Assert.IsNull(vm.BuildEdit().Auth!.Password);
        List<SessionProfile> saved = [];
        _repository.SaveSessionAsync(Arg.Do<SessionProfile>(saved.Add)).Returns(Task.CompletedTask);

        SessionBatchEditOutcome? outcome = await vm.ApplyCommand.Execute().FirstAsync();

        Assert.IsNotNull(outcome);
        Assert.AreEqual(2, outcome.Changed);
        Assert.AreEqual(2, outcome.AuthSkipped);
        Assert.HasCount(2, saved);
        foreach (SessionProfile profile in saved)
        {
            Assert.AreEqual(AuthMethod.Tailscale, profile.AuthMethod);
            Assert.IsNull(profile.CredentialSource);
            Assert.IsFalse(CredentialMaterial.HasInline(profile));
            Assert.IsNull(profile.Password);
            Assert.IsNull(profile.PrivateKeyPath);
            Assert.IsNull(profile.PrivateKeyPassphrase);
            Assert.IsNull(profile.CertificatePath);
            Assert.IsFalse(profile.RememberPassword);
        }
        Assert.AreEqual("root", saved.Single(p => p.Id == _a.Id).Username);
        Assert.AreEqual("ops", saved.Single(p => p.Id == sftp.Id).Username);
        Assert.AreEqual("old-password", _a.Password, "原配置不被批量编辑就地改写");
    }

    [TestMethod]
    public async Task TailscaleSharedEntry_IsRejectedInsteadOfAppliedAsSharedCredential()
    {
        var credential = new SharedCredential { Name = "invalid", AuthMethod = AuthMethod.Tailscale };
        var vm = new SessionBatchEditViewModel([_a], [_a], [credential], _repository)
        {
            ChangeAuth = true
        };
        vm.SelectedCredential = vm.CredentialOptions.Single();

        Assert.AreEqual(Strings.Get("SharedCred_ErrTailscaleUnsupported"), vm.ValidationError);
        Assert.IsFalse(await vm.ApplyCommand.CanExecute.FirstAsync());
    }
}

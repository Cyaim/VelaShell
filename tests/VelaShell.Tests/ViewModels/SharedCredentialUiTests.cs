using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Presentation.Services;
using VelaShell.Security;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 共享凭据(#550)在界面层的规则:登录框怎么说、输入落到哪,连接配置页的凭据来源,
/// 编辑框、设置页,以及连接流程何时退回登录框。断言一律比对本地化资源。
/// </summary>
[TestClass]
public sealed class SharedCredentialUiTests
{
    private static SharedCredential Credential(string username = "admin", string? password = "s3cret") =>
        new() { Name = "switches", Username = username, Password = password };

    private static SessionProfile Referencing(SharedCredential credential, string username = "") =>
        new()
        {
            Name = "sw-01",
            Host = "10.0.0.1",
            Username = username,
            CredentialSource = CredentialReference.ForShared(credential.Id)
        };

    private static AuthenticationResult Result(string username, string? password, bool update) =>
        new(username, AuthMethod.Password, SecureStringConvert.FromPlaintext(password), null, null, null, true, update);

    // ———— 登录框:为什么又来问 ————

    [TestMethod]
    public void Context_DistinguishesRejectedIncompleteMissingAndRetry()
    {
        SharedCredential complete = Credential();
        SharedCredential noSecret = Credential(password: null);

        Assert.AreEqual(SharedCredentialPromptReason.Rejected,
            SharedCredentialPrompt.BuildContext(Referencing(complete), complete, 3).Reason);
        Assert.AreEqual(SharedCredentialPromptReason.Incomplete,
            SharedCredentialPrompt.BuildContext(Referencing(noSecret), noSecret, 3).Reason);
        Assert.AreEqual(SharedCredentialPromptReason.Incomplete,
            SharedCredentialPrompt.BuildContext(Referencing(Credential(username: "")), Credential(username: ""), 3).Reason,
            "凭据与连接都没有用户名,也算不完整");
        Assert.AreEqual(SharedCredentialPromptReason.Missing,
            SharedCredentialPrompt.BuildContext(Referencing(complete), null, 0).Reason);
        SessionProfile retried = Referencing(complete);
        retried.Password = "typed";
        Assert.AreEqual(SharedCredentialPromptReason.None,
            SharedCredentialPrompt.BuildContext(retried, complete, 3).Reason, "本次手输的又被拒了:不再复述原因");
    }

    [TestMethod]
    public void Dialog_ExplainsTheReason_AndDefaultsTheUpdateBoxCarefully()
    {
        var rejected = new AuthenticationDialogViewModel("h", 22, "admin",
            sharedCredential: new("switches", 40, SharedCredentialPromptReason.Rejected));
        var incomplete = new AuthenticationDialogViewModel("h", 22, "admin",
            sharedCredential: new("switches", 40, SharedCredentialPromptReason.Incomplete));
        var missing = new AuthenticationDialogViewModel("h", 22, "admin",
            sharedCredential: new(string.Empty, 0, SharedCredentialPromptReason.Missing));

        Assert.AreEqual(Strings.Format("Auth_SharedRejected", "switches"), rejected.SharedCredentialNotice);
        Assert.AreEqual(Strings.Format("Auth_UpdateShared", "switches", 40), rejected.UpdateSharedCredentialText);
        // 被拒时可能只是这一台密码不同:默认不动另外 39 台。
        Assert.IsFalse(rejected.UpdateSharedCredential);
        // 不完整时用户就是来补它的:默认存回去。
        Assert.IsTrue(incomplete.UpdateSharedCredential);
        Assert.IsFalse(missing.CanUpdateSharedCredential, "凭据已经没了,无处可存");
        Assert.AreEqual(Strings.Get("Auth_SharedMissing"), missing.SharedCredentialNotice);
        // 引用共享凭据的连接自己不存密码:「记住密码」不显示。
        Assert.IsFalse(rejected.ShowRememberPassword);
        Assert.IsTrue(new AuthenticationDialogViewModel("h", 22, "root").ShowRememberPassword);
    }

    [TestMethod]
    public async Task Dialog_ReportsTheUpdateChoiceInItsResult()
    {
        var vm = new AuthenticationDialogViewModel("h", 22, "admin",
            sharedCredential: new("switches", 2, SharedCredentialPromptReason.Rejected));
        await vm.NextCommand.Execute().FirstAsync();
        vm.Password = SecureStringConvert.FromPlaintext("new-pass");
        vm.UpdateSharedCredential = true;

        AuthenticationResult result = await vm.LoginCommand.Execute().FirstAsync();

        Assert.IsTrue(result.UpdateSharedCredential);
    }

    // ———— 登录框:输入落到哪 ————

    [TestMethod]
    public void Apply_ReturnsACopy_AndKeepsAnInheritedUsernameInherited()
    {
        SharedCredential credential = Credential();
        SessionProfile profile = Referencing(credential);

        SessionProfile copy = SharedCredentialPrompt.Apply(profile, credential, Result("admin", null, update: false), "typed", out SharedCredential? updated);

        Assert.AreNotSame(profile, copy);
        Assert.IsNull(profile.Password, "传入的那一份(会话树缓存里的)不能被改");
        Assert.AreEqual("typed", copy.Password);
        // 用户名与凭据相同:不往连接上写 —— 连上之后这份配置会被存回去,写了就从「跟着凭据走」变成了覆盖。
        Assert.AreEqual(string.Empty, copy.Username);
        Assert.IsNull(updated);
    }

    /// <summary>这个功能的核心:改完服务器密码,在登录框里输一次、勾一下,所有引用它的连接一起换上。</summary>
    [TestMethod]
    public void Apply_WithUpdate_HandsBackTheCredentialToSave()
    {
        SharedCredential credential = Credential();
        SessionProfile profile = Referencing(credential);

        SessionProfile copy = SharedCredentialPrompt.Apply(profile, credential, Result("admin", null, update: true), "new-pass", out SharedCredential? updated);

        Assert.IsNotNull(updated);
        Assert.AreEqual(credential.Id, updated.Id);
        Assert.AreEqual("new-pass", updated.Password);
        Assert.AreEqual("s3cret", credential.Password, "传进来的凭据不改,改的是副本");
        Assert.AreEqual(string.Empty, copy.Username);
        Assert.AreEqual("new-pass", copy.Password);
    }

    [TestMethod]
    public void Apply_WithoutAReference_BehavesLikeBefore()
    {
        var profile = new SessionProfile { Name = "p", Host = "h", RememberPassword = true };

        SessionProfile copy = SharedCredentialPrompt.Apply(profile, null,
            new AuthenticationResult("root", AuthMethod.Password, null, null, null, null, false), "pw", out SharedCredential? updated);

        Assert.AreEqual("root", copy.Username);
        Assert.AreEqual("pw", copy.Password);
        Assert.IsFalse(copy.RememberPassword);
        Assert.IsNull(profile.Password);
        Assert.IsNull(updated);
    }

    [TestMethod]
    public void InitialUsername_PrefersTheConnectionsOwn()
    {
        SharedCredential credential = Credential();

        Assert.AreEqual("admin", SharedCredentialPrompt.InitialUsername(Referencing(credential), credential));
        Assert.AreEqual("ops", SharedCredentialPrompt.InitialUsername(Referencing(credential, "ops"), credential));
    }

    [TestMethod]
    public void Apply_Tailscale_UsesTypedUsername_AndNeverUpdatesSharedCredential()
    {
        SharedCredential credential = Credential();
        SessionProfile profile = Referencing(credential);
        profile.Password = "old-password";
        profile.PrivateKeyPath = "/old-key";
        profile.PrivateKeyPassphrase = "old-passphrase";
        profile.CertificatePath = "/old-cert";
        profile.RememberPassword = true;
        profile.JumpHostProfileId = Guid.NewGuid();
        var result = new AuthenticationResult("admin", AuthMethod.Tailscale,
            SecureStringConvert.FromPlaintext("ignored"), "/ignored-key", "ignored", "/ignored-cert", true, true);

        SessionProfile copy = SharedCredentialPrompt.Apply(profile, credential, result, "ignored", out SharedCredential? updated);

        Assert.AreEqual("admin", copy.Username, "即使与共享用户名相同,Tailscale 仍保存自己的系统用户名");
        Assert.AreEqual(AuthMethod.Tailscale, copy.AuthMethod);
        Assert.IsFalse(CredentialMaterial.HasInline(copy));
        Assert.IsNull(copy.Password);
        Assert.IsNull(copy.PrivateKeyPath);
        Assert.IsNull(copy.PrivateKeyPassphrase);
        Assert.IsNull(copy.CertificatePath);
        Assert.IsNull(copy.CredentialSource);
        Assert.IsFalse(copy.RememberPassword);
        Assert.AreEqual(profile.JumpHostProfileId, copy.JumpHostProfileId);
        Assert.IsNull(updated);
        Assert.AreEqual("old-password", profile.Password);
        Assert.AreEqual("s3cret", credential.Password);

        profile.AuthMethod = AuthMethod.Tailscale;
        Assert.AreEqual(string.Empty, SharedCredentialPrompt.InitialUsername(profile, credential));
    }

    [TestMethod]
    [DataRow(ConnectionType.SSH)]
    [DataRow(ConnectionType.SFTP)]
    public async Task ProfileDialog_Tailscale_LoadsInline_RequiresOwnUsername_AndReopensWithoutSecrets(ConnectionType type)
    {
        SharedCredential credential = Credential();
        SessionProfile existing = Referencing(credential);
        existing.ConnectionType = type;
        existing.AuthMethod = AuthMethod.Tailscale;
        existing.Password = "old-password";
        existing.PrivateKeyPath = "/old-key";
        existing.PrivateKeyPassphrase = "old-passphrase";
        existing.CertificatePath = "/old-cert";
        var vm = new ConnectionProfileViewModel(existing, sharedCredentials: RepositoryWith(credential));
        await vm.LoadSharedCredentialsAsync();

        Assert.IsTrue(vm.IsTailscaleAuth);
        Assert.IsTrue(vm.ShowTailscaleHint);
        Assert.IsTrue(vm.ShowAuthMethodSelector);
        Assert.IsFalse(vm.UsesSharedCredential);
        Assert.IsNull(vm.SelectedCredentialSource.Reference);
        Assert.IsFalse(vm.ShowCredentialSourceSelector);
        Assert.IsFalse(vm.SharedCredentialProvidesUsername);
        Assert.IsFalse(vm.ShowInlinePassword);
        Assert.IsFalse(vm.ShowInlineKeyFields);
        Assert.IsFalse(vm.ShowAgentHint);
        Assert.IsNotNull(vm.UsernameError);
        Assert.IsFalse(await vm.SaveCommand.CanExecute.FirstAsync());

        vm.Username = "ops";
        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNotNull(saved);
        Assert.AreEqual(AuthMethod.Tailscale, saved.AuthMethod);
        Assert.AreEqual(type, saved.ConnectionType);
        Assert.AreEqual("ops", saved.Username);
        Assert.IsFalse(CredentialMaterial.HasInline(saved));
        Assert.IsNull(saved.Password);
        Assert.IsNull(saved.PrivateKeyPath);
        Assert.IsNull(saved.PrivateKeyPassphrase);
        Assert.IsNull(saved.CertificatePath);
        Assert.IsNull(saved.CredentialSource);
        Assert.IsFalse(saved.RememberPassword);
        Assert.AreEqual("old-password", existing.Password, "编辑与保存不修改输入配置");

        var reopened = new ConnectionProfileViewModel(saved, sharedCredentials: RepositoryWith(credential));
        await reopened.LoadSharedCredentialsAsync();
        Assert.IsTrue(reopened.IsTailscaleAuth);
        Assert.IsTrue(reopened.ShowAuthMethodSelector);
        Assert.AreEqual("ops", reopened.Username);
        Assert.IsNull(reopened.Password);
        Assert.IsNull(reopened.PrivateKeyPath);
        Assert.IsNull(reopened.CertificatePath);
    }

    [TestMethod]
    public async Task ProfileDialog_SwitchingToTailscale_DropsSharedSourceWithoutCopyingItsUsername()
    {
        SharedCredential credential = Credential();
        var vm = new ConnectionProfileViewModel(Referencing(credential), sharedCredentials: RepositoryWith(credential));
        await vm.LoadSharedCredentialsAsync();
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        await vm.SelectAuthMethodCommand.Execute(AuthMethod.Tailscale).FirstAsync();

        Assert.IsTrue(vm.IsTailscaleAuth);
        Assert.IsNull(vm.SelectedCredentialSource.Reference);
        Assert.IsFalse(vm.UsesSharedCredential);
        Assert.AreEqual(string.Empty, vm.Username);
        Assert.IsNotNull(vm.UsernameError);
        Assert.Contains(nameof(ConnectionProfileViewModel.IsTailscaleAuth), raised);
        Assert.Contains(nameof(ConnectionProfileViewModel.ShowTailscaleHint), raised);
        Assert.IsFalse(await vm.SaveCommand.CanExecute.FirstAsync());

        await vm.SelectAuthMethodCommand.Execute(AuthMethod.Password).FirstAsync();
        Assert.IsTrue(vm.ShowCredentialSourceSelector);
        Assert.IsTrue(vm.ShowInlinePassword);
        Assert.IsFalse(vm.ShowTailscaleHint);
    }

    [TestMethod]
    public async Task ExternalTailscaleSharedCredential_IsExplicitlyRejectedByProfileAndEditor()
    {
        SharedCredential credential = Credential();
        credential.AuthMethod = AuthMethod.Tailscale;
        var vm = new ConnectionProfileViewModel(Referencing(credential), sharedCredentials: RepositoryWith(credential));
        await vm.LoadSharedCredentialsAsync();

        Assert.AreEqual(Strings.Get("SharedCred_ErrTailscaleUnsupported"), vm.CredentialSourceError);
        Assert.AreEqual(Strings.Format("Profile_CredSummary", "admin", Strings.Get("Profile_TailscaleAuth")), vm.SharedCredentialSummary);
        Assert.IsFalse(await vm.SaveCommand.CanExecute.FirstAsync());
        Assert.AreEqual(AuthMethod.Password, vm.AuthMethod, "非法共享条目不能改写会话认证方式");

        var editor = new SharedCredentialEditorViewModel(credential, []);
        Assert.AreEqual(AuthMethod.Tailscale, editor.AuthMethod);
        Assert.IsFalse(editor.IsPasswordAuth);
        Assert.AreEqual(Strings.Get("SharedCred_ErrTailscaleUnsupported"), editor.SecretError);
        Assert.IsFalse(await editor.SaveCommand.CanExecute.FirstAsync());
    }

    [TestMethod]
    [DataRow(AuthMethod.Password)]
    [DataRow(AuthMethod.PrivateKey)]
    public async Task LoginRetry_UnsupportedSharedCredential_PreservesOwnAuthenticationAndRequiresUsername(AuthMethod method)
    {
        SharedCredential credential = Credential();
        credential.AuthMethod = AuthMethod.Tailscale;
        SessionProfile profile = Referencing(credential);
        profile.AuthMethod = method;
        var context = SharedCredentialPrompt.BuildContext(profile, credential, 2);
        AuthenticationDialogViewModel vm = SharedCredentialPrompt.CreateDialog(profile, credential, null, context);

        Assert.AreEqual(string.Empty, vm.Username);
        Assert.AreEqual(method == AuthMethod.Password, vm.IsPasswordMethod);
        Assert.AreEqual(method == AuthMethod.PrivateKey, vm.IsKeyMethod);
        Assert.IsFalse(vm.IsTailscaleMethod);
        Assert.IsFalse(await vm.NextCommand.CanExecute.FirstAsync());
        Assert.IsFalse(await vm.LoginCommand.CanExecute.FirstAsync());
        Assert.AreEqual(Strings.Get("SharedCred_ErrTailscaleUnsupported"), vm.SharedCredentialNotice);
        Assert.IsTrue(vm.HasSharedCredentialNotice);
        Assert.IsFalse(vm.CanUpdateSharedCredential);

        vm.Username = credential.Username;
        await vm.NextCommand.Execute().FirstAsync();
        using var password = SecureStringConvert.FromPlaintext("typed");
        vm.Password = password;
        vm.PrivateKeyPath = "/typed-key";
        vm.UpdateSharedCredential = true;
        AuthenticationResult result = await vm.LoginCommand.Execute().FirstAsync();
        using (result.Password)
        {
            Assert.AreEqual(method, result.AuthMethod);
            Assert.IsFalse(result.UpdateSharedCredential);
            SessionProfile copy = SharedCredentialPrompt.Apply(profile, credential,
                result with { UpdateSharedCredential = true }, "typed", out SharedCredential? updated);
            Assert.AreEqual(method, copy.AuthMethod);
            Assert.AreEqual(credential.Username, copy.Username, "输入即使与非法来源相同也必须成为连接自己的用户名");
            Assert.IsNull(updated, "不能通过登录重试改写非法共享条目");
            Assert.AreEqual(AuthMethod.Tailscale, credential.AuthMethod);
        }
    }

    [TestMethod]
    public async Task LoginRetry_UnsupportedSharedCredential_AllowsExplicitTailscaleRecovery()
    {
        SharedCredential credential = Credential();
        credential.AuthMethod = AuthMethod.Tailscale;
        SessionProfile profile = Referencing(credential);
        AuthenticationDialogViewModel vm = SharedCredentialPrompt.CreateDialog(profile, credential, null,
            SharedCredentialPrompt.BuildContext(profile, credential, 2));

        await vm.SelectTailscaleCommand.Execute().FirstAsync();
        Assert.IsFalse(await vm.LoginCommand.CanExecute.FirstAsync());
        Assert.AreEqual(Strings.Get("SharedCred_ErrTailscaleUnsupported"), vm.SharedCredentialNotice);
        vm.Username = "ops";
        await vm.NextCommand.Execute().FirstAsync();
        AuthenticationResult result = await vm.LoginCommand.Execute().FirstAsync();
        SessionProfile copy = SharedCredentialPrompt.Apply(profile, credential, result, null, out SharedCredential? updated);

        Assert.AreEqual(AuthMethod.Tailscale, copy.AuthMethod);
        Assert.AreEqual("ops", copy.Username);
        Assert.IsNull(copy.CredentialSource);
        Assert.IsFalse(CredentialMaterial.HasInline(copy));
        Assert.IsNull(updated);
    }

    [TestMethod]
    public async Task LoginRetry_SupportedSharedKey_StillPrefillsAndCompletesMissingUsername()
    {
        SharedCredential credential = Credential(username: "");
        credential.AuthMethod = AuthMethod.PrivateKey;
        credential.PrivateKeyPath = "/shared-key";
        SessionProfile profile = Referencing(credential);
        AuthenticationDialogViewModel vm = SharedCredentialPrompt.CreateDialog(profile, credential, null,
            SharedCredentialPrompt.BuildContext(profile, credential, 2));

        Assert.IsTrue(vm.IsKeyMethod);
        Assert.AreEqual(Strings.Format("Auth_SharedIncomplete", credential.Name), vm.SharedCredentialNotice);
        Assert.IsTrue(vm.CanUpdateSharedCredential);
        vm.Username = "ops";
        await vm.NextCommand.Execute().FirstAsync();
        AuthenticationResult result = await vm.LoginCommand.Execute().FirstAsync();
        SessionProfile copy = SharedCredentialPrompt.Apply(profile, credential, result, null, out SharedCredential? updated);

        Assert.IsNotNull(updated);
        Assert.AreEqual("ops", updated.Username);
        Assert.AreEqual(AuthMethod.PrivateKey, updated.AuthMethod);
        Assert.AreEqual("/shared-key", copy.PrivateKeyPath);
        Assert.AreEqual(string.Empty, copy.Username);
    }

    // ———— 连接配置页:凭据来源 ————

    private static ISharedCredentialRepository RepositoryWith(params SharedCredential[] credentials)
    {
        ISharedCredentialRepository repository = Substitute.For<ISharedCredentialRepository>();
        repository.GetAllAsync().Returns([.. credentials]);
        return repository;
    }

    [TestMethod]
    public async Task ProfileDialog_SelectsTheReferencedCredential_AndSavesTheReferenceWithoutMaterial()
    {
        SharedCredential credential = Credential();
        SessionProfile existing = Referencing(credential);
        var vm = new ConnectionProfileViewModel(existing, sharedCredentials: RepositoryWith(credential));

        await vm.LoadSharedCredentialsAsync();

        Assert.IsTrue(vm.UsesSharedCredential);
        Assert.AreEqual(credential, vm.SelectedCredentialSource.Credential);
        Assert.IsFalse(vm.ShowInlinePassword, "密码在凭据里:口令框收起");
        Assert.IsFalse(vm.ShowAuthMethodSelector);
        Assert.IsNull(vm.UsernameError, "凭据自带用户名时,连接自己可以不填");
        Assert.AreEqual(Strings.Format("Profile_CredUsernameInherit", "admin"), vm.UsernamePlaceholder);

        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();

        Assert.IsNotNull(saved);
        Assert.AreEqual(existing.CredentialSource, saved.CredentialSource);
        Assert.IsNull(saved.Password);
    }

    [TestMethod]
    public async Task ProfileDialog_PickingACredential_DropsTheMatchingUsernameAndHiddenMaterial()
    {
        SharedCredential credential = Credential();
        var vm = new ConnectionProfileViewModel(sharedCredentials: RepositoryWith(credential))
        {
            Host = "10.0.0.9",
            Username = "admin",
            Password = SecureStringConvert.FromPlaintext("old")
        };
        await vm.LoadSharedCredentialsAsync();

        vm.SelectedCredentialSource = vm.CredentialSources.Single(o => o.Credential?.Id == credential.Id);
        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();

        Assert.AreEqual(string.Empty, vm.Username, "与凭据相同的用户名清掉,让这条连接跟着凭据走");
        Assert.IsNotNull(saved);
        Assert.AreEqual(CredentialReference.ForShared(credential.Id), saved.CredentialSource);
        // 收起来的口令框里那份旧值不能跟着存(也不能被「测试」当成手输去连)。
        Assert.IsNull(saved.Password);
    }

    [TestMethod]
    public async Task ProfileDialog_SwitchingBackToInline_CopiesTheCredentialIntoTheForm()
    {
        SharedCredential credential = Credential();
        var vm = new ConnectionProfileViewModel(Referencing(credential), sharedCredentials: RepositoryWith(credential));
        await vm.LoadSharedCredentialsAsync();

        vm.SelectedCredentialSource = vm.CredentialSources[0];

        Assert.IsFalse(vm.UsesSharedCredential);
        Assert.AreEqual("admin", vm.Username);
        Assert.AreEqual("s3cret", SecureStringConvert.ToPlaintext(vm.Password));
        Assert.IsTrue(vm.ShowInlinePassword);
    }

    [TestMethod]
    public async Task ProfileDialog_FtpWithAKeyCredential_CannotBeSaved()
    {
        var key = new SharedCredential { Name = "key", Username = "root", AuthMethod = AuthMethod.PrivateKey, PrivateKeyPath = "/k" };
        var existing = new SessionProfile
        {
            Name = "ftp",
            Host = "f",
            ConnectionType = ConnectionType.FTP,
            Port = 21,
            CredentialSource = CredentialReference.ForShared(key.Id)
        };
        var vm = new ConnectionProfileViewModel(existing, sharedCredentials: RepositoryWith(key));

        await vm.LoadSharedCredentialsAsync();

        Assert.AreEqual(Strings.Get("Profile_ErrCredNotPassword"), vm.CredentialSourceError);
        Assert.IsFalse(await vm.SaveCommand.CanExecute.FirstAsync());
    }

    [TestMethod]
    public async Task ProfileDialog_DeletedCredential_IsShownAndBlocksSaving()
    {
        SessionProfile existing = Referencing(Credential(), "admin");
        var vm = new ConnectionProfileViewModel(existing, sharedCredentials: RepositoryWith());

        await vm.LoadSharedCredentialsAsync();

        Assert.AreEqual(Strings.Get("Profile_CredSourceMissing"), vm.SelectedCredentialSource.Display);
        Assert.AreEqual(Strings.Get("Profile_ErrCredMissing"), vm.CredentialSourceError);
    }

    /// <summary>没有凭据仓储(设计期、单测)时只是选不了,已有的引用照样存回去。</summary>
    [TestMethod]
    public async Task ProfileDialog_WithoutRepository_KeepsAnExistingReference()
    {
        SessionProfile existing = Referencing(Credential(), "admin");
        var vm = new ConnectionProfileViewModel(existing);

        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();

        Assert.IsFalse(vm.ShowCredentialSourceSelector);
        Assert.AreEqual(existing.CredentialSource, saved!.CredentialSource);
    }

    /// <summary>口令框跟着认证方式显隐(原先切到私钥页时漏发通知,口令框留在原地)。</summary>
    [TestMethod]
    public void ProfileDialog_PasswordFieldFollowsTheAuthMethod()
    {
        var vm = new ConnectionProfileViewModel();
        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        vm.AuthMethod = AuthMethod.PrivateKey;

        Assert.Contains(nameof(ConnectionProfileViewModel.ShowPasswordField), raised);
        Assert.IsFalse(vm.ShowPasswordField);
    }

    // ———— 编辑框 ————

    [TestMethod]
    public void Editor_RequiresNameAndMaterialForTheChosenMethod()
    {
        var vm = new SharedCredentialEditorViewModel(null, []);

        Assert.AreEqual(Strings.Get("SharedCred_ErrNameRequired"), vm.NameError);
        Assert.AreEqual(Strings.Get("SharedCred_ErrPasswordRequired"), vm.SecretError);

        vm.Name = "switches";
        vm.Password = SecureStringConvert.FromPlaintext("pw");
        Assert.IsNull(vm.NameError);
        Assert.IsNull(vm.SecretError);

        vm.AuthMethod = AuthMethod.PrivateKey;
        Assert.AreEqual(Strings.Get("SharedCred_ErrKeyRequired"), vm.SecretError);

        vm.AuthMethod = AuthMethod.Agent;
        Assert.IsNull(vm.SecretError, "Agent 没有材料要填");
    }

    [TestMethod]
    public void Editor_BuildsOnlyTheFieldsOfTheChosenMethod()
    {
        var vm = new SharedCredentialEditorViewModel(null, [])
        {
            Name = " switches ",
            Username = " admin ",
            Password = SecureStringConvert.FromPlaintext("pw"),
            PrivateKeyPath = "/stale",
            CertificatePath = "/stale-cert"
        };

        SharedCredential built = vm.BuildCredential();

        Assert.AreEqual("switches", built.Name);
        Assert.AreEqual("admin", built.Username);
        Assert.AreEqual("pw", built.Password);
        Assert.IsNull(built.PrivateKeyPath, "密码认证不带私钥那几栏里残留的值");
        Assert.IsNull(built.CertificatePath);
    }

    /// <summary>迁移:一键勾出认证材料与凭据相同、还各存各的连接。</summary>
    [TestMethod]
    public async Task Editor_CheckMatching_TicksConnectionsWithTheSameCredentials()
    {
        SessionProfile same = new() { Name = "sw-01", Host = "a", Username = "admin", Password = "pw" };
        SessionProfile different = new() { Name = "sw-02", Host = "b", Username = "admin", Password = "other" };
        var vm = new SharedCredentialEditorViewModel(null, [same, different])
        {
            Name = "switches",
            Username = "admin",
            Password = SecureStringConvert.FromPlaintext("pw")
        };

        await vm.CheckMatchingCommand.Execute().FirstAsync();
        SharedCredentialEditResult result = await vm.SaveCommand.Execute().FirstAsync();

        Assert.AreSequenceEqual([same.Id], [.. result.Users]);
        Assert.AreEqual(Strings.Format("SharedCred_MatchResult", 1), vm.MatchStatus);
    }

    [TestMethod]
    public void Editor_KeyCredential_DisablesConnectionsThatOnlyTakePasswords()
    {
        SessionProfile ftp = new() { Name = "ftp", Host = "f", ConnectionType = ConnectionType.FTP };
        SessionProfile ssh = new() { Name = "ssh", Host = "s" };
        var vm = new SharedCredentialEditorViewModel(null, [ftp, ssh])
        {
            AuthMethod = AuthMethod.PrivateKey
        };

        Assert.IsFalse(vm.Users.Single(u => u.Profile.Id == ftp.Id).IsCompatible);
        Assert.IsTrue(vm.Users.Single(u => u.Profile.Id == ssh.Id).IsCompatible);
    }

    [TestMethod]
    public void Editor_CopyFromAConnection_FillsTheForm()
    {
        SessionProfile source = new()
        {
            Name = "web-01",
            Host = "w",
            Username = "deploy",
            AuthMethod = AuthMethod.PrivateKey,
            PrivateKeyPath = "/keys/deploy"
        };
        var vm = new SharedCredentialEditorViewModel(null, [source]);

        vm.SelectedCopySource = vm.CopySources.Single();

        Assert.AreEqual("deploy", vm.Username);
        Assert.AreEqual(AuthMethod.PrivateKey, vm.AuthMethod);
        Assert.AreEqual("/keys/deploy", vm.PrivateKeyPath);
        Assert.AreEqual("deploy", vm.Name, "名称空着时顺带用它的用户名");
    }

    /// <summary>从连接配置页进来(不显示清单)时,谁在用它不归这个框管:原样交回。</summary>
    [TestMethod]
    public async Task Editor_WithoutTheUsersList_KeepsTheExistingUsers()
    {
        SharedCredential credential = Credential();
        SessionProfile user = Referencing(credential);
        var vm = new SharedCredentialEditorViewModel(credential, [user], showUsers: false);

        SharedCredentialEditResult result = await vm.SaveCommand.Execute().FirstAsync();

        Assert.AreSequenceEqual([user.Id], [.. result.Users]);
    }

    // ———— 设置页 ————

    [TestMethod]
    public async Task SettingsPage_ListsCredentialsWithTheirUsage()
    {
        SharedCredential credential = Credential();
        ISessionRepository sessions = Substitute.For<ISessionRepository>();
        sessions.GetAllSessionsAsync().Returns([Referencing(credential), Referencing(credential), new SessionProfile { Name = "x" }]);
        ISharedCredentialRepository repository = RepositoryWith(credential);
        var vm = new SharedCredentialsViewModel(repository, new SharedCredentialService(repository, sessions), sessions);

        await vm.RefreshAsync();

        SharedCredentialRow row = vm.FilteredItems.Single();
        Assert.AreEqual(2, row.UsageCount);
        Assert.AreEqual(Strings.Format("SharedCred_UsageCount", 2), row.UsageText);
        Assert.AreEqual(Strings.Format("SharedCred_DeleteConfirmInUse", "switches", 2), SharedCredentialsViewModel.DeleteConfirmMessage(row));
        Assert.IsFalse(vm.IsEmpty);

        vm.SearchQuery = "nothing-matches";
        Assert.IsEmpty(vm.FilteredItems);
    }

    // ———— 连接流程 ————

    [TestMethod]
    public async Task ReferencingProfile_ConnectsWithoutAskingFirst()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService ssh = Substitute.For<ISshConnectionService>();
        workflow.ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns<Task<SshSession>>(_ => throw new InvalidOperationException("network down"));
        var vm = new MainWindowViewModel(workflow, ssh, () => FakeTerminal.Emulator());
        int prompted = 0;
        vm.InteractiveAuthenticator = p =>
        {
            prompted++;
            return Task.FromResult<SessionProfile?>(p);
        };

        await vm.TryConnectProfileAsync(Referencing(Credential()));

        Assert.AreEqual(0, prompted, "用户名与密码都由凭据提供,不该先弹登录框");
        await workflow.Received(1).ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>());
    }

    /// <summary>凭据取不到(被删了、在本机没有密码):退回登录框让用户手输,而不是带空密码去试。</summary>
    [TestMethod]
    public async Task CredentialFailure_FallsBackToTheLoginPrompt()
    {
        IConnectionWorkflowService workflow = Substitute.For<IConnectionWorkflowService>();
        ISshConnectionService ssh = Substitute.For<ISshConnectionService>();
        int calls = 0;
        workflow.ConnectProfileAsync(Arg.Any<SessionProfile>(), Arg.Any<CancellationToken>())
                .Returns<Task<SshSession>>(_ => ++calls == 1
                    ? throw new CredentialProviderException(CredentialFailure.NotFound, "shared", Strings.Get("Cred_Failure_NotFound"))
                    : throw new InvalidOperationException("stop here"));
        var vm = new MainWindowViewModel(workflow, ssh, () => FakeTerminal.Emulator());
        SessionProfile? asked = null;
        vm.InteractiveAuthenticator = p =>
        {
            asked = p;
            SessionProfile copy = p.Clone();
            copy.Username = "root";
            copy.Password = "typed";
            return Task.FromResult<SessionProfile?>(copy);
        };

        await vm.TryConnectProfileAsync(Referencing(Credential()));

        Assert.IsNotNull(asked, "凭据取不到时要问用户");
        await workflow.Received(1).ConnectProfileAsync(Arg.Is<SessionProfile>(p => p.Password == "typed"), Arg.Any<CancellationToken>());
    }
}

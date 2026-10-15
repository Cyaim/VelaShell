using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Security;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

// 断言一律比对本地化资源,不写中文字面量:这些文案会随 UI 语言变化,
// 写死就等于把整个测试类绑死在中文环境上(英文环境下会集体失败)。
[TestClass]
public sealed class AuthenticationDialogViewModelTests
{
    [TestMethod]
    public void Step1_ShowsConnectingTarget_AndFirstConnectionFingerprintHint()
    {
        var vm = new AuthenticationDialogViewModel("192.168.1.100", 22, "root");

        Assert.AreEqual(1, vm.Step);
        Assert.IsTrue(vm.IsStep1);
        Assert.AreEqual(Strings.Format("Auth_HeaderTitle", 1), vm.HeaderTitle);
        Assert.AreEqual(Strings.Format("Auth_ConnectingTo", "root@192.168.1.100:22"), vm.TargetText);
        Assert.AreEqual(Strings.Get("Auth_FingerprintFirstConnect"), vm.FingerprintText);
    }

    [TestMethod]
    public void KnownHost_ShowsStoredFingerprint()
    {
        var vm = new AuthenticationDialogViewModel("h", 22, "root",
            knownFingerprint: "SHA256:xK3fAbCdEfGhIjKlMnOpQrStUvWxYz9mPq");

        // 指纹被截断显示,故只断言前缀出现在“已信任”这条文案里。
        Assert.Contains("SHA256:xK3f", vm.FingerprintText);
        Assert.AreNotEqual(Strings.Get("Auth_FingerprintFirstConnect"), vm.FingerprintText);
        Assert.StartsWith(TrustedPrefix(), vm.FingerprintText);
    }

    /// <summary>取“已信任”文案中占位符 {0} 之前的固定前缀,用来判别走的是哪条文案。</summary>
    private static string TrustedPrefix()
    {
        string template = Strings.Get("Auth_FingerprintTrusted");
        int placeholder = template.IndexOf("{0}", StringComparison.Ordinal);
        return placeholder > 0 ? template[..placeholder] : template;
    }

    [TestMethod]
    public async Task Next_RequiresUsername_ThenAdvancesToStep2()
    {
        var vm = new AuthenticationDialogViewModel("h", 22, username: null);

        Assert.IsFalse(await vm.NextCommand.CanExecute.FirstAsync());

        vm.Username = "root";
        Assert.IsTrue(await vm.NextCommand.CanExecute.FirstAsync());

        vm.NextCommand.Execute().Subscribe();
        Assert.AreEqual(2, vm.Step);
        Assert.AreEqual(Strings.Format("Auth_HeaderTitle", 2), vm.HeaderTitle);

        // 第 2 步的目标行是裸的 user@host:port,不再套“正在连接 …”的壳。
        Assert.AreEqual("root@h:22", vm.TargetText);
        Assert.AreEqual(Strings.Format("Auth_UsernameLine", "root"), vm.UsernameLine);

        vm.BackCommand.Execute().Subscribe();
        Assert.AreEqual(1, vm.Step);
    }

    [TestMethod]
    public async Task Login_PasswordMethod_ReturnsPasswordResult()
    {
        var vm = new AuthenticationDialogViewModel("h", 2222, "root");
        vm.NextCommand.Execute().Subscribe();

        Assert.IsTrue(vm.IsPasswordMethod);
        Assert.IsFalse(await vm.LoginCommand.CanExecute.FirstAsync());

        vm.Password = SecureStringConvert.FromPlaintext("s3cret");
        vm.RememberPassword = false;
        Assert.IsTrue(await vm.LoginCommand.CanExecute.FirstAsync());

        AuthenticationResult? result = null;
        vm.LoginCommand.Subscribe(r => result = r);
        vm.LoginCommand.Execute().Subscribe();

        Assert.IsNotNull(result);
        Assert.AreEqual("root", result.Username);
        Assert.AreEqual(AuthMethod.Password, result.AuthMethod);
        Assert.AreEqual("s3cret", SecureStringConvert.ToPlaintext(result.Password));
        Assert.IsFalse(result.RememberPassword);
    }

    [TestMethod]
    public async Task Login_KeyMethod_ReturnsKeyResult()
    {
        var vm = new AuthenticationDialogViewModel("h", 22, "root");
        vm.NextCommand.Execute().Subscribe();
        vm.SelectKeyCommand.Execute().Subscribe();

        Assert.IsTrue(vm.IsKeyMethod);
        Assert.IsFalse(await vm.LoginCommand.CanExecute.FirstAsync());

        vm.PrivateKeyPath = "C:/keys/id_ed25519";
        vm.PrivateKeyPassphrase = "pp";
        Assert.IsTrue(await vm.LoginCommand.CanExecute.FirstAsync());

        AuthenticationResult? result = null;
        vm.LoginCommand.Subscribe(r => result = r);
        vm.LoginCommand.Execute().Subscribe();

        Assert.IsNotNull(result);
        Assert.AreEqual(AuthMethod.PrivateKey, result.AuthMethod);
        Assert.AreEqual("C:/keys/id_ed25519", result.PrivateKeyPath);
        Assert.AreEqual("pp", result.PrivateKeyPassphrase);
        Assert.IsNull(result.Password);
    }

    [TestMethod]
    [DataRow(AuthMethod.Password, 0)]
    [DataRow(AuthMethod.Certificate, 1)]
    [DataRow(AuthMethod.PrivateKey, 2)]
    [DataRow(AuthMethod.Tailscale, 3)]
    public void InitialMethod_MapsUiIndicesExplicitly(AuthMethod method, int index)
    {
        var vm = new AuthenticationDialogViewModel("h", 22, "ops", initialMethod: method);

        Assert.AreEqual(index, vm.MethodIndex);
        Assert.AreEqual(method == AuthMethod.Tailscale, vm.IsTailscaleMethod);
    }

    [TestMethod]
    public async Task Tailscale_RequiresOnlyOwnUsername_AndReturnsNoCredentialMaterial()
    {
        var vm = new AuthenticationDialogViewModel("h", 22, initialMethod: AuthMethod.Tailscale,
            sharedCredential: new("switches", 2, SharedCredentialPromptReason.Incomplete))
        {
            Password = SecureStringConvert.FromPlaintext("stale-password"),
            PrivateKeyPath = "/stale-key",
            PrivateKeyPassphrase = "stale-passphrase",
            CertificatePath = "/stale-cert",
            RememberPassword = true,
            UpdateSharedCredential = true
        };

        Assert.IsFalse(await vm.NextCommand.CanExecute.FirstAsync());
        Assert.IsFalse(await vm.LoginCommand.CanExecute.FirstAsync());
        Assert.IsFalse(vm.IsPasswordMethod);
        Assert.IsFalse(vm.ShowsKeyFields);
        Assert.IsFalse(vm.ShowRememberPassword);
        Assert.IsFalse(vm.CanUpdateSharedCredential);
        Assert.IsFalse(vm.HasSharedCredentialNotice);

        vm.Username = " ops ";
        await vm.NextCommand.Execute().FirstAsync();
        Assert.IsTrue(vm.IsStep2);
        Assert.IsTrue(await vm.LoginCommand.CanExecute.FirstAsync());
        AuthenticationResult result = await vm.LoginCommand.Execute().FirstAsync();

        Assert.AreEqual("ops", result.Username);
        Assert.AreEqual(AuthMethod.Tailscale, result.AuthMethod);
        Assert.IsNull(result.Password);
        Assert.IsNull(result.PrivateKeyPath);
        Assert.IsNull(result.PrivateKeyPassphrase);
        Assert.IsNull(result.CertificatePath);
        Assert.IsFalse(result.RememberPassword);
        Assert.IsFalse(result.UpdateSharedCredential);

        vm.Username = " ";
        Assert.IsFalse(await vm.LoginCommand.CanExecute.FirstAsync());
    }

    [TestMethod]
    public async Task SelectingTailscale_AndReturningToPassword_RestoresNormalCredentialRequirements()
    {
        var vm = new AuthenticationDialogViewModel("h", 22, "ops");
        await vm.SelectTailscaleCommand.Execute().FirstAsync();

        Assert.IsTrue(vm.IsTailscaleMethod);
        Assert.IsTrue(await vm.LoginCommand.CanExecute.FirstAsync());

        await vm.SelectPasswordCommand.Execute().FirstAsync();
        Assert.IsFalse(vm.IsTailscaleMethod);
        Assert.IsTrue(vm.ShowRememberPassword);
        Assert.IsFalse(await vm.LoginCommand.CanExecute.FirstAsync());
    }
}

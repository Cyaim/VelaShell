using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ReactiveUI.Builder;
using ReactiveUI.Primitives.Concurrency;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 密钥管理页的导入 / 导出回显:带口令的钥要说出来,导出成败都要看得见。
/// </summary>
[TestClass]
[TestCategory("SettingsUi")]
public sealed class SshKeyManagerViewModelTests
{
    private static readonly SshKeyInfo Key = new("id_ed25519", "ED25519", "SHA256:abc", "/home/u/.ssh/id_ed25519", "ssh-ed25519 AAAA u@h");

    static SshKeyManagerViewModelTests()
    {
        try
        {
            RxAppBuilder
                .CreateReactiveUIBuilder()
                .WithMainThreadScheduler(CurrentThreadSequencer.Instance)
                .WithCoreServices()
                .BuildApp();
        }
        catch (InvalidOperationException)
        {
            // Already initialized
        }
    }

    [TestMethod]
    public async Task Import_OfAPassphraseProtectedKey_SaysSo()
    {
        ISshKeyService service = Service();
        service.ImportKeyAsync("x", Arg.Any<CancellationToken>()).Returns(new SshKeyImportResult(Key, NeedsPassphrase: true));
        var vm = new SshKeyManagerViewModel(service);

        await vm.ImportAsync("x");

        Assert.AreEqual(Strings.Format("Msg_KeyImportedNeedsPassphrase", Key.Name), vm.StatusMessage);
    }

    [TestMethod]
    public async Task Import_OfAPlainKey_UsesThePlainMessage()
    {
        ISshKeyService service = Service();
        service.ImportKeyAsync("x", Arg.Any<CancellationToken>()).Returns(new SshKeyImportResult(Key, NeedsPassphrase: false));
        var vm = new SshKeyManagerViewModel(service);

        await vm.ImportAsync("x");

        Assert.AreEqual(Strings.Format("Msg_KeyImported", Key.Name), vm.StatusMessage);
    }

    [TestMethod]
    public async Task Import_OfADamagedKey_ShowsTheReason()
    {
        ISshKeyService service = Service();
        string reason = Strings.Format("KeySvc_ImportInvalidPrivateKey", "x");
        service.ImportKeyAsync("x", Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidDataException(reason));
        var vm = new SshKeyManagerViewModel(service);

        await vm.ImportAsync("x");

        Assert.AreEqual(Strings.Format("Msg_ImportFailed", reason), vm.StatusMessage);
    }

    [TestMethod]
    public async Task Export_ReportsWhereItWent_OrWhyItFailed()
    {
        ISshKeyService service = Service();
        var vm = new SshKeyManagerViewModel(service);

        await vm.ExportPrivateKeyAsync(Key, "/tmp/backup");
        Assert.AreEqual(Strings.Format("Msg_PrivateKeyExported", Key.Name, "/tmp/backup"), vm.StatusMessage);
        await service.Received(1).ExportPrivateKeyAsync(Key, "/tmp/backup", Arg.Any<CancellationToken>());

        service.ExportPublicKeyAsync(Key, "/ro/x.pub", Arg.Any<CancellationToken>()).ThrowsAsync(new UnauthorizedAccessException("read-only"));
        await vm.ExportPublicKeyAsync(Key, "/ro/x.pub");
        Assert.AreEqual(Strings.Format("Msg_KeyExportFailed", "read-only"), vm.StatusMessage);
    }

    private static ISshKeyService Service()
    {
        ISshKeyService service = Substitute.For<ISshKeyService>();
        service.ListKeysAsync(Arg.Any<CancellationToken>()).Returns(new List<SshKeyInfo> { Key });
        return service;
    }
}

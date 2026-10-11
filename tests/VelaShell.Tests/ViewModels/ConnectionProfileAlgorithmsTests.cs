using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Ssh;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 连接对话框里的「允许老算法」与自定义算法清单:读回、保存、写错时按钮灰掉、收起时不存。
/// </summary>
[TestClass]
[TestCategory("Ssh")]
public sealed class ConnectionProfileAlgorithmsTests
{
    [TestMethod]
    public async Task Algorithms_RoundTripThroughTheEditDialog()
    {
        var existing = new SessionProfile
        {
            Name = "core-switch",
            Host = "10.0.0.1",
            Username = "admin",
            Ssh = new() { LegacyAlgorithms = true, Ciphers = "^aes128-ctr", Macs = "+hmac-sha1" },
        };

        var vm = new ConnectionProfileViewModel(existing);
        Assert.IsTrue(vm.SshLegacyAlgorithms);
        Assert.IsTrue(vm.SshCustomAlgorithms, "存过自定义清单的配置一打开就要把清单摆出来");
        Assert.AreEqual("^aes128-ctr", vm.SshCiphers);
        Assert.AreEqual("+hmac-sha1", vm.SshMacs);
        Assert.IsNull(vm.SshKexAlgorithms);

        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();

        Assert.IsNotNull(saved?.Ssh);
        Assert.IsTrue(saved.Ssh.LegacyAlgorithms);
        Assert.AreEqual("^aes128-ctr", saved.Ssh.Ciphers);
        Assert.AreEqual("+hmac-sha1", saved.Ssh.Macs);
        Assert.IsNull(saved.Ssh.KexAlgorithms);
    }

    [TestMethod]
    public async Task OnlyTheLegacySwitch_IsEnoughToKeepTheOptions()
    {
        var vm = new ConnectionProfileViewModel { Host = "h", Username = "u", SshLegacyAlgorithms = true };

        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();

        Assert.IsTrue(saved?.Ssh?.LegacyAlgorithms, "只开了老算法也不能因为「其余几项都关着」被当成空对象丢掉");
    }

    [TestMethod]
    public async Task ABadList_DisablesSaving_AndSaysWhy()
    {
        var vm = new ConnectionProfileViewModel { Host = "h", Username = "u", SshCustomAlgorithms = true };
        Assert.IsTrue(await vm.SaveCommand.CanExecute.FirstAsync());

        vm.SshCiphers = "+aes128-cbc";

        Assert.IsFalse(await vm.SaveCommand.CanExecute.FirstAsync(), "写错的清单存下来也只是把错误推迟到连接那一刻");
        Assert.AreEqual(
            Strings.Format("Ssh_AlgoSpecInvalid", Strings.Get("Ssh_AlgoKindEncryption"),
                Strings.Format("Ssh_AlgoSpecUnimplemented", "aes128-cbc")),
            vm.SshAlgorithmsError);

        vm.SshCiphers = "+aes128-ctr";
        Assert.IsNull(vm.SshAlgorithmsError);
        Assert.IsTrue(await vm.SaveCommand.CanExecute.FirstAsync());
    }

    [TestMethod]
    public async Task CollapsingTheLists_DropsThem_AndClearsTheError()
    {
        var vm = new ConnectionProfileViewModel
        {
            Host = "h",
            Username = "u",
            SshCustomAlgorithms = true,
            SshKexAlgorithms = "not-an-algorithm",
        };
        Assert.IsNotNull(vm.SshAlgorithmsError);

        vm.SshCustomAlgorithms = false;

        Assert.IsNull(vm.SshAlgorithmsError, "收起的清单不存,也就不该挡住保存");
        SessionProfile? saved = await vm.SaveCommand.Execute().FirstAsync();
        Assert.IsNull(saved?.Ssh, "看不见的旧写法不该在下次展开前悄悄生效");
    }

    [TestMethod]
    public async Task SwitchingToFtp_ReleasesTheSaveButton()
    {
        var vm = new ConnectionProfileViewModel
        {
            Host = "h",
            Username = "u",
            SshCustomAlgorithms = true,
            SshMacs = "bogus",
        };
        Assert.IsFalse(await vm.SaveCommand.CanExecute.FirstAsync());

        vm.SelectConnectionTypeCommand.Execute(ConnectionType.FTP).Subscribe();

        Assert.IsNull(vm.SshAlgorithmsError, "FTP 没有 SSH 算法,这一处错误不该把 FTP 配置也挡住");
        Assert.IsTrue(await vm.SaveCommand.CanExecute.FirstAsync(), "按钮的可用性要跟着连接类型重新算");
    }

    [TestMethod]
    public void TheTooltips_FollowTheLegacySwitch()
    {
        var vm = new ConnectionProfileViewModel { Host = "h", Username = "u" };
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.Contains(Strings.Format("Profile_SshAlgoTipMore", "diffie-hellman-group14-sha1"),
vm.SshKexAlgorithmsTip);

        vm.SshLegacyAlgorithms = true;

        Assert.Contains(nameof(ConnectionProfileViewModel.SshKexAlgorithmsTip), changed);
        Assert.Contains(string.Join(", ", SshAlgorithmPreferences.Defaults(SshAlgorithmKind.KeyExchange, legacy: true)),
vm.SshKexAlgorithmsTip,
            "放开之后 group14-sha1 已经在默认里了");
        Assert.DoesNotContain("\n", vm.SshKexAlgorithmsTip, "没有另可加的了,第二行不出现");
    }
}

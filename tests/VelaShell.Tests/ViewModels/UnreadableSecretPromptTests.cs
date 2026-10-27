using ReactiveUI.Primitives;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 登录框认出「已存的密码解不开」并说清原因(第二轮第 20 条):是哪一份、为什么(照本机加密密钥的解析结果,§253)、怎么办。
/// </summary>
[TestClass]
public sealed class UnreadableSecretPromptTests
{
    /// <summary>载入时没解开、仍是密文原样的那一份。</summary>
    private const string Undecryptable = "enc1:AAAAAAAAAAAAAAAAAAAAAAAA";

    [TestMethod]
    public void Applies_ToTheProfilesOwnSecret_OrTheSharedOne_ButNotToWhatWasJustTyped()
    {
        var own = new SessionProfile { Name = "p", Host = "h", Username = "root", Password = Undecryptable };
        var plain = new SessionProfile { Name = "p", Host = "h", Username = "root", Password = "typed" };
        var keyed = new SessionProfile
        {
            Name = "p",
            Host = "h",
            Username = "root",
            AuthMethod = AuthMethod.PrivateKey,
            PrivateKeyPath = "/keys/id",
            PrivateKeyPassphrase = Undecryptable
        };
        var shared = new SharedCredential { Name = "ops", Username = "root", Password = Undecryptable };
        var referencing = new SessionProfile
        {
            Name = "p",
            Host = "h",
            CredentialSource = CredentialReference.ForShared(shared.Id)
        };

        Assert.IsTrue(UnreadableSecretPrompt.Applies(own, null));
        Assert.IsFalse(UnreadableSecretPrompt.Applies(plain, null));
        Assert.IsTrue(UnreadableSecretPrompt.Applies(keyed, null), "私钥口令解不开同样要说");
        Assert.IsTrue(UnreadableSecretPrompt.Applies(referencing, shared));

        referencing.Password = "typed-after-a-rejection";
        Assert.IsFalse(UnreadableSecretPrompt.Applies(referencing, shared), "本次手输过的以手输为准,不再提凭据里那份");
    }

    /// <summary>密钥链暂时打不开:说清密码没丢、解锁后重启就能读,这次也可以先手输;点名是哪个密钥链、为什么。</summary>
    [TestMethod]
    public void KeychainUnavailable_SaysItIsNotLost_AndHowToGetItBack()
    {
        string notice = UnreadableSecretPrompt.Describe(null, new MasterKeyStatus
        {
            Protection = MasterKeyProtection.PlainFile,
            KeychainName = "Secret Service",
            KeychainError = "The name org.freedesktop.secrets was not provided",
            UnavailableKeyIds = ["0123456789abcdef0123456789abcdef"]
        });

        Assert.AreEqual(Strings.Format("Auth_UnreadableSecretKeychainUnavailable", Strings.Get("Auth_UnreadableSecretOwn"),
            "Secret Service", "The name org.freedesktop.secrets was not provided"), notice);
    }

    /// <summary>密钥不在这台设备上(条目找不到、密钥文件被换过):只能重新输入并存一份;共享凭据点名是哪条。</summary>
    [TestMethod]
    public void KeyMissingOrReplaced_AsksToEnterAndSaveItAgain()
    {
        string missing = UnreadableSecretPrompt.Describe("ops", new MasterKeyStatus
        {
            Protection = MasterKeyProtection.SystemKeychain,
            KeychainName = "macOS Keychain",
            MissingKeyIds = ["0123456789abcdef0123456789abcdef"]
        });
        string replaced = UnreadableSecretPrompt.Describe(null, new MasterKeyStatus
        {
            Protection = MasterKeyProtection.UserBoundFile,
            ReplacedKeyFile = "secret.key.unreadable-20261025080000"
        });

        Assert.AreEqual(Strings.Format("Auth_UnreadableSecretKeyMissing", Strings.Format("Auth_UnreadableSecretShared", "ops")), missing);
        Assert.AreEqual(Strings.Format("Auth_UnreadableSecretKeyMissing", Strings.Get("Auth_UnreadableSecretOwn")), replaced);
    }

    /// <summary>解析结果说不出原因(从别的设备同步来的密文、数据损坏),或者压根拿不到结果:照「解不开,重新输入」说。</summary>
    [TestMethod]
    public void NoReasonKnown_StillExplainsAndAsksToReEnter()
    {
        string expected = Strings.Format("Auth_UnreadableSecretUnknown", Strings.Get("Auth_UnreadableSecretOwn"));

        Assert.AreEqual(expected, UnreadableSecretPrompt.Describe(null, new MasterKeyStatus { Protection = MasterKeyProtection.UserBoundFile }));
        Assert.AreEqual(expected, UnreadableSecretPrompt.Describe(null, null));
    }

    /// <summary>
    /// 共享凭据那份解不开:不再说成「被服务器拒绝」,而是单独一种原因;默认勾上「同时更新共享凭据」——
    /// 用户就是来补它的,存回去一次修好所有引用它的连接。
    /// </summary>
    [TestMethod]
    public void SharedCredential_Unreadable_IsItsOwnReason_AndDefaultsToSavingItBack()
    {
        var shared = new SharedCredential { Name = "ops", Username = "root", Password = Undecryptable };
        var profile = new SessionProfile { Name = "p", Host = "h", CredentialSource = CredentialReference.ForShared(shared.Id) };

        SharedCredentialPromptContext context = SharedCredentialPrompt.BuildContext(profile, shared, usageCount: 12);
        var vm = new AuthenticationDialogViewModel("h", 22, "root", sharedCredential: context,
            unreadableSecretNotice: UnreadableSecretPrompt.Describe("ops", null));

        Assert.AreEqual(SharedCredentialPromptReason.Unreadable, context.Reason);
        Assert.IsTrue(vm.UpdateSharedCredential);
        Assert.IsTrue(vm.CanUpdateSharedCredential);
        Assert.IsFalse(vm.HasSharedCredentialNotice, "原因由「解不开」那一条说,不叠两条");
        Assert.IsTrue(vm.HasUnreadableSecretNotice);
    }

    [TestMethod]
    public void DialogNotice_IsHiddenOnlyWhenNothingIsUnreadable_OrTailscaleIsChosen()
    {
        var plain = new AuthenticationDialogViewModel("h", 22, "root");
        var unreadable = new AuthenticationDialogViewModel("h", 22, "root", unreadableSecretNotice: "notice");

        Assert.IsFalse(plain.HasUnreadableSecretNotice);
        Assert.IsTrue(unreadable.HasUnreadableSecretNotice);
        unreadable.SelectTailscaleCommand.Execute().Subscribe();
        Assert.IsFalse(unreadable.HasUnreadableSecretNotice, "Tailscale 用不着那份密码");
    }
}

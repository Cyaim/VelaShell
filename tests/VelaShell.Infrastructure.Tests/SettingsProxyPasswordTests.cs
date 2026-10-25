using System.Text.Json.Nodes;
using SonnetDB.Documents;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.Persistence;

namespace VelaShell.Infrastructure.Tests;

/// <summary>
/// 设置里的代理密码落盘加密(<see cref="SonnetDbSettingsService" /> 经 <c>ISecretProtector</c>):
/// 库里躺着的是密文,交给调用方的是明文;存量明文第一次保存时换成密文;解不开的那份既不当密码交出去,也不被一次保存抹掉。
/// </summary>
[TestClass]
public sealed class SettingsProxyPasswordTests : IDisposable
{
    private readonly SonnetDbEngine _engine;
    private readonly string _testDirectory;
    private readonly AesSecretProtector _protector;

    public SettingsProxyPasswordTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"velashell_proxypw_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _engine = new(Path.Combine(_testDirectory, "sonnetdb"));
        _protector = new(Path.Combine(_testDirectory, "secret.key"));
    }

    public void Dispose()
    {
        _engine.Dispose();
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [TestMethod]
    public async Task Save_StoresTheProxyPasswordEncrypted_AndReadsItBackAsPlaintext()
    {
        var service = new SonnetDbSettingsService(_engine, protector: _protector);
        AppSettings settings = await service.GetSettingsAsync();
        UseHttpProxy(settings, "s3cret-proxy");
        await service.SaveSettingsAsync(settings);

        string raw = await RawSettingsJsonAsync();
        Assert.DoesNotContain("s3cret-proxy", raw, "库里不该有代理密码的明文");
        Assert.StartsWith("enc1:", StoredProxyPassword(raw));
        Assert.AreEqual("s3cret-proxy", service.CurrentSnapshot!.Proxy.Password, "内存里的快照是明文");
        Assert.AreEqual("s3cret-proxy", settings.Proxy.Password, "调用方手里那份对象不被改成密文");

        var reopened = new SonnetDbSettingsService(_engine, protector: _protector);
        Assert.AreEqual("s3cret-proxy", (await reopened.GetSettingsAsync()).Proxy.Password);
    }

    [TestMethod]
    public async Task LegacyPlaintext_IsReadAsIs_AndEncryptedOnTheFirstSave()
    {
        AppSettings legacy = new();
        UseHttpProxy(legacy, "old-plain");
        await WriteRawSettingsJsonAsync(SonnetDbJson.Serialize(legacy));

        var service = new SonnetDbSettingsService(_engine, protector: _protector);
        AppSettings loaded = await service.GetSettingsAsync();
        Assert.AreEqual("old-plain", loaded.Proxy.Password, "存量明文照常读得出来");
        Assert.AreEqual("old-plain", StoredProxyPassword(await RawSettingsJsonAsync()), "只读不写:没保存之前库里不动");

        loaded.TerminalFontSize = 15;
        await service.SaveSettingsAsync(loaded);

        string raw = await RawSettingsJsonAsync();
        Assert.DoesNotContain("old-plain", raw);
        Assert.AreEqual("old-plain", _protector.Unprotect(StoredProxyPassword(raw)));
    }

    [TestMethod]
    public async Task LegacySettingsFile_IsImportedWithTheProxyPasswordEncrypted()
    {
        string legacyDirectory = Path.Combine(_testDirectory, "legacy");
        Directory.CreateDirectory(legacyDirectory);
        AppSettings legacy = new();
        UseHttpProxy(legacy, "from-json-file");
        await File.WriteAllTextAsync(Path.Combine(legacyDirectory, "settings.json"), SonnetDbJson.Serialize(legacy));

        var service = new SonnetDbSettingsService(_engine, [legacyDirectory], _protector);
        Assert.AreEqual("from-json-file", (await service.GetSettingsAsync()).Proxy.Password);

        string raw = await RawSettingsJsonAsync();
        Assert.DoesNotContain("from-json-file", raw, "从旧 settings.json 导进库里的那一次就该是密文");
    }

    [TestMethod]
    public async Task UndecryptablePassword_IsHandedOutEmpty_ButSurvivesUnrelatedSaves()
    {
        await SaveWithProtectorAsync(_protector, "kept-across-keys");
        string original = StoredProxyPassword(await RawSettingsJsonAsync());

        // 换了一把本机密钥(数据目录从别处拷来、系统密钥链这次没打开):解不开。
        var otherKey = new AesSecretProtector(Path.Combine(_testDirectory, "other.key"));
        var service = new SonnetDbSettingsService(_engine, protector: otherKey);
        AppSettings loaded = await service.GetSettingsAsync();
        Assert.AreEqual(string.Empty, loaded.Proxy.Password, "解不开的密文不能当密码交给代理");

        loaded.Appearance.LastWindowWidth = 1234;
        await service.SaveSettingsAsync(loaded);

        Assert.AreEqual(original, StoredProxyPassword(await RawSettingsJsonAsync()), "一次无关的保存不能把密码抹掉");
        var backWithTheRightKey = new SonnetDbSettingsService(_engine, protector: _protector);
        Assert.AreEqual("kept-across-keys", (await backWithTheRightKey.GetSettingsAsync()).Proxy.Password,
            "密钥回来之后照常解开");
    }

    [TestMethod]
    public async Task UndecryptablePassword_IsReplacedWhenRetyped_AndDroppedWhenTheProxyChanges()
    {
        await SaveWithProtectorAsync(_protector, "old-secret");
        var otherKey = new AesSecretProtector(Path.Combine(_testDirectory, "other.key"));

        var retyped = new SonnetDbSettingsService(_engine, protector: otherKey);
        AppSettings settings = await retyped.GetSettingsAsync();
        settings.Proxy.Password = "new-secret";
        await retyped.SaveSettingsAsync(settings);
        Assert.AreEqual("new-secret", otherKey.Unprotect(StoredProxyPassword(await RawSettingsJsonAsync())),
            "重填的这一份用当前的密钥存,这台设备解得开");

        await SaveWithProtectorAsync(_protector, "old-secret");
        var moved = new SonnetDbSettingsService(_engine, protector: otherKey);
        settings = await moved.GetSettingsAsync();
        settings.Proxy.Host = "another-proxy.example";
        await moved.SaveSettingsAsync(settings);
        Assert.AreEqual(string.Empty, StoredProxyPassword(await RawSettingsJsonAsync()),
            "换了一台代理:旧密码不属于它,与导入、同步拉取同一口径");
    }

    private async Task SaveWithProtectorAsync(AesSecretProtector protector, string password)
    {
        var service = new SonnetDbSettingsService(_engine, protector: protector);
        AppSettings settings = await service.GetSettingsAsync();
        UseHttpProxy(settings, password);
        await service.SaveSettingsAsync(settings);
    }

    private static void UseHttpProxy(AppSettings settings, string password)
    {
        settings.Proxy.Type = "http";
        settings.Proxy.Host = "proxy.example";
        settings.Proxy.Port = 3128;
        settings.Proxy.Username = "alice";
        settings.Proxy.Password = password;
    }

    private Task<string> RawSettingsJsonAsync() =>
        _engine.WithCollectionAsync(SonnetDbEngine.ConfigCollection, store =>
        {
            DocumentRow? row = store.Get("settings");
            return row?.Json ?? string.Empty;
        });

    private async Task WriteRawSettingsJsonAsync(string json) =>
        await _engine.WithCollectionAsync<object?>(SonnetDbEngine.ConfigCollection, store =>
        {
            store.Upsert("settings", json);
            return null;
        });

    private static string StoredProxyPassword(string raw) =>
        JsonNode.Parse(raw)?["proxy"]?["password"]?.GetValue<string>() ?? string.Empty;
}

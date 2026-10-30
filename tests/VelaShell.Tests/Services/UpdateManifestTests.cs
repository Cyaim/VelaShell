using System.Text.Json;
using VelaShell.Services.Update;

namespace VelaShell.Tests.Services;

[TestClass]
public class UpdateManifestTests
{
    private const string SampleJson = """
        {
          "version": "0.2.0",
          "tag": "v0.2.0",
          "assets": {
            "win-x64":   { "name": "VelaShell-0.2.0-win-x64.zip",     "sha256": "aa11", "size": 100 },
            "win-arm64": { "name": "VelaShell-0.2.0-win-arm64.zip",   "sha256": "bb22", "size": 200 },
            "osx-x64":   { "name": "VelaShell-0.2.0-osx-x64.tar.gz",  "sha256": "cc33", "size": 300 },
            "osx-arm64": { "name": "VelaShell-0.2.0-osx-arm64.tar.gz","sha256": "dd44", "size": 400 },
            "linux-x64": { "name": "VelaShell-0.2.0-linux-x64.tar.gz","sha256": "ee55", "size": 500 },
            "linux-arm64":{ "name": "VelaShell-0.2.0-linux-arm64.tar.gz", "sha256": "ff66", "size": 600 }
          }
        }
        """;

    [TestMethod]
    [TestCategory("Update")]
    public void Parse_ValidManifest_ExposesAllFields()
    {
        var manifest = UpdateManifest.Parse(SampleJson);
        Assert.AreEqual("0.2.0", manifest.Version);
        Assert.AreEqual("v0.2.0", manifest.Tag);
        Assert.HasCount(6, manifest.Assets);
        UpdateAsset asset = manifest.Assets["win-x64"];
        Assert.AreEqual("VelaShell-0.2.0-win-x64.zip", asset.Name);
        Assert.AreEqual("aa11", asset.Sha256);
        Assert.AreEqual(100, asset.Size);
    }

    [TestMethod]
    [TestCategory("Update")]
    [DataRow("{\"body\":\"完整日志\\n第二行\",\"publishedAt\":\"2026-10-21T00:00:00Z\",\"htmlUrl\":\"https://github.com/owner/repo/releases/tag/v0.2.0\"}")]
    [DataRow("null")]
    [DataRow("false")]
    public void Parse_AdditionalReleaseNotes_DoesNotChangeUpdateAssets(string notes)
    {
        string json = SampleJson.Insert(SampleJson.LastIndexOf('}'), $", \"releaseNotes\": {notes}");
        var original = UpdateManifest.Parse(SampleJson);
        var extended = UpdateManifest.Parse(json);

        Assert.AreEqual(original.Version, extended.Version);
        Assert.AreEqual(original.Tag, extended.Tag);
        Assert.AreSequenceEqual([.. original.Assets], [.. extended.Assets], SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    [TestCategory("Update")]
    public void AssetForCurrentPlatform_KnownRids_ReturnsMatch()
    {
        // 测试跑在 win/osx/linux 的 x64/arm64 上,CurrentRid 一定命中样例清单。
        var manifest = UpdateManifest.Parse(SampleJson);
        string? rid = UpdateManifest.CurrentRid();
        Assert.IsNotNull(rid);
        UpdateAsset? asset = manifest.AssetForCurrentPlatform();
        Assert.IsNotNull(asset);
        Assert.AreEqual(manifest.Assets[rid].Name, asset.Name);
    }

    [TestMethod]
    [TestCategory("Update")]
    public void AssetForCurrentPlatform_MissingRid_ReturnsNull()
    {
        var manifest = UpdateManifest.Parse("""
            { "version": "0.2.0", "tag": "v0.2.0",
              "assets": { "solaris-sparc": { "name": "n", "sha256": "s", "size": 1 } } }
            """);
        Assert.IsNull(manifest.AssetForCurrentPlatform());
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Parse_MissingVersion_Throws()
    {
        Assert.ThrowsExactly<KeyNotFoundException>(() =>
            UpdateManifest.Parse("""{ "tag": "v1", "assets": { "a": { "name": "n", "sha256": "s" } } }"""));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Parse_EmptyAssets_Throws()
    {
        Assert.ThrowsExactly<FormatException>(() =>
            UpdateManifest.Parse("""{ "version": "1.0.0", "tag": "v1.0.0", "assets": {} }"""));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Parse_JsonElement_SurvivesDocumentDisposalAndMatchesStringEntryPoint()
    {
        UpdateManifest fromElement;
        using (var document = JsonDocument.Parse(SampleJson))
        {
            fromElement = UpdateManifest.Parse(document.RootElement);
        }
        var fromString = UpdateManifest.Parse(SampleJson);

        Assert.AreEqual(fromString.Version, fromElement.Version);
        Assert.AreEqual(fromString.Tag, fromElement.Tag);
        Assert.AreSequenceEqual([.. fromString.Assets], [.. fromElement.Assets], SequenceOrder.InAnyOrder);
        Assert.AreEqual(fromElement.Assets["win-x64"], fromElement.Assets["WIN-X64"]);
    }

    [TestMethod]
    [TestCategory("Update")]
    [DataRow(false)]
    [DataRow(true)]
    public void Assets_ParseAndInstallationSnapshot_CannotBeMutated(bool installationSnapshot)
    {
        UpdateManifest manifest = CreateEligibleManifest();
        if (installationSnapshot)
        {
            manifest = manifest.ForCurrentPlatformInstallation()!;
            Assert.IsNotNull(manifest);
            Assert.HasCount(1, manifest.Assets);
        }
        string rid = UpdateManifest.CurrentRid()!;
        UpdateAsset original = manifest.Assets[rid];
        Assert.IsFalse(manifest.Assets is Dictionary<string, UpdateAsset>);
        if (manifest.Assets is IDictionary<string, UpdateAsset> dictionary)
        {
            Assert.IsTrue(dictionary.IsReadOnly);
            Assert.ThrowsExactly<NotSupportedException>(() => dictionary[rid] = new("changed.zip", "bad", 0));
            Assert.ThrowsExactly<NotSupportedException>(() => dictionary.Remove(rid));
            Assert.ThrowsExactly<NotSupportedException>(dictionary.Clear);
        }
        Assert.AreEqual(original, manifest.Assets[rid]);
        Assert.AreEqual(original, manifest.Assets[rid.ToUpperInvariant()]);
        Assert.IsTrue(manifest.IsEligibleForInstallation());
    }

    [TestMethod]
    [TestCategory("Update")]
    [DataRow("1.7.1", "v1.7.1", true)]
    [DataRow("1.7.1.0", "v1.7.1", true)]
    [DataRow("1.7.1+build", "v1.7.1", true)]
    [DataRow("1.7.1-beta.1", "v1.7.1-beta.1", true)]
    [DataRow("1.7.2", "v1.7.1", false)]
    [DataRow("1.7.1-beta.1", "v1.7.1", false)]
    [DataRow("invalid", "v1.7.1", false)]
    [DataRow("1.7.1", "v1", false)]
    public void IsEligibleForInstallation_RequiresMatchingParseableVersions(string version, string tag, bool eligible)
    {
        UpdateManifest manifest = CreateEligibleManifest(version, tag);
        Assert.AreEqual(eligible, manifest.IsEligibleForInstallation());
        Assert.AreEqual(eligible, manifest.ForCurrentPlatformInstallation() != null);
    }

    [TestMethod]
    [TestCategory("Update")]
    public void IsEligibleForInstallation_MissingCurrentPlatform_IsNotEligible()
    {
        string? rid = UpdateManifest.CurrentRid();
        Assert.IsNotNull(rid);
        var manifest = UpdateManifest.Parse($$"""
            {"version":"1.7.1","tag":"v1.7.1","assets":{"unsupported-rid":
             {"name":"pkg.zip","sha256":"{{new string('a', 64)}}","size":100} } }
            """);
        Assert.IsFalse(manifest.IsEligibleForInstallation());
        Assert.IsNull(manifest.ForCurrentPlatformInstallation());
    }

    [TestMethod]
    [TestCategory("Update")]
    [DataRow("pkg.zip", 64, 'a', 1L, true)]
    [DataRow("VelaShell-1.7.1.tar.gz", 64, 'F', 8589934592L, true)]
    [DataRow("", 64, 'a', 1L, false)]
    [DataRow("../pkg.zip", 64, 'a', 1L, false)]
    [DataRow("dir/pkg.zip", 64, 'a', 1L, false)]
    [DataRow("dir\\pkg.zip", 64, 'a', 1L, false)]
    [DataRow("C:pkg.zip", 64, 'a', 1L, false)]
    [DataRow(".pkg.zip", 64, 'a', 1L, false)]
    [DataRow("pkg.zip.", 64, 'a', 1L, false)]
    [DataRow("pkg.zip?x=1", 64, 'a', 1L, false)]
    [DataRow("pkg.zip", 63, 'a', 1L, false)]
    [DataRow("pkg.zip", 65, 'a', 1L, false)]
    [DataRow("pkg.zip", 64, 'g', 1L, false)]
    [DataRow("pkg.zip", 64, 'a', 0L, false)]
    [DataRow("pkg.zip", 64, 'a', -1L, false)]
    [DataRow("pkg.zip", 64, 'a', 8589934593L, false)]
    public void IsEligibleAsset_ValidatesFileNameHashAndSize(string name, int hashLength, char hashCharacter, long size, bool eligible)
    {
        UpdateAsset asset = new(name, new string(hashCharacter, hashLength), size);
        Assert.AreEqual(eligible, UpdateManifest.IsEligibleAsset(asset));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void IsEligibleAsset_FileNameLength_IsBounded()
    {
        string hash = new('a', 64);
        Assert.IsTrue(UpdateManifest.IsEligibleAsset(new(new string('a', 255), hash, 1)));
        Assert.IsFalse(UpdateManifest.IsEligibleAsset(new(new string('a', 256), hash, 1)));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Parse_HistoricalLooseFields_RemainAcceptedButCannotBeInstalled()
    {
        var manifest = UpdateManifest.Parse("""
            {"version":"historical","tag":"v1","assets":{"WIN-X64":{"name":"../pkg.zip","sha256":"short"}}}
            """);
        Assert.AreEqual("historical", manifest.Version);
        Assert.AreEqual("v1", manifest.Tag);
        Assert.AreEqual(0L, manifest.Assets["win-x64"].Size);
        Assert.AreEqual("../pkg.zip", manifest.Assets["win-x64"].Name);
        Assert.AreEqual("short", manifest.Assets["win-x64"].Sha256);
        Assert.IsFalse(manifest.IsEligibleForInstallation());
    }

    private static UpdateManifest CreateEligibleManifest(string version = "1.7.1", string tag = "v1.7.1")
    {
        string? rid = UpdateManifest.CurrentRid();
        Assert.IsNotNull(rid);
        return UpdateManifest.Parse(JsonSerializer.Serialize(new
        {
            version,
            tag,
            assets = new Dictionary<string, object>
            {
                [rid] = new { name = "pkg.zip", sha256 = new string('a', 64), size = 100 }
            }
        }));
    }

    [TestMethod]
    [TestCategory("Update")]
    public void Parse_MalformedJson_Throws() =>
        // JsonReaderException 等派生类也算,用非严格断言。
        Assert.Throws<JsonException>(() => UpdateManifest.Parse("not json"));
}

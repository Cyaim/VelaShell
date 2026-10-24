using VelaShell.Core.Layouts;
using VelaShell.Infrastructure.Persistence;
using VelaShell.Services.Layouts;

namespace VelaShell.Tests.Services;

/// <summary>
/// 布局模板经真实的 SonnetDB 文档库落盘、关库重开之后原样读回(序列化往返)。
/// </summary>
[TestClass]
[TestCategory("DataStore")]
public sealed class LayoutTemplateStoreTests
{
    private string _root = null!;

    [TestInitialize]
    public void SetUp() =>
        _root = Path.Combine(Path.GetTempPath(), "velashell-layout-tests", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Windows 上被映射的段文件要等进程退出才删得掉;临时目录,留着无妨。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static LayoutTemplate Sample(string name)
    {
        var profile = Guid.NewGuid();
        return new()
        {
            Name = name,
            CreatedAt = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 10, 24, 9, 30, 0, TimeSpan.Zero),
            Root = new()
            {
                Orientation = LayoutOrientation.Horizontal,
                Children =
                [
                    new()
                    {
                        Proportion = 0.65,
                        ActiveIndex = 1,
                        TabsPosition = "Left",
                        Documents =
                        [
                            new() { Kind = LayoutDocumentKind.Terminal, ProfileId = profile, Title = "web-1", IsPinned = true },
                            new() { Kind = LayoutDocumentKind.Files, ProfileId = profile, Title = "web-1 files", IsActive = true },
                        ],
                    },
                    new()
                    {
                        Documents =
                        [
                            new() { Kind = LayoutDocumentKind.DualFiles, ProfileId = profile, SecondProfileId = Guid.NewGuid(), Title = "a ⇄ b" },
                            new() { Kind = LayoutDocumentKind.LocalShell, ShellId = "pwsh", Title = "PowerShell" },
                        ],
                    },
                ],
            },
        };
    }

    [TestMethod]
    public async Task Templates_RoundTripThroughSonnetDb_AndComeBackInNaturalOrder()
    {
        LayoutTemplate ten = Sample("机房 10");
        LayoutTemplate two = Sample("机房 2");
        using (var engine = new SonnetDbEngine(_root))
        {
            var store = new LayoutTemplateStore(new SonnetDbAppDataStore(engine));
            await store.SaveAsync(ten);
            await store.SaveAsync(two);
        }

        using (var engine = new SonnetDbEngine(_root))
        {
            var store = new LayoutTemplateStore(new SonnetDbAppDataStore(engine));
            IReadOnlyList<LayoutTemplate> loaded = await store.LoadAllAsync();

            Assert.AreSequenceEqual(["机房 2", "机房 10"], loaded.Select(t => t.Name).ToArray(), "自然顺序:2 在 10 前面");
            LayoutTemplate back = loaded[1];
            Assert.AreEqual(ten.Id, back.Id);
            Assert.AreEqual(ten.CreatedAt, back.CreatedAt);
            Assert.AreEqual(ten.UpdatedAt, back.UpdatedAt);
            Assert.AreEqual(LayoutTemplate.CurrentSchemaVersion, back.SchemaVersion);
            Assert.AreEqual(LayoutOrientation.Horizontal, back.Root!.Orientation);
            Assert.IsNull(back.Root.Children[1].Proportion, "均分落盘成「没写」,读回来还是均分");
            LayoutNode left = back.Root.Children[0];
            Assert.AreEqual(0.65, left.Proportion);
            Assert.AreEqual(1, left.ActiveIndex);
            Assert.AreEqual("Left", left.TabsPosition);
            Assert.IsTrue(left.Documents[0].IsPinned);
            Assert.IsTrue(left.Documents[1].IsActive);
            LayoutDocument[] expected = [.. LayoutTree.Documents(ten.Root)];
            LayoutDocument[] actual = [.. LayoutTree.Documents(back.Root)];
            Assert.AreSequenceEqual(
                expected.Select(d => (d.Kind, d.ProfileId, d.SecondProfileId, d.ShellId, d.Title, d.IsPinned, d.IsActive)).ToArray(),
                actual.Select(d => (d.Kind, d.ProfileId, d.SecondProfileId, d.ShellId, d.Title, d.IsPinned, d.IsActive)).ToArray());

            await store.DeleteAsync(two.Id);
            Assert.AreSequenceEqual([ten.Id], (await store.LoadAllAsync()).Select(t => t.Id).ToArray());
        }
    }

    [TestMethod]
    public async Task SessionLayout_IsKeptApartFromTheNamedTemplates_AndCanBeCleared()
    {
        using var engine = new SonnetDbEngine(_root);
        var store = new LayoutTemplateStore(new SonnetDbAppDataStore(engine));
        LayoutTemplate session = Sample(string.Empty);
        session.Id = Guid.Empty;

        await store.SaveLastSessionAsync(session);

        Assert.IsEmpty(await store.LoadAllAsync(), "退出时的布局不混进命名模板的列表");
        Assert.AreEqual(4, LayoutTree.Documents((await store.LoadLastSessionAsync())!.Root).Count());

        await store.SaveLastSessionAsync(null);
        Assert.IsNull(await store.LoadLastSessionAsync());
    }

    [TestMethod]
    public async Task WithoutADataStore_EverythingIsANoOp()
    {
        var store = new LayoutTemplateStore(null);

        await store.SaveAsync(Sample("x"));
        await store.SaveLastSessionAsync(Sample("y"));

        Assert.IsEmpty(await store.LoadAllAsync());
        Assert.IsNull(await store.LoadLastSessionAsync());
    }
}

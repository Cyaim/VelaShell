using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

[TestClass]
[TestCategory("Plugins")]
public sealed class GeneratedImageStoreTests
{
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3];

    [TestMethod]
    public void SaveAndLoad_RoundTripBinaryAndRejectUnsafeReferences()
    {
        using var context = new TestPluginContext();
        var store = new GeneratedImageStore(context.DataDirectory);
        ChatGeneratedImage image = store.Save(new DataContent(Png, "image/png"));
        Assert.AreSequenceEqual(Png, store.Load(image).Data.ToArray());
        Assert.AreEqual("image/png", store.Load(image).MediaType);
        Assert.ThrowsExactly<ArgumentException>(() => store.Load(new ChatGeneratedImage("../secret.png", "image/png")));
        Assert.ThrowsExactly<ArgumentException>(() => store.Load(image with { MediaType = "image/svg+xml" }));
        Assert.ThrowsExactly<ArgumentException>(() => store.Save(new DataContent(Png, "image/svg+xml")));
        Assert.ThrowsExactly<ArgumentException>(() => store.Save(new DataContent(Png, "image/jpeg")));
        Assert.ThrowsExactly<ArgumentException>(() => store.Save(new DataContent(new byte[GeneratedImageStore.MaxImageBytes + 1], "image/png")));
    }

    [TestMethod]
    public void SupportedFormats_RoundTrip()
    {
        using var context = new TestPluginContext();
        var store = new GeneratedImageStore(context.DataDirectory);
        (string type, byte[] bytes)[] formats =
        [
            ("image/jpeg", [255, 216, 255, 1]),
            ("image/gif", "GIF89a"u8.ToArray()),
            ("image/webp", "RIFF1234WEBP"u8.ToArray())
        ];
        foreach ((string type, byte[] bytes) in formats)
        {
            DataContent loaded = store.Load(store.Save(new DataContent(bytes, type)));
            Assert.AreEqual(type, loaded.MediaType);
            Assert.AreSequenceEqual(bytes, loaded.Data.ToArray());
        }
    }

    [TestMethod]
    public async Task ImagesOnlyMetadata_RoundTripsAndLimitsCount()
    {
        using var context = new TestPluginContext();
        var images = new GeneratedImageStore(context.DataDirectory);
        var refs = Enumerable.Range(0, 5).Select(_ => images.Save(new DataContent(Png, "image/png"))).ToArray();
        var store = new ChatHistoryStore(context);
        await store.InitAsync();
        string id = ChatHistoryStore.NewConversationId();
        await store.AppendAsync(id, DateTimeOffset.UtcNow, 0, "assistant", "");
        await store.AppendMetaAsync(id, 0, new ChatTurnMeta(Images: refs));
        ChatTurnMeta meta = (await store.LoadAsync(id)).Single().Meta!;
        Assert.HasCount(4, meta.Images!);
        Assert.AreEqual(refs[0], meta.Images![0]);
        Assert.AreSequenceEqual(Png, images.Load(meta.Images[0]).Data.ToArray());
    }
}

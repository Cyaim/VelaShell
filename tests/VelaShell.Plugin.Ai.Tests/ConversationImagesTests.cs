using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

[TestClass]
public sealed class ConversationImagesTests
{
    [TestMethod]
    public async Task ToolCannotEditImageFromAnotherConversationOrReadArbitraryPath()
    {
        using var context = new TestPluginContext();
        var store = new GeneratedImageStore(context.DataDirectory);
        var first = new ConversationImages(store);
        var other = new ConversationImages(store);
        var reference = store.Save(new DataContent(Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII="), "image/png"));
        first.Register(reference);
        Assert.IsFalse(first.Load(reference.FileName).Data.IsEmpty);
        Assert.ThrowsExactly<ArgumentException>(() => other.Load(reference.FileName));
        Assert.ThrowsExactly<ArgumentException>(() => first.Load("../../secret.png"));
        var turn = other.BeginTurn(new AiSettingsStore(context), "missing", (_, _) => Task.FromResult(true), CancellationToken.None);
        Assert.IsNull(turn.ResolveResult("[]"));
        Assert.IsNull(turn.ResolveResult("{\"type\":7}"));
        Assert.IsNull(turn.ResolveResult("{\"type\":\"image_generation_result\",\"imageId\":42}"));
        var edit = turn.CreateTools().OfType<AIFunction>().Single(t => t.Name == "edit_image");
        await Assert.ThrowsAsync<ArgumentException>(async () => await edit.InvokeAsync(new AIFunctionArguments
        { ["prompt"] = "edit", ["imageIds"] = new[] { reference.FileName } }));
    }

    [TestMethod]
    public async Task DefaultImageModelSurvivesGlobalSaveAndReload()
    {
        using var context = new TestPluginContext();
        var store = new AiSettingsStore(context);
        var settings = await store.LoadAsync();
        await store.SaveGlobalSettingsAsync(settings, new AiSettings { ImageModelId = "explicit-missing-id" });
        Assert.AreEqual("explicit-missing-id", settings.ImageModelId);
        Assert.AreEqual("explicit-missing-id", (await store.LoadAsync()).ImageModelId);
        settings.ImageModelId = null;
        await store.ReloadIntoAsync(settings);
        Assert.AreEqual("explicit-missing-id", settings.ImageModelId);
    }
}

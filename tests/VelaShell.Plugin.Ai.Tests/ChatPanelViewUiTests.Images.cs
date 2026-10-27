using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using AvaloniaEdit;
using Avalonia.VisualTree;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Chat;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.Plugin.Ai.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

public sealed partial class ChatPanelViewUiTests
{
    private const string GeneratedPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=";

    [TestMethod]
    [DataRow(ChatMode.Chat)]
    [DataRow(ChatMode.Agent)]
    public void ImageGeneration_DisplaysAndPersistsWithoutEmptyReplyRetry(ChatMode mode)
    {
        OnUi(async () =>
        {
            using var endpoint = new KeyAuthStub((_, _) => (200,
                "{\"created\":1,\"data\":[{\"b64_json\":\"" + GeneratedPng + "\"}]}"));
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider provider = StubProvider("images", endpoint.BaseUrl + "/v1", "gpt-image-2");
            await store.SaveAsync(new AiSettings
            {
                Providers = [provider], ActiveModelId = provider.Models[0].Id,
                Mode = mode, SuggestFollowUps = true
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("draw a cat");
                Assert.IsTrue(await WaitForAsync(() => endpoint.Requests.Count > 0
                    && !Find<Button>(panel, "StopButton").IsVisible));
                StackPanel messages = Find<StackPanel>(panel, "MessagesPanel");
                Assert.HasCount(1, messages.GetVisualDescendants().OfType<Image>().Where(i => i.Source is not null).ToList());
                Assert.HasCount(1, endpoint.Requests, "纯图片不应判空、生成摘要或后续提问");
                var history = new ChatHistoryStore(context);
                await history.InitAsync();
                ChatSessionSummary saved = (await history.ListSessionsAsync()).Single();
                ChatEntry answer = (await history.LoadAsync(saved.Id)).Single(entry => entry.Role == "assistant");
                Assert.HasCount(1, answer.Meta!.Images!);
                Assert.IsFalse(string.IsNullOrWhiteSpace(answer.Text));
                MethodInfo load = typeof(ChatPanelView).GetMethod("LoadConversationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                await (Task)load.Invoke(panel, [saved])!;
                Assert.HasCount(1, messages.GetVisualDescendants().OfType<Image>().Where(i => i.Source is not null).ToList(),
                    "历史重新加载后仍应显示图片");
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void ImageGeneration_ServerFailureDoesNotRetryOrFailOver()
    {
        OnUi(async () =>
        {
            using var endpoint = new KeyAuthStub((_, _) => (503, "{\"error\":{\"message\":\"unavailable\"}}"));
            using var backup = new KeyAuthStub((_, _) => (200, LifecycleAnswer));
            using var context = new TestPluginContext();
            var store = new AiSettingsStore(context);
            AiProvider image = StubProvider("images", endpoint.BaseUrl + "/v1", "gpt-image-2");
            AiProvider text = StubProvider("backup", backup.BaseUrl, "m");
            await store.SaveAsync(new AiSettings
            {
                Providers = [image, text], ActiveModelId = image.Models[0].Id,
                Mode = ChatMode.Agent, FailoverChain = [new FailoverEntry { ModelId = text.Models[0].Id }]
            });
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                panel.SendExternal("draw a cat");
                Assert.IsTrue(await WaitForAsync(() => endpoint.Requests.Count > 0
                    && !Find<Button>(panel, "StopButton").IsVisible));
                Assert.HasCount(1, endpoint.Requests);
                Assert.IsEmpty(backup.Requests);
            }
            finally { panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void ClipboardPaste_AddsImageAttachmentsAndStillPastesText()
    {
        OnUi(async () =>
        {
            using var context = new TestPluginContext();
            (Window window, ChatPanelView panel) = await ShowAsync(context);
            try
            {
                var clipboard = window.Clipboard!;
                var editor = Find<TextEditor>(panel, "InputBox");
                using var source = new MemoryStream(Convert.FromBase64String(GeneratedPng));
                using var bitmap = new Bitmap(source);
                await clipboard.SetBitmapAsync(bitmap);
                editor.Text = "prompt";
                ApplicationCommands.Paste.Execute(null, editor.TextArea);
                Assert.IsTrue(await WaitForAsync(() => Find<WrapPanel>(panel, "AttachmentBar").Children.Count == 1));
                Assert.AreEqual("prompt", editor.Text);
                var contents = (List<AIContent>)typeof(ChatPanelView).GetMethod("BuildUserContents",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, ["prompt"])!;
                Assert.HasCount(1, contents.OfType<DataContent>().ToList());
                for (int i = 0; i < 4; i++)
                {
                    ApplicationCommands.Paste.Execute(null, editor.TextArea);
                    int expected = Math.Min(i + 2, 4);
                    Assert.IsTrue(await WaitForAsync(() => Find<WrapPanel>(panel, "AttachmentBar").Children.Count == expected));
                }
                Assert.HasCount(4, Find<WrapPanel>(panel, "AttachmentBar").Children);
                await clipboard.SetTextAsync("pasted text");
                editor.Text = "";
                ApplicationCommands.Paste.Execute(null, editor.TextArea);
                Assert.IsTrue(await WaitForAsync(() => editor.Text == "pasted text"));
            }
            finally { await window.Clipboard!.ClearAsync(); panel.Detach(); window.Close(); }
        });
    }

    [TestMethod]
    public void NonEmptyImageCountsAsMeaningfulOutput()
    {
        Assert.IsTrue(ChatPanelView.HasMeaningfulOutput([
            new ChatResponseUpdate(ChatRole.Assistant, [new DataContent(Convert.FromBase64String(GeneratedPng), "image/png")])]));
        Assert.IsFalse(ChatPanelView.HasMeaningfulOutput([
            new ChatResponseUpdate(ChatRole.Assistant, [new DataContent(ReadOnlyMemory<byte>.Empty, "image/png")])]));
        Assert.IsFalse(ChatPanelView.HasMeaningfulOutput([
            new ChatResponseUpdate(ChatRole.Assistant, [new DataContent(new byte[] { 1 }, "application/octet-stream")])]));
    }
}

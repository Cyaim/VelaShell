using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

[TestClass]
public sealed class OpenAiImageTests
{
    private static ResolvedModel Model(string url, string name, ChatProtocol protocol)
    {
        var provider = new AiProvider { BaseUrl = url, DefaultProtocol = protocol,
            Models = [new AiModelConfig { Model = name }] };
        return new ResolvedModel(provider, provider.Models[0]);
    }

    [TestMethod]
    public void RoutingAndCatalogRecognizeOnlyKnownNamesOrExplicitProtocol()
    {
        Assert.IsTrue(AiSettingsStore.IsImageModel(Model("http://localhost/v1", "gpt-image-2", ChatProtocol.OpenAiResponses)));
        Assert.IsTrue(AiSettingsStore.IsImageModel(Model("http://localhost/v1", "dall-e-3", ChatProtocol.OpenAiChatCompletions)));
        Assert.IsTrue(AiSettingsStore.IsImageModel(Model("http://localhost/v1", "image-2", ChatProtocol.OpenAiImages)));
        Assert.IsFalse(AiSettingsStore.IsImageModel(Model("http://localhost/v1", "image-2", ChatProtocol.OpenAiResponses)));
        Assert.IsFalse(AiSettingsStore.IsImageModel(Model("http://localhost/v1", "gpt-image-2", ChatProtocol.AnthropicMessages)));
        var models = EndpointModelCatalog.Parse("{\"data\":[{\"id\":\"gpt-image-2\"},{\"id\":\"dall-e-3\"},{\"id\":\"text-embedding-3\"}]}");
        Assert.AreEqual(2, models.Count);
    }

    [TestMethod]
    public async Task ImageProbeDoesNotSendRequest()
    {
        using var context = new TestPluginContext();
        (Exception? error, _) = await HealthProbe.ProbeAsync(new AiSettingsStore(context),
            Model("http://127.0.0.1:1/v1", "image-2", ChatProtocol.OpenAiImages));
        Assert.IsInstanceOfType<NotSupportedException>(error);
    }

    [TestMethod]
    public async Task ImageRequestUsesEndpointAndReturnsDataContent()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        int port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var context = new TestPluginContext();
        using IChatClient client = new AiSettingsStore(context).CreateClient(
            Model($"http://127.0.0.1:{port}/v1", "image-2", ChatProtocol.OpenAiImages),
            new ProviderCredential("test-key", false, [new("x-test-header", "present")]));
        Task<ChatResponse> responseTask = client.GetResponseAsync([
            new ChatMessage(ChatRole.User, "old"), new ChatMessage(ChatRole.User, "draw a cat")]);
        HttpListenerContext request = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual("/v1/images/generations", request.Request.Url!.AbsolutePath);
        Assert.AreEqual("Bearer test-key", request.Request.Headers["Authorization"]);
        Assert.AreEqual("present", request.Request.Headers["x-test-header"]);
        using var reader = new StreamReader(request.Request.InputStream);
        using JsonDocument body = JsonDocument.Parse(await reader.ReadToEndAsync());
        Assert.AreEqual("draw a cat", body.RootElement.GetProperty("prompt").GetString());
        Assert.AreEqual("image-2", body.RootElement.GetProperty("model").GetString());
        byte[] reply = Encoding.UTF8.GetBytes("{\"created\":1,\"data\":[{\"b64_json\":\"AQID\"}]}");
        request.Response.ContentType = "application/json";
        request.Response.ContentLength64 = reply.Length;
        await request.Response.OutputStream.WriteAsync(reply);
        request.Response.Close();
        ChatResponse response = await responseTask;
        DataContent image = response.Messages.Single().Contents.OfType<DataContent>().Single();
        Assert.AreEqual("image/png", image.MediaType);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, image.Data.ToArray());
    }

    [TestMethod]
    public async Task ImageGenerationDoesNotRetryServerErrors()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        int port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var context = new TestPluginContext();
        using IChatClient client = new AiSettingsStore(context).CreateClient(
            Model($"http://127.0.0.1:{port}/v1", "gpt-image-2", ChatProtocol.OpenAiResponses), ProviderCredential.Key("key"));
        Task<ChatResponse> responseTask = client.GetResponseAsync([new ChatMessage(ChatRole.User, "draw")]);
        HttpListenerContext request = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
        request.Response.StatusCode = 429;
        request.Response.Close();
        await Assert.ThrowsAsync<Exception>(async () => await responseTask.WaitAsync(TimeSpan.FromSeconds(10)));
        Task<HttpListenerContext> second = listener.GetContextAsync();
        Assert.AreNotSame(second, await Task.WhenAny(second, Task.Delay(1200)), "SDK retried a billable image request");
        listener.Stop();
    }

    [TestMethod]
    public async Task ImageAttachmentIsRejectedBeforeRequest()
    {
        using var context = new TestPluginContext();
        using IChatClient client = new AiSettingsStore(context).CreateClient(
            Model("http://127.0.0.1:1/v1", "gpt-image-2", ChatProtocol.OpenAiResponses), ProviderCredential.Key("key"));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => client.GetResponseAsync([
            new ChatMessage(ChatRole.User, [new TextContent("edit"), new DataContent(new byte[] { 1 }, "image/png")])]));
    }
}

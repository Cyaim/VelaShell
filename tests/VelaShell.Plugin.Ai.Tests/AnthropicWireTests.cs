using System.Collections.Specialized;
using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// Anthropic 协议下,插件的聊天客户端到底发出了哪些头、什么样的请求体 ——
/// 走真 <see cref="AiSettingsStore.CreateClient" />、真 SDK、真适配器,打到本地假端点抓报文。
/// </summary>
/// <remarks>
/// 起因:Anthropic SDK 从 13.x 起 (a) 把 <c>anthropic-version</c> 从 <c>2023-06-01</c> 换成 <c>2026-10-21</c>;
/// (b) 把 <c>ApiKey</c> 也发成 <c>Authorization: Bearer</c>,不再发 <c>x-api-key</c>;
/// (c) <c>MessageCreateParams</c> 去掉了 temperature / top_p / top_k,<c>IChatClient</c> 适配器也不再映射它们。
/// 只认 <c>x-api-key</c> 的中转站因此一聊天就 401,而拉模型清单(那条请求是自己拼的)照常 ——
/// 只看其中一边查不出来,所以在线上报文这一层守住。
/// </remarks>
[TestClass]
[TestCategory("Plugins")]
public sealed class AnthropicWireTests
{
    private const string Reply = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_wire","type":"message","role":"assistant","model":"claude-wire","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":0}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"OK"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":1}}

        event: message_stop
        data: {"type":"message_stop"}


        """;

    private static ResolvedModel ModelFor(string baseUrl, ChatProtocol protocol = ChatProtocol.AnthropicMessages)
    {
        var provider = new AiProvider
        {
            Name = "wire",
            BaseUrl = baseUrl,
            DefaultProtocol = protocol,
            Models = [new AiModelConfig { Model = "claude-wire", MaxTokens = 64 }]
        };
        return new ResolvedModel(provider, provider.Models[0]);
    }

    /// <summary>用给定凭据发一轮流式对话,抓回这一次请求的头与体。</summary>
    /// <param name="credential">交给 <see cref="AiSettingsStore.CreateClient" /> 的凭据。</param>
    /// <param name="tune">发送前按正式请求的路子调一下选项(拿得到解好的模型)。</param>
    private static async Task<(NameValueCollection Headers, string Body)> SendAsync(ProviderCredential credential,
        Action<ChatOptions, ResolvedModel>? tune = null)
    {
        await using var stub = new SseStub(Reply);
        using var context = new TestPluginContext();
        ResolvedModel model = ModelFor(stub.BaseUrl);
        var options = new ChatOptions();
        tune?.Invoke(options, model);
        using IChatClient client = new AiSettingsStore(context).CreateClient(model, credential);
        string text = "";
        await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync("hi", options))
        {
            text += update.Text;
        }
        Assert.AreEqual("OK", text, "假端点回的是标准 Anthropic 流,整轮应当正常收尾");
        return (stub.RequestHeaders.Single(), stub.Requests.Single());
    }

    [TestMethod]
    public async Task ApiKey_GoesOutAsXApiKey_WithNoAuthorizationHeader()
    {
        (NameValueCollection headers, _) = await SendAsync(ProviderCredential.Key("sk-ant-key"));

        Assert.AreEqual("sk-ant-key", headers["x-api-key"], "手填的 Key 要按 13 之前的老规矩走 x-api-key");
        Assert.IsNull(headers["Authorization"], "API Key 不能再以 Bearer 出现:只读 x-api-key 的中转站会把它当成没带 Key");
        // 版本头不动,照 SDK 的发 —— 请求体就是按这个版本拼的。SDK 再换版本时这里会红,提醒重新核对兼容层
        Assert.AreEqual("2026-10-21", headers["anthropic-version"]);
    }

    [TestMethod]
    public async Task LoginToken_StaysBearer_AndKeepsItsBetaHeader()
    {
        var credential = new ProviderCredential("sk-ant-oat-1", true, [new("anthropic-beta", "oauth-2025-04-20")]);

        (NameValueCollection headers, _) = await SendAsync(credential);

        Assert.AreEqual("Bearer sk-ant-oat-1", headers["Authorization"], "登录换回来的短期令牌本来就该走 Bearer");
        Assert.IsNull(headers["x-api-key"], "令牌不能被改写成 x-api-key");
        Assert.AreEqual("oauth-2025-04-20", headers["anthropic-beta"]);
    }

    [TestMethod]
    [DataRow("Authorization", "Bearer relay-token", "sk-ant-key")]
    [DataRow("Authorization", "Bearer sk-ant-key", "sk-ant-key")]
    [DataRow("x-api-key", "relay-key", "relay-key")]
    public async Task UserConfiguredHeaders_AreNeverClobbered(string name, string value, string expectedApiKey)
    {
        // 第二行是关键:用户自己写的 Authorization 恰好就是 "Bearer 这把 Key" 时,
        // 改写必须发生在附加头之前,否则会把用户明确配置的头当成 SDK 的那份一并摘掉
        var credential = new ProviderCredential("sk-ant-key", false,
            [new(name, value), new("X-Relay-Route", "r1")]);

        (NameValueCollection headers, _) = await SendAsync(credential);

        Assert.AreEqual(expectedApiKey, headers["x-api-key"]);
        Assert.AreEqual(name == "Authorization" ? value : null, headers["Authorization"]);
        Assert.AreEqual("r1", headers["X-Relay-Route"]);
    }

    [TestMethod]
    public async Task NoCredential_LeavesTheSdkEnvironmentFallbackAlone()
    {
        // 没有凭据时由 SDK 自己读 ANTHROPIC_API_KEY / ANTHROPIC_AUTH_TOKEN;那条路不归插件改写 ——
        // 不论这台机器设没设这两个变量,都不该凭空冒出一个 x-api-key
        (NameValueCollection headers, _) = await SendAsync(ProviderCredential.Key(null));

        Assert.IsNull(headers["x-api-key"]);
    }

    [TestMethod]
    public async Task Sampling_IsNotSentOnTheAnthropicProtocol_ButStopSequencesAre()
    {
        (_, string body) = await SendAsync(ProviderCredential.Key("sk-ant-key"), (options, model) =>
        {
            // 照聊天面板的组装顺序:先填设置里的采样参数,再过一遍端点规则
            options.Temperature = 0.3f;
            options.TopP = 0.9f;
            options.TopK = 5;
            options.StopSequences = ["END_MARK"];
            AiSettingsStore.ApplyEndpointQuirks(options, model);
        });

        Assert.DoesNotContain("temperature", body, $"实际请求体:{body}");
        Assert.DoesNotContain("top_p", body, $"实际请求体:{body}");
        Assert.DoesNotContain("top_k", body, $"实际请求体:{body}");
        Assert.Contains("END_MARK", body, $"停止序列在 Anthropic 协议上照发。实际请求体:{body}");
    }

    [TestMethod]
    [DataRow(ChatProtocol.AnthropicMessages, true)]
    [DataRow(ChatProtocol.OpenAiChatCompletions, false)]
    [DataRow(ChatProtocol.OpenAiResponses, false)]
    public void EndpointQuirks_DropSamplingOnlyOnTheAnthropicProtocol(ChatProtocol protocol, bool dropped)
    {
        // 不靠 SDK 适配器"恰好不映射":2026-10-21 版的接口收到这三个字段一律 400,
        // 哪天适配器又映射回来,设置页上那句"不会发送"也得仍然成立
        var options = new ChatOptions { Temperature = 0.3f, TopP = 0.9f, TopK = 5 };

        AiSettingsStore.ApplyEndpointQuirks(options, ModelFor("http://127.0.0.1:9", protocol));

        Assert.AreEqual(dropped ? null : 0.3f, options.Temperature);
        Assert.AreEqual(dropped ? null : 0.9f, options.TopP);
        Assert.AreEqual(dropped ? null : 5, options.TopK);
    }
}

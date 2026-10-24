namespace VelaShell.Plugin.Ai.Auth;

/// <summary>
/// 把 Anthropic SDK 13.x 以 <c>Authorization: Bearer</c> 发出去的 API Key 改回 <c>x-api-key</c>。
/// </summary>
/// <remarks>
/// <para>
/// 起因:Anthropic SDK 从 13.0 起把 <c>ApiKey</c> 也当 Bearer 发(12.x 及以前发的是 <c>x-api-key</c>,
/// 13.2.1 的程序集里已经找不到这个字符串)。官方端点两种都认,可不少中转站与兼容端点
/// <b>只从 <c>x-api-key</c> 取 Key</b> —— 升到 13.x 之后拉模型清单照常(那条请求是自己拼的,
/// 见 <c>EndpointModelCatalog</c>),一聊天却是 401。这里把请求恢复成 13 之前的样子。
/// </para>
/// <para>
/// <b>只认我们自己的那把 Key</b>:<c>Authorization</c> 恰好是 <c>Bearer {这把 Key}</c> 才改写,
/// 别的值一律不碰。登录换来的短期令牌(<c>AuthToken</c>)本来就该走 Bearer,
/// 无凭据时 SDK 自己读环境变量的那条路也不归插件管 —— 这两种情况调用方都不挂这个处理器。
/// </para>
/// <para>
/// <b>顺序</b>:必须挂在 <see cref="ExtraHeadersHandler" /> <b>外层</b>(先执行)。附加头后施加,
/// 用户明确配置的 <c>Authorization</c> / <c>x-api-key</c> 才总是最后落定;反过来挂的话,
/// 用户恰好写了 <c>Authorization: Bearer 同一把 Key</c> 时会被当成 SDK 的那份一并摘掉。
/// </para>
/// <para>
/// <c>anthropic-version</c> 不动,照 SDK 的发(13.x 是 <c>2026-10-21</c>,此前是 <c>2023-06-01</c>):
/// 请求体是按那个版本拼的,两者要配套 —— 新版本下 temperature / top_p / top_k 一律被拒,
/// 插件在 Anthropic 协议上也就不再发它们(见 <c>AiSettingsStore.ApplyEndpointQuirks</c>)。
/// </para>
/// </remarks>
/// <param name="apiKey">这次要发的 API Key(非空)。</param>
internal sealed class AnthropicApiKeyHandler(string apiKey) : DelegatingHandler
{
    private const string ApiKeyHeader = "x-api-key";

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Headers.TryGetValues("Authorization", out IEnumerable<string>? values)
            && values.ToArray() is [string only]
            && string.Equals(only, "Bearer " + apiKey, StringComparison.Ordinal))
        {
            request.Headers.Remove("Authorization");
            // 先移除再加:SDK 的重试会拿同一个 request 再来一次,不清掉就叠成两份
            request.Headers.Remove(ApiKeyHeader);
            request.Headers.TryAddWithoutValidation(ApiKeyHeader, apiKey);
        }
        return base.SendAsync(request, cancellationToken);
    }
}

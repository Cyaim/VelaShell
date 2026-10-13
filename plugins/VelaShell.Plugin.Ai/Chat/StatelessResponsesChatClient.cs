using Microsoft.Extensions.AI;

namespace VelaShell.Plugin.Ai.Chat;

/// <summary>
/// 让 OpenAI Responses 那条路的函数调用循环<b>每一步都整段重发历史</b>,
/// 不靠 <c>previous_response_id</c> 让服务端接上一步。
/// </summary>
/// <remarks>
/// <para>
/// 起因:M.E.AI 的 Responses 适配器只要请求里没写 <c>store:false</c>,就把服务端回的 response id
/// 当成 <see cref="ChatResponseUpdate.ConversationId" /> 交上来;<c>FunctionInvokingChatClient</c>
/// 见到它就认定"会话存在服务端",工具结果那一步只发 <c>previous_response_id</c> + 工具输出。
/// 中转站背后往往是按号池分发的上游(实测 Routin 转的是 ChatGPT 的 Codex 后端),上一步既没存、
/// 也未必落在同一个账号上 —— 第二步必然 400(Routin 回 <c>previous_response_id is not available
/// for this user</c>,或者只剩一句 <c>Upstream rejected the request</c>)。
/// 表现就是 Agent 模式下模型一调工具,这一轮就挂。
/// </para>
/// <para>
/// 插件自己管历史(<see cref="ChatHistoryStore" /> + <c>ContextBuilder</c>),从来不用服务端存的会话。
/// 这里把 ConversationId 抹掉,循环就和其它协议一样自己攒历史、每步整段发 —— 多传几 KB 上下文,
/// 换来不依赖"上游替我们记着"。<c>store</c> 字段本身不动(那归 <c>EndpointQuirks.StoreResponses</c> 管)。
/// </para>
/// <para>
/// <b>实测记录(2026-10-13,Microsoft.Extensions.AI.OpenAI 10.10.1 / OpenAI 2.14.0,真端点 api.routin.ai)</b>:
/// 不抹 → 第二步只带 <c>previous_response_id</c> 与 <c>function_call_output</c>,400;
/// 抹掉 → 第二步带 system + user + <c>function_call</c> + <c>function_call_output</c>,200;
/// 开思考(effort=medium、summary=detailed)同样 200。
/// </para>
/// </remarks>
/// <param name="innerClient">Responses 适配器出来的那个客户端。</param>
internal sealed class StatelessResponsesChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ChatResponse response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        response.ConversationId = null;
        return response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(messages, options, cancellationToken)
            .ConfigureAwait(false))
        {
            update.ConversationId = null;
            yield return update;
        }
    }
}

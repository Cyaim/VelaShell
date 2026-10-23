using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 两击确认换源清 Key 的第一击:记着当时的供应商、地址与存储层给的确认凭据
/// (<see cref="AiSettingsStore.ApiKeyResetRequiredException.Confirmation" />)。设置页与目录页共用。
/// </summary>
/// <remarks>
/// 同一供应商、同一地址再点一次,才把确认凭据交回去;地址改了就重新提示。要清的 Key 在两击之间变了
/// (别处又存了一把,或同一个槽换了值)由存储层核对确认凭据拦下,不靠这里。
/// </remarks>
internal sealed record KeyResetPrompt(AiProvider Provider, string Url, IReadOnlyList<string> Confirmation)
{
    /// <summary>这一击要交回的确认凭据;换了供应商或地址就是 null(重新提示)。</summary>
    public IReadOnlyList<string>? For(AiProvider provider, string url)
        => ReferenceEquals(Provider, provider) && Url == url ? Confirmation : null;

    /// <summary>
    /// 换源时被清掉自带 Key 的模型(显示名),供保存后的提示逐个点名;名称与模型 id 都空着的,与设置页列表一样叫「未命名」。
    /// </summary>
    public static List<string> ClearedModels(AiProvider provider, IReadOnlyList<string> cleared, Loc loc)
        => [.. provider.Models.Where(model => cleared.Contains(model.Id))
            .Select(model => string.IsNullOrWhiteSpace(model.DisplayName) ? loc["Unnamed"] : model.DisplayName)];
}

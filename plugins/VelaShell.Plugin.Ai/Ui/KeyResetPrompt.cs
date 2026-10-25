using VelaShell.Plugin.Ai.Configuration;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 确认框里点了确认的那一次换源清 Key:当时的供应商、地址与存储层给的确认凭据
/// (<see cref="AiSettingsStore.ApiKeyResetRequiredException.Confirmation" />),交给紧接着的那次重新提交。设置页与目录页共用。
/// </summary>
/// <remarks>
/// 确认走独立的确认框(<see cref="ConfirmOverlay" />),不再靠同一个按钮点两下:连点两下就算确认了。
/// 确认的是框里列出的那几把;框开着的时候别处又存了一把、或同一个槽换了值,由存储层核对凭据拦下,
/// 再问一次(换一句话说「有了变动」)。这份记录只活在确认之后的那一次提交里,不留到下一次点「保存」。
/// </remarks>
internal sealed record KeyResetPrompt(AiProvider Provider, string Url, IReadOnlyList<string> Confirmation)
{
    /// <summary>这次提交要交回的确认凭据;换了供应商或地址就是 null(重新问)。</summary>
    public IReadOnlyList<string>? For(AiProvider provider, string url)
        => ReferenceEquals(Provider, provider) && Url == url ? Confirmation : null;

    /// <summary>弹确认框:换源要清掉这些已存的 Key(订阅登录的还要退出登录)。点确认返回 true。</summary>
    /// <param name="overlay">视图上的确认框。</param>
    /// <param name="provider">换源的供应商。</param>
    /// <param name="reset">存储层的拒绝(要清谁、要不要退出登录)。</param>
    /// <param name="changed">已经确认过一次、提交时又被拒(框开着期间 Key 变了)。</param>
    /// <param name="loc">文案。</param>
    public static Task<bool> AskAsync(ConfirmOverlay overlay, AiProvider provider,
        AiSettingsStore.ApiKeyResetRequiredException reset, bool changed, Loc loc)
    {
        // 退出登录那一种:旧地址签出的令牌不发往新地址,之后要在新地址重新登录;正在用这家的对话会中断,一并说清
        string message = reset.SignsOut
            ? loc["SetupBaseUrlSignOutConfirm"]
                + (reset.KeyCount > 0 ? "\n" + loc.F("SetupBaseUrlKeyResetConfirmAlso", reset.KeyCount) : "")
            : loc.F(changed ? "SetupBaseUrlKeyResetChanged" : "SetupBaseUrlKeyResetConfirm", reset.KeyCount);
        List<string> models = ClearedModels(provider, reset.KeyOwners, loc);
        if (models.Count > 0) message += "\n" + loc.F("SetupBaseUrlKeyResetModels", string.Join(", ", models));
        return overlay.AskAsync(
            loc[reset.SignsOut ? "SetupSignOutResetTitle" : "SetupKeyResetTitle"],
            message,
            loc[reset.SignsOut ? "SetupSignOutAccept" : "SetupKeyResetAccept"],
            loc["Cancel"]);
    }

    /// <summary>
    /// 换源时被清掉自带 Key 的模型(显示名),供确认框与保存后的提示逐个点名;名称与模型 id 都空着的,与设置页列表一样叫「未命名」。
    /// </summary>
    public static List<string> ClearedModels(AiProvider provider, IReadOnlyList<string> cleared, Loc loc)
        => [.. provider.Models.Where(model => cleared.Contains(model.Id))
            .Select(model => string.IsNullOrWhiteSpace(model.DisplayName) ? loc["Unnamed"] : model.DisplayName)];
}

using ReactiveUI;
using VelaShell.PluginSdk;

namespace VelaShell.ViewModels;

/// <summary>
/// 「选类型」那一屏上的一张卡片,对应变体字段的一个候选项。
/// </summary>
/// <param name="value">落进变体字段的值(<c>ProtocolSettingChoice.Value</c>)。</param>
/// <param name="label">卡片上的名字(<c>ProtocolSettingChoice.Label</c>)。</param>
/// <param name="icon">
/// 卡片图标;null 时界面画协议栏同款的通用插件图标。
/// 第一期取工作台描述符的图标(几张卡片同一个图标),变体自带图标要等 SDK。
/// </param>
public sealed class PluginTypeCardViewModel(string value, string label, PluginIcon? icon) : ReactiveObject
{
    /// <summary>落进变体字段的值。</summary>
    public string Value { get; } = value;

    /// <summary>卡片上的名字。</summary>
    public string Label { get; } = label;

    /// <summary>卡片图标;null 表示用通用插件图标。</summary>
    public PluginIcon? Icon { get; } = icon;

    /// <summary>
    /// 是否为当前选中的那张。做成卡片自己的状态,理由同
    /// <see cref="PluginProtocolTabViewModel.IsSelected" />:模板里比对父视图模型的属性没有现成的路子。
    /// </summary>
    public bool IsSelected
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }
}

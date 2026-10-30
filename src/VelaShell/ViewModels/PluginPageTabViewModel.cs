using ReactiveUI;

namespace VelaShell.ViewModels;

/// <summary>
/// 分页页签条上插件声明的一个自定义页签(「数据库」「SSL」……),介于「常规」与「高级」之间。
/// <para>
/// 只是页签:页里画哪几行由 <see cref="PluginProtocolFieldViewModel.PageKey" /> 决定,
/// 与「常规」「高级」两页绑的是同一批行。不做「高级」那几样宿主专属行为(数量徽标、改过值的圆点)。
/// </para>
/// </summary>
/// <param name="key">页键(序数比较)。</param>
/// <param name="label">页签文案(插件自行本地化)。</param>
public sealed class PluginPageTabViewModel(string key, string label) : ReactiveObject
{
    /// <summary>页键。</summary>
    public string Key { get; } = key;

    /// <summary>页签文案。</summary>
    public string Label { get; } = label;

    /// <summary>
    /// 是否为当前选中的页签。做成页签自己的状态,理由同
    /// <see cref="PluginProtocolTabViewModel.IsSelected" />:模板里比对父视图模型的属性没有现成的路子。
    /// </summary>
    public bool IsSelected
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 这一页此刻有没有要画的行。没有就整个页签收起(所有字段都被显示条件藏掉时,点进去是一片空白)。
    /// </summary>
    public bool HasVisibleFields
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }
}

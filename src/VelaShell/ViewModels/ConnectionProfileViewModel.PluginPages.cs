using System.Collections.ObjectModel;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace VelaShell.ViewModels;

/// <summary>
/// 插件字段按**页键**分页:「常规」、插件声明的自定义页(「数据库」「SSL」……)、「高级」。
/// <para>
/// 原先是 IsAdvanced 二分(常规 / 高级)。现在每一行带一个页键
/// (<see cref="PluginProtocolFieldViewModel.PageKey" />,由 <see cref="PluginConnectionForm.PageKeyFor" /> 算出:
/// 页键优先于 IsAdvanced,不认识的键按没填),行可见性、各自定义页签在不在、「常规」页插件那一节在不在、
/// 「高级」页签上的字段数,全走同一条判定 <see cref="IsFormRowOn" />。
/// </para>
/// <para>
/// 自定义页只是页签加一份行:不做「高级」那几样宿主专属行为(数量徽标、改过值的圆点)。
/// 分页来自 <c>WorkspaceDescriptor.Pages</c> 与字段的 <c>Page</c>(经 <c>PluginConnectionForm.From()</c>);
/// 没声明分页的连接类型 <see cref="PluginPages" /> 为空,对话框与没有这套机制时一样。
/// </para>
/// </summary>
public partial class ConnectionProfileViewModel
{
    /// <summary>页签条上插件声明的自定义页签(声明顺序);当前表单没有自定义页时为空。</summary>
    public ObservableCollection<PluginPageTabViewModel> PluginPages { get; } = [];

    /// <summary>
    /// 自定义页里选的是哪一页(页键)。只在 <see cref="SelectedSection" /> 为
    /// <see cref="ConnectionProfileSection.PluginPage" /> 时决定显示什么。
    /// </summary>
    public string? SelectedPluginPageKey
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>当前是否在某个插件自定义页上。</summary>
    public bool IsPluginPageSection => SelectedSection == ConnectionProfileSection.PluginPage;

    /// <summary>
    /// 自定义页签那一组出不出现。一个看得见的都没有时整组收起 ——
    /// 留着一个零宽的空组,页签条的间距会多出一格,「高级」页签就挪了位置。
    /// </summary>
    public bool ShowPluginPageTabs => PluginPages.Any(tab => tab.HasVisibleFields);

    /// <summary>切到某个自定义页(参数是页键)。</summary>
    public ReactiveCommand<string, RxVoid> SelectPluginPageCommand { get; }

    /// <summary>
    /// 测试接缝:插件表单解析出来之后、渲染之前换一份。
    /// <para>
    /// 分页机制的边界情况(未知页键、宿主键、重复键)用合成表单测更直接;生产代码不设它。
    /// 只作用于构造之后的装载 —— 编辑态在构造函数里就装载了,
    /// 对象初始化器赶不上那一次。
    /// </para>
    /// </summary>
    internal Func<PluginConnectionForm, PluginConnectionForm>? PluginFormOverride { get; init; }

    /// <summary>当前分页对应的页键:「高级」、某个自定义页,其余一律「常规」。</summary>
    private string CurrentPluginPageKey => SelectedSection switch
    {
        ConnectionProfileSection.Advanced => PluginFormPage.AdvancedKey,
        ConnectionProfileSection.PluginPage => SelectedPluginPageKey ?? PluginFormPage.GeneralKey,
        _ => PluginFormPage.GeneralKey
    };

    /// <summary>当前选中的自定义页还在不在(页签存在且有要画的行)。</summary>
    private bool IsPluginPageAvailable =>
        SelectedPluginPageKey is { } key
        && PluginPages.Any(tab => tab.HasVisibleFields && string.Equals(tab.Key, key, StringComparison.Ordinal));

    /// <summary>
    /// 这一行会不会画在 <paramref name="pageKey" /> 那一页上:是表单行(<see cref="IsFormRow" />:
    /// 显示条件成立、不是被卡片替代的变体字段),且属于那一页。
    /// <para>
    /// 行可见性、各自定义页签在不在、「常规」页插件那一节在不在、「高级」页签上的字段数用的是这同一条判定 ——
    /// 分开写,就会出现"页签上报 2 项、点进去只有 1 项"或"只剩标题的空节"。
    /// </para>
    /// </summary>
    private bool IsFormRowOn(PluginProtocolFieldViewModel field, string pageKey) =>
        IsFormRow(field) && string.Equals(field.PageKey, pageKey, StringComparison.Ordinal);

    /// <summary>
    /// 切到某个自定义页。页签不存在或此刻没有要画的行时什么也不做(页签本身也是收起的)。
    /// </summary>
    private void SelectPluginPage(string key)
    {
        if (PluginPages.FirstOrDefault(tab => string.Equals(tab.Key, key, StringComparison.Ordinal))
            is not { HasVisibleFields: true })
        {
            return;
        }
        SelectedPluginPageKey = key;
        if (SelectedSection == ConnectionProfileSection.PluginPage)
        {
            // 自定义页之间切换:分页没变,SelectedSection 的 setter 会短路,这里补一次下发。
            SyncPluginPageSelection();
            ApplyPluginFieldVisibility();
        }
        else
        {
            SelectedSection = ConnectionProfileSection.PluginPage;
        }
    }

    /// <summary>
    /// 按表单的分页声明重建自定义页签。<paramref name="form" /> 为 null(切回内建协议、换了协议还没装载完)时清空。
    /// <para>
    /// 「常规」「高级」两个键由宿主自己画,同一个键声明了两次只取第一个 —— 两个同键的页签,选中一个会亮两个。
    /// 页签在不在(<see cref="PluginPageTabViewModel.HasVisibleFields" />)由随后那次
    /// <see cref="ApplyPluginFieldVisibility" /> 定。
    /// </para>
    /// </summary>
    private void RebuildPluginPages(PluginConnectionForm? form)
    {
        PluginPages.Clear();
        HashSet<string> taken = new(StringComparer.Ordinal) { PluginFormPage.GeneralKey, PluginFormPage.AdvancedKey };
        foreach (PluginFormPage page in form?.Pages ?? [])
        {
            if (taken.Add(page.Key))
            {
                PluginPages.Add(new(page.Key, page.Label));
            }
        }
        if (SelectedPluginPageKey is { } key
            && !PluginPages.Any(tab => string.Equals(tab.Key, key, StringComparison.Ordinal)))
        {
            SelectedPluginPageKey = null;
        }
        SyncPluginPageSelection();
        this.RaisePropertyChanged(nameof(ShowPluginPageTabs));
    }

    /// <summary>页签高亮跟着分页走:只有在自定义页上,且是选中的那一页。</summary>
    private void SyncPluginPageSelection()
    {
        bool onPluginPage = IsPluginPageSection;
        foreach (PluginPageTabViewModel tab in PluginPages)
        {
            tab.IsSelected = onPluginPage && string.Equals(tab.Key, SelectedPluginPageKey, StringComparison.Ordinal);
        }
    }

    /// <summary>重算每个自定义页签有没有要画的行(与行可见性同一条判定)。</summary>
    private void RefreshPluginPageTabs()
    {
        foreach (PluginPageTabViewModel tab in PluginPages)
        {
            tab.HasVisibleFields = PluginFields.Any(field => IsFormRowOn(field, tab.Key));
        }
        this.RaisePropertyChanged(nameof(ShowPluginPageTabs));
    }
}

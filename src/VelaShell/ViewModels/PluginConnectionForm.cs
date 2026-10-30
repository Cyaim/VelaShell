using VelaShell.Infrastructure.Plugins.Protocols;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.ViewModels;

/// <summary>
/// 连接配置页需要从插件描述里知道的**全部**东西,与它是文件协议还是工作台无关。
/// <para>
/// 两种连接类型(<see cref="ProtocolDescriptor" /> / <see cref="WorkspaceDescriptor" />)在
/// 连接对话框上的表现完全一致:同一排页签、同一套声明式字段、同样的三格标签改写、
/// 同样的匿名判定。把差异收在这个转换器里,视图模型就只有一条路径 ——
/// 否则每个用到描述符的地方都要写一次"要么问这个、要么问那个"。
/// </para>
/// </summary>
/// <param name="Kind">形态(宿主打开会话时据此决定画文件浏览器还是工作台文档)。</param>
/// <param name="HostLabel">"主机"输入框的标签改写;null 用宿主默认。</param>
/// <param name="HostPlaceholder">"主机"输入框的占位提示改写。</param>
/// <param name="UsernameLabel">"用户名"输入框的标签改写。</param>
/// <param name="PasswordLabel">"密码"输入框的标签改写。</param>
/// <param name="Fields">声明式设置字段。</param>
/// <param name="AllowsAnonymous">是否允许不填凭据直接连(决定要不要弹登录框、要不要灰掉按钮)。</param>
/// <param name="SupportsSshTunnel">是否支持经 SSH 隧道抵达(决定是否渲染"经 SSH 隧道"一节)。</param>
/// <param name="ShowsCredentials">
/// 是否显示用户名/口令两栏。声明了 <see cref="ProtocolFeatures.NoCredentials" /> 的协议
/// (Telnet 这种登录发生在带内的)一律收起 —— 摆着两个填了也发不出去的框,
/// 只会让用户以为填上就能自动登录。工作台侧的同名能力位
/// (<see cref="WorkspaceFeatures.NoCredentials" />)一并生效。
/// </param>
/// <param name="ShowsPort">
/// 是否显示"端口"那一栏。声明了 <c>NoEndpoint</c> 的连接类型收起它 ——
/// 目标不是一个 <c>host:port</c> 时(SQLite 是磁盘上的一个文件、串口是一根线),
/// 那一栏填什么都不会被用上,而且还留着上一个协议的残值。
/// <para>
/// <b>只收端口,不收主机</b>:主机那一栏恰恰要留着装目标(文件路径 / 设备名)。
/// 端口的**取值**也照旧参与按钮可用性判定 —— 收起一栏不该顺手把按钮堵死。
/// </para>
/// </param>
/// <param name="HostKind">
/// "主机"那一栏的输入形态。默认 <see cref="ProtocolSettingKind.Text" />;
/// 取 <see cref="ProtocolSettingKind.DynamicChoice" /> 时渲染成可刷新的下拉
/// (串口:本机端口是可枚举、且会热插拔的)。
/// </param>
/// <param name="HostChoices">主机栏做成下拉时的候选项(动态形态下是兜底列表)。</param>
/// <param name="HostAllowsCustomValue">主机栏的下拉是否可手输。</param>
/// <param name="ChoiceSource">
/// 动态候选项的来源:协议实现自己(它同时实现了 <see cref="IProtocolChoiceSource" />)。
/// 没实现就是 null —— 此时动态下拉退化成只有兜底列表的普通下拉,不报错。
/// </param>
/// <param name="VariantKey">
/// 由哪个字段选择变体;null 表示这个连接类型没有变体。见 <see cref="WorkspaceDescriptor.VariantKey" />。
/// </param>
/// <param name="Variants">变体表(仅工作台形态有)。</param>
/// <param name="Icon">
/// 连接类型自报的图标(仅工作台形态有,取 <see cref="WorkspaceDescriptor.Icon" />);
/// 「选类型」卡片上变体没给 <see cref="WorkspaceVariant.Icon" /> 时退回它,再为 null 时界面画通用插件图标。
/// </param>
/// <param name="Pages">
/// 介于「常规」与「高级」之间的自定义分页(「数据库」「SSL」……),按声明顺序成页签;null / 空表示没有。
/// 工作台形态取 <see cref="WorkspaceDescriptor.Pages" />;文件协议没有分页列表,恒为 null。
/// </param>
/// <param name="FieldPages">
/// 字段键 → 所属分页的键(取各字段的 <see cref="ProtocolSettingField.Page" />);见 <see cref="PageKeyFor" />。
/// </param>
internal sealed record PluginConnectionForm(
    PluginConnectionKind Kind,
    string? HostLabel,
    string? HostPlaceholder,
    string? UsernameLabel,
    string? PasswordLabel,
    IReadOnlyList<ProtocolSettingField> Fields,
    bool AllowsAnonymous,
    bool SupportsSshTunnel,
    bool ShowsCredentials,
    bool ShowsPort = true,
    ProtocolSettingKind HostKind = ProtocolSettingKind.Text,
    IReadOnlyList<ProtocolSettingChoice>? HostChoices = null,
    bool HostAllowsCustomValue = false,
    IProtocolChoiceSource? ChoiceSource = null,
    string? VariantKey = null,
    IReadOnlyList<WorkspaceVariant>? Variants = null,
    PluginSdk.PluginIcon? Icon = null,
    IReadOnlyList<PluginFormPage>? Pages = null,
    IReadOnlyDictionary<string, string>? FieldPages = null)
{
    /// <summary>主机栏是否渲染成下拉(静态或动态)。</summary>
    public bool HostIsChoice => HostKind is ProtocolSettingKind.Choice or ProtocolSettingKind.DynamicChoice;

    /// <summary>
    /// 字段画在哪一页:<see cref="FieldPages" /> 给了键、且这个键在 <see cref="Pages" /> 里,就是那一页;
    /// 否则按 <see cref="ProtocolSettingField.IsAdvanced" /> 去「高级」或「常规」。
    /// <para>
    /// <b>页键优先于 IsAdvanced</b>:填了页键的字段进那一页,IsAdvanced 不再看 ——
    /// 「高级」带着宿主专属的行为(数量徽标、改过值的圆点),自定义页不做这几样。
    /// </para>
    /// <para>
    /// <b>不认识的键按没填处理</b>(不报错):与 <c>VisibleWhen</c> 引用不存在的键同一态度,
    /// 字段落回默认位置,用户总还找得到它。
    /// </para>
    /// </summary>
    /// <param name="field">字段声明。</param>
    /// <returns>页键:<see cref="PluginFormPage.GeneralKey" />、<see cref="PluginFormPage.AdvancedKey" /> 或某个自定义页的键。</returns>
    public string PageKeyFor(ProtocolSettingField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (FieldPages is not null
            && FieldPages.TryGetValue(field.Key, out string? key)
            && Pages is { Count: > 0 } pages)
        {
            for (int i = 0; i < pages.Count; i++)
            {
                if (string.Equals(pages[i].Key, key, StringComparison.Ordinal))
                {
                    return key;
                }
            }
        }
        return field.IsAdvanced ? PluginFormPage.AdvancedKey : PluginFormPage.GeneralKey;
    }

    /// <summary>
    /// 新建连接时要不要先出一屏「选类型」的卡片(见 <see cref="TypePickerField" />)。
    /// </summary>
    public bool HasTypePicker => TypePickerField is not null;

    /// <summary>
    /// 选变体的那个字段 —— 前提是它能画成卡片;不能时为 null,表单与没有卡片时一样。
    /// <para>
    /// 能画成卡片 = 封闭的静态下拉、至少两个候选、用户看得见:
    /// <see cref="ProtocolSettingKind.Choice" />、非 <see cref="ProtocolSettingField.IsHidden" />、
    /// 非 <see cref="ProtocolSettingField.AllowsCustomValue" />、<c>Choices.Count &gt;= 2</c>。
    /// 动态下拉(候选是现取的)、可手输的(值不在表里正是它的意义)、只有一个候选的(没得选),
    /// 做成卡片都说不通。这是宿主的规则,不是 SDK 开关。
    /// </para>
    /// <para>
    /// <see cref="Variants" /> 可以为空:变体字段只用来驱动 <c>VisibleWhen</c> 时卡片照出。
    /// </para>
    /// </summary>
    public ProtocolSettingField? TypePickerField
    {
        get
        {
            if (VariantKey is not { Length: > 0 } key)
            {
                return null;
            }
            for (int i = 0; i < Fields.Count; i++)
            {
                ProtocolSettingField candidate = Fields[i];
                if (string.Equals(candidate.Key, key, StringComparison.Ordinal))
                {
                    return candidate is { Kind: ProtocolSettingKind.Choice, IsHidden: false, AllowsCustomValue: false }
                           && candidate.Choices.Count >= 2
                        ? candidate
                        : null;
                }
            }
            return null;
        }
    }

    /// <summary>主机栏的候选项是否要向插件现取(并给出刷新按钮)。</summary>
    public bool HostIsDynamic => HostKind == ProtocolSettingKind.DynamicChoice;

    /// <summary>
    /// 按当前表单取值套用变体,得到**这一刻**该显示的表单形态。
    /// <para>
    /// 返回的仍是一个 <see cref="PluginConnectionForm" />,于是视图模型里所有读它的地方
    /// (三格标签、匿名判定、要不要显示凭据与端口)一行都不用改 —— 变体在这里就被抹平了。
    /// </para>
    /// <para>
    /// <b>字段表不随变体变</b>:字段的显隐由 <see cref="ProtocolSettingField.VisibleWhen" /> 管,
    /// 两者用的是同一个键。在这里再删一遍字段会把已存的值一起弄丢(见 VisibleWhen 的存取语义)。
    /// </para>
    /// </summary>
    /// <param name="lookup">按键取当前值。</param>
    /// <returns>套用变体之后的表单;没有变体或一个都对不上时返回自身。</returns>
    public PluginConnectionForm ForVariant(Func<string, string?> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        if (VariantKey is not { Length: > 0 } key || Variants is not { Count: > 0 } variants)
        {
            return this;
        }
        string current = lookup(key) ?? string.Empty;
        WorkspaceVariant? variant = null;
        for (int i = 0; i < variants.Count; i++)
        {
            if (string.Equals(variants[i].Value, current, StringComparison.Ordinal))
            {
                variant = variants[i];
                break;
            }
        }
        if (variant is null)
        {
            return this;
        }
        WorkspaceFeatures? features = variant.Features;
        return this with
        {
            HostLabel = variant.HostLabel ?? HostLabel,
            HostPlaceholder = variant.HostPlaceholder ?? HostPlaceholder,
            UsernameLabel = variant.UsernameLabel ?? UsernameLabel,
            PasswordLabel = variant.PasswordLabel ?? PasswordLabel,
            // 变体的能力位是**整体替换**,不是按位合并(见 WorkspaceVariant.Features):
            // 四格全部按变体重算,"这一档没有端点"与"切回去又有了"由同一次覆盖负责,不会粘住。
            AllowsAnonymous = features is { } f ? WorkspaceVariants.AllowsAnonymous(f) : AllowsAnonymous,
            SupportsSshTunnel = features?.HasFlag(WorkspaceFeatures.SshTunnel) ?? SupportsSshTunnel,
            ShowsCredentials = features is { } g ? !g.HasFlag(WorkspaceFeatures.NoCredentials) : ShowsCredentials,
            ShowsPort = features is { } h ? !h.HasFlag(WorkspaceFeatures.NoEndpoint) : ShowsPort
        };
    }

    /// <summary>
    /// 各变体声明的默认端口。
    /// <para>
    /// 宿主用它判断"用户有没有手填过端口" —— 少算变体那几个,
    /// 换方言时端口就不会跟随(5432 不在任何一张页签默认端口表里,会被当成用户手填的)。
    /// </para>
    /// </summary>
    /// <returns>变体默认端口(没声明端口的变体不出现)。</returns>
    public IEnumerable<int> VariantPorts()
    {
        if (Variants is not { Count: > 0 } variants)
        {
            yield break;
        }
        for (int i = 0; i < variants.Count; i++)
        {
            if (variants[i].DefaultPort is { } port)
            {
                yield return port;
            }
        }
    }

    /// <summary>
    /// 从协议描述转换。
    /// <para>
    /// 不填 <see cref="Pages" /> / <see cref="FieldPages" />:<see cref="ProtocolDescriptor" /> 没有分页列表,
    /// 文件协议的字段上 <see cref="ProtocolSettingField.Page" /> 被忽略(照 SDK 契约),字段照旧按 IsAdvanced 落位。
    /// </para>
    /// </summary>
    /// <param name="descriptor">协议描述。</param>
    /// <param name="handler">
    /// 该协议的实现(文件系统或终端)。只用来问一件事:它认不认
    /// <see cref="IProtocolChoiceSource" /> —— 动态下拉的候选项由它现给。
    /// </param>
    /// <returns>统一表单模型。</returns>
    public static PluginConnectionForm From(ProtocolDescriptor descriptor, object? handler = null) =>
        new(PluginConnectionKind.FileSystem,
            descriptor.HostLabel,
            descriptor.HostPlaceholder,
            descriptor.UsernameLabel,
            descriptor.PasswordLabel,
            descriptor.Fields,
            // NoCredentials 蕴含匿名:没有凭据这回事(Telnet),就不能拿"用户名没填"
            // 把连接按钮堵死。
            descriptor.Features.HasFlag(ProtocolFeatures.AnonymousAccess)
            || descriptor.Features.HasFlag(ProtocolFeatures.NoCredentials),
            // 文件协议侧暂不支持声明式隧道:它的会话由宿主的文件服务持有,
            // 隧道租约要跟着那条会话走,与工作台文档的生命周期不是一回事。
            SupportsSshTunnel: false,
            ShowsCredentials: !descriptor.Features.HasFlag(ProtocolFeatures.NoCredentials),
            ShowsPort: !descriptor.Features.HasFlag(ProtocolFeatures.NoEndpoint),
            HostKind: descriptor.HostKind,
            HostChoices: descriptor.HostChoices,
            HostAllowsCustomValue: descriptor.HostAllowsCustomValue,
            ChoiceSource: handler as IProtocolChoiceSource);

    /// <summary>从工作台描述转换。</summary>
    /// <param name="descriptor">连接类型描述。</param>
    /// <returns>统一表单模型。</returns>
    public static PluginConnectionForm From(WorkspaceDescriptor descriptor) =>
        new(PluginConnectionKind.Workspace,
            descriptor.HostLabel,
            descriptor.HostPlaceholder,
            descriptor.UsernameLabel,
            descriptor.PasswordLabel,
            descriptor.Fields,
            // NoCredentials 蕴含匿名:没有凭据这回事(SQLite 就是个文件),
            // 就不能拿"用户名没填"把连接按钮堵死。与打开会话前的判定共用一条。
            WorkspaceVariants.AllowsAnonymous(descriptor.Features),
            descriptor.Features.HasFlag(WorkspaceFeatures.SshTunnel),
            ShowsCredentials: !descriptor.Features.HasFlag(WorkspaceFeatures.NoCredentials),
            // SQLite / DuckDB 这类"就是磁盘上一个文件"的方言:端口那一栏收起,
            // 主机那一栏改标成「数据库文件」装路径。
            ShowsPort: !descriptor.Features.HasFlag(WorkspaceFeatures.NoEndpoint),
            VariantKey: descriptor.VariantKey,
            Variants: descriptor.Variants,
            Icon: descriptor.Icon,
            Pages: PagesOf(descriptor.Pages),
            FieldPages: FieldPagesOf(descriptor.Fields));

    /// <summary>
    /// SDK 的分页声明 → 内部模型,顺序不变。没声明(空列表)记为 null,与「没有自定义页」同一个值。
    /// <para>
    /// 宿主自己的键(general / advanced)与重复键不在这里筛:建页签时统一跳过,口径只留一处。
    /// </para>
    /// </summary>
    /// <param name="pages">描述符的 <see cref="WorkspaceDescriptor.Pages" />。</param>
    /// <returns>内部分页表;没有时为 null。</returns>
    private static PluginFormPage[]? PagesOf(IReadOnlyList<ProtocolSettingPage>? pages)
    {
        if (pages is not { Count: > 0 })
        {
            return null;
        }
        var result = new PluginFormPage[pages.Count];
        for (int i = 0; i < pages.Count; i++)
        {
            result[i] = new PluginFormPage(pages[i].Key, pages[i].Label);
        }
        return result;
    }

    /// <summary>
    /// 填了 <see cref="ProtocolSettingField.Page" /> 的字段 → 字段键到页键的字典(序数比较);一个都没填时为 null。
    /// <para>
    /// 页键在不在分页表里不在这里判:<see cref="PageKeyFor" /> 把不认识的键按没填处理(SDK 契约:不报错)。
    /// 字段键重复时只认第一次,不因插件声明有误抛异常把整个表单弄没。
    /// </para>
    /// </summary>
    /// <param name="fields">描述符的字段表。</param>
    /// <returns>字段键 → 页键;没有字段声明分页时为 null。</returns>
    private static Dictionary<string, string>? FieldPagesOf(IReadOnlyList<ProtocolSettingField> fields)
    {
        Dictionary<string, string>? result = null;
        for (int i = 0; i < fields.Count; i++)
        {
            if (fields[i] is { Page: { } page } field)
            {
                result ??= [with(StringComparer.Ordinal)];
                result.TryAdd(field.Key, page);
            }
        }
        return result;
    }
}

/// <summary>
/// 连接对话框里插件声明的一个自定义分页(「数据库」「SSL」……),介于「常规」与「高级」之间。
/// <para>
/// 用键而不是枚举:这些是领域页,宿主没法为任意插件预备一套封闭的文案;显隐与测试都按键比较,
/// 文案换语言不影响。「常规」「高级」由宿主出文案,不在插件的分页表里 —— 两个常量只是它们在页键上的名字。
/// </para>
/// </summary>
/// <param name="Key">页键:稳定标识,序数比较。</param>
/// <param name="Label">页签文案(插件自行本地化)。</param>
internal sealed record PluginFormPage(string Key, string Label)
{
    /// <summary>「常规」页的页键:没有页键、也没标 IsAdvanced 的字段在这里。</summary>
    public const string GeneralKey = "general";

    /// <summary>「高级」页的页键:没有页键、标了 IsAdvanced 的字段在这里。</summary>
    public const string AdvancedKey = "advanced";
}

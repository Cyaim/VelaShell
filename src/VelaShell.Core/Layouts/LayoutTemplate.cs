using System.Text.Json;
using System.Text.Json.Serialization;

namespace VelaShell.Core.Layouts;

/// <summary>
/// 一个命名布局模板:「打开这一组机器,并按这个分屏排好」。
/// </summary>
/// <remarks>
/// <para>
/// 存的是<b>布局的形状与每个标签指向哪条连接</b>,不是会话本身 —— 文档即活动会话,会话没法序列化,
/// 打开模板时按会话配置 Id 重新连一遍。正因为如此,同一套结构既能做用户起名的模板,也能做
/// 「退出时的布局」(启动时恢复,见 <c>GeneralOptions.RestoreLayoutOnStartup</c>)。
/// </para>
/// <para>
/// 字段一律是字符串或可空数值而不是枚举与 <see cref="double.NaN" />:文档库的 JSON 约定不带枚举转换器
/// (枚举会落成数字,以后插值就错位),而 NaN 根本写不进 JSON。新版本加了新的标签类型或标签条位置,
/// 旧版本读到时按「不认识」跳过,不会把整份模板读坏。
/// </para>
/// </remarks>
public sealed class LayoutTemplate
{
    /// <summary>当前写出的结构版本。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>模板名的最大长度。</summary>
    public const int MaxNameLength = 100;

    /// <summary>写出时的结构版本;读到更高的版本照常尽力还原,不认识的部分跳过。</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>模板的稳定标识(也是文档库里的主键)。</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>用户起的名字。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>创建时间(UTC)。</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>最近一次改动(改名、覆盖)的时间(UTC)。</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>布局树的根;为 null 表示空模板(不会被保存出来,读到时按空处理)。</summary>
    public LayoutNode? Root { get; set; }

    /// <summary>
    /// 更新的版本写进来、本版本不认识的字段:原样留着,落盘、同步、导出时照写回去。
    /// 不留的话,旧版本拉到新版本推上来的模板、再推一次就把新字段抹掉了(快捷命令的 <c>HiddenBuiltInIds</c> 吃过这个亏)。
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// 布局树的一个节点:<see cref="Orientation" /> 有值是分栏(看 <see cref="Children" />),
/// 否则是标签组(看 <see cref="Documents" />)。
/// </summary>
public sealed class LayoutNode
{
    /// <summary>分栏方向:<see cref="LayoutOrientation.Horizontal" />(左右)或 <see cref="LayoutOrientation.Vertical" />(上下);标签组为 null。</summary>
    public string? Orientation { get; set; }

    /// <summary>在父分栏里的比例权重;null = 与兄弟均分。</summary>
    public double? Proportion { get; set; }

    /// <summary>分栏的子节点,按排列顺序。</summary>
    public List<LayoutNode> Children { get; set; } = [];

    /// <summary>标签组里的标签,顺序即标签条上的顺序(固定的排在最前)。</summary>
    public List<LayoutDocument> Documents { get; set; } = [];

    /// <summary>标签组里选中的那个标签的下标;-1 = 没记(还原时选第一个到达的)。</summary>
    public int ActiveIndex { get; set; } = -1;

    /// <summary>标签条位置(<c>Top</c> / <c>Left</c> / <c>Right</c> …,按名字存);null 或不认识的值按置顶处理。</summary>
    public string? TabsPosition { get; set; }

    /// <summary>本版本不认识的字段,原样往返(理由见 <see cref="LayoutTemplate.ExtensionData" />)。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>这个节点是不是分栏。</summary>
    [JsonIgnore]
    public bool IsSplit => Orientation is not null;
}

/// <summary>分栏方向的取值。</summary>
public static class LayoutOrientation
{
    /// <summary>子节点左右排列。</summary>
    public const string Horizontal = "horizontal";

    /// <summary>子节点上下排列。</summary>
    public const string Vertical = "vertical";
}

/// <summary>模板里的一个标签:它是什么类型、指向哪条连接。</summary>
public sealed class LayoutDocument
{
    /// <summary>标签类型,取值见 <see cref="LayoutDocumentKind" />;不认识的类型还原时跳过。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>会话配置 Id(本地终端没有)。</summary>
    public Guid? ProfileId { get; set; }

    /// <summary>「远程 + 远程」双栏的右栏配置 Id;其余类型为 null。</summary>
    public Guid? SecondProfileId { get; set; }

    /// <summary>本地终端的 shell 标识(<c>pwsh</c> / <c>cmd</c> / <c>wsl</c> …);其余类型为 null。</summary>
    public string? ShellId { get; set; }

    /// <summary>保存时标签上的标题。只用来在「配置已经删了」时告诉用户少了哪个,不参与还原。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>标签是否固定。</summary>
    public bool IsPinned { get; set; }

    /// <summary>保存时它是不是整个工作区的活动标签(全树至多一个)。</summary>
    public bool IsActive { get; set; }

    /// <summary>本版本不认识的字段,原样往返(理由见 <see cref="LayoutTemplate.ExtensionData" />)。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// <see cref="LayoutDocument.Kind" /> 的取值。按「怎么重新打开它」分,而不是按协议分:
/// 同一条 SSH 配置既能开终端,也能开独立的文件标签,两者在模板里要分得开。
/// </summary>
public static class LayoutDocumentKind
{
    /// <summary>终端标签(SSH,或插件提供的终端协议,如 Telnet)。</summary>
    public const string Terminal = "terminal";

    /// <summary>单栏「本地 + 远程」文件标签(SFTP / FTP / 插件文件协议,也包括 SSH 配置开的 SFTP)。</summary>
    public const string Files = "files";

    /// <summary>「远程 + 远程」双栏文件标签,两条配置。</summary>
    public const string DualFiles = "dualFiles";

    /// <summary>插件工作台(Redis、MySQL …)。</summary>
    public const string Workspace = "workspace";

    /// <summary>本地终端(按 shell 类型还原)。</summary>
    public const string LocalShell = "localShell";
}

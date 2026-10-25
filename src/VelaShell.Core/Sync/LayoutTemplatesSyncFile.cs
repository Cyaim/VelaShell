using VelaShell.Core.Layouts;

namespace VelaShell.Core.Sync;

/// <summary>
/// 布局模板在 Gist 里的那份文件(<c>velashell-layouts.json</c>,与主载荷 <c>velashell-sync.json</c> 同一个 Gist、分开两个文件)。
/// 结构与主载荷的信封相同:明文时用 <see cref="Data" />,设了端到端口令时整段加密进 <see cref="CipherText" />。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么单独一个文件,而不是主载荷里多一段</b>:主载荷是整份推的,不认识新段的旧客户端拉下来会把它丢掉,
/// 再推一次就从云端抹了(快捷命令的 <c>HiddenBuiltInIds</c> 就是这么丢的)。旧客户端只读写 <c>velashell-sync.json</c>,
/// 而 GitHub 的「更新 Gist」只改请求里列出的文件、其余文件原样保留 —— 放在旁边的文件它根本碰不到。
/// 新客户端推送时两个文件在同一次提交里写,历史版本列表里的一个版本仍对应一台设备的一次推送。
/// </para>
/// <para>
/// <b>自带版本</b>(<see cref="SchemaVersion" />),与主载荷的版本互不牵连:读到比本机新的版本时只跳过模板这一份,
/// 不读也不写(写回去就是拿本机看得懂的旧形状盖掉新版本的数据),设置与连接照常同步。
/// </para>
/// </remarks>
public sealed class LayoutTemplatesSyncEnvelope
{
    /// <summary>格式标识。</summary>
    public const string FormatId = "velashell-layout-templates-sync";

    /// <summary>本机写出、也是本机认得的最高版本。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>格式标识;不是 <see cref="FormatId" /> 的文件当作坏文件。</summary>
    public string Format { get; set; } = FormatId;

    /// <summary>这份文件的结构版本。</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>生成时间(UTC)。</summary>
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>生成它的设备名。</summary>
    public string DeviceName { get; set; } = "";

    /// <summary>是否经端到端加密;true 时用 <see cref="CipherText" />,否则用 <see cref="Data" />。</summary>
    public bool Encrypted { get; set; }

    /// <summary>加密后的 <see cref="LayoutTemplatesSyncData" />(格式同主载荷的 <see cref="SyncEnvelope.CipherText" />)。</summary>
    public string? CipherText { get; set; }

    /// <summary>明文的模板与墓碑;仅在未加密时存在。</summary>
    public LayoutTemplatesSyncData? Data { get; set; }
}

/// <summary>布局模板同步文件的内容:全部模板加上保留期内的墓碑。</summary>
public sealed class LayoutTemplatesSyncData
{
    /// <summary>活着的模板(与本机库里存的是同一个形状)。</summary>
    public List<LayoutTemplate> Templates { get; set; } = [];

    /// <summary>删掉的模板留下的墓碑(过了 <see cref="LayoutTemplateMerge.TombstoneRetention" /> 的不再写)。</summary>
    public List<LayoutTemplateTombstone> Tombstones { get; set; } = [];
}

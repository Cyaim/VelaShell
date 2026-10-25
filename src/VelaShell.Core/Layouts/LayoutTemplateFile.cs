using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using VelaShell.Core.Import;

namespace VelaShell.Core.Layouts;

/// <summary>
/// 布局模板的导出文件:导出头(与设置导出同一个键 <see cref="SettingsTransfer.HeaderProperty" />,格式、版本、时间、个数)
/// 加一个 <c>templates</c> 数组,数组里每一项就是库里存的那份模板。
/// </summary>
/// <remarks>
/// <para>
/// <b>里面没有机密</b>:模板只记每个标签指向哪条连接配置(Id)与打开方式、本地终端的 shell 类型、保存时的标签标题,
/// 不含密码,也不含主机地址 —— 但标签标题常常就是 <c>用户@主机</c>,不算可以随手公开的东西。
/// </para>
/// <para>
/// <b>导入按「尽力读」</b>:导出头的格式对得上就读,版本更高也照读(模板各带自己的 <see cref="LayoutTemplate.SchemaVersion" />,
/// 不认识的标签类型打开时跳过,不认识的字段原样留着,与 §236 读库的口径相同);格式不对、不是 JSON、
/// 没有 <c>templates</c> 数组的一律当「不是布局模板文件」。连接配置在本机存不存在不在这里管,由调用方提示。
/// </para>
/// </remarks>
public static class LayoutTemplateFile
{
    /// <summary>导出头里的格式标识。</summary>
    public const string FormatId = "velashell-layout-templates";

    /// <summary>当前的导出格式版本。</summary>
    public const int CurrentVersion = 1;

    private const string TemplatesProperty = "templates";

    /// <summary>与设置导出同一套约定(camelCase、缩进、中文不转义、读时容忍注释与尾逗号),外加不写 null(「均分」的比例就是不写)。</summary>
    private static readonly JsonSerializerOptions Options = new(SettingsTransfer.Options)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>写出导出文件。</summary>
    /// <param name="templates">要导出的模板(按给出的顺序写)。</param>
    /// <param name="exportedAtUtc">导出时间。</param>
    /// <returns>JSON 文本。</returns>
    public static string Export(IEnumerable<LayoutTemplate> templates, DateTime exportedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(templates);
        LayoutTemplate[] list = [.. templates];
        var output = new JsonObject
        {
            [SettingsTransfer.HeaderProperty] = new JsonObject
            {
                ["format"] = FormatId,
                ["version"] = CurrentVersion,
                ["exportedAtUtc"] = exportedAtUtc.ToUniversalTime().ToString("O"),
                ["count"] = list.Length,
            },
            [TemplatesProperty] = JsonSerializer.SerializeToNode(list, Options),
        };
        return output.ToJsonString(Options);
    }

    /// <summary>解析一份导入文件。</summary>
    /// <param name="json">文件内容。</param>
    /// <returns>文件里能用的模板(可能为空);不是布局模板文件时为 null。</returns>
    public static IReadOnlyList<LayoutTemplate>? Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            if (JsonNode.Parse(json, NodeOptions, DocumentOptions) is not JsonObject root
                || root[SettingsTransfer.HeaderProperty] is not JsonObject header
                || header["format"] is not JsonValue format
                || !format.TryGetValue(out string? formatId)
                || !string.Equals(formatId, FormatId, StringComparison.OrdinalIgnoreCase)
                || root[TemplatesProperty] is not JsonArray array)
            {
                return null;
            }
            List<LayoutTemplate?> parsed = array.Deserialize<List<LayoutTemplate?>>(Options) ?? [];
            return [.. parsed.Select(Sanitize).OfType<LayoutTemplate>()];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// 一项能不能用、用之前扶正:名字去首尾空白、超长截断,空名字与一个标签都剪不出来的树不要;
    /// Id 写成全零的(手写的文件)换一个新的。树只拿来校验、不替换 —— 剪枝会新建节点,把不认识的字段剪丢。
    /// </summary>
    private static LayoutTemplate? Sanitize(LayoutTemplate? template)
    {
        if (template is null)
        {
            return null;
        }
        string name = template.Name?.Trim() ?? string.Empty;
        if (name.Length == 0 || LayoutTree.Prune(template.Root, static _ => true) is null)
        {
            return null;
        }
        template.Name = name.Length > LayoutTemplate.MaxNameLength ? name[..LayoutTemplate.MaxNameLength] : name;
        if (template.Id == Guid.Empty)
        {
            template.Id = Guid.NewGuid();
        }
        return template;
    }
}

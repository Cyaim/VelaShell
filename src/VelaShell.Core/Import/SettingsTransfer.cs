using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using VelaShell.Core.Models;

namespace VelaShell.Core.Import;

/// <summary>
/// 一份待导入的设置文件:解析过、认得出至少一个分类的那种。
/// </summary>
public sealed class SettingsImportFile
{
    internal SettingsImportFile(JsonObject raw, AppSettings typed, IReadOnlyList<SettingsCategory> categories, bool isLegacy)
    {
        Raw = raw;
        Typed = typed;
        Canonical = SettingsTransfer.ToNode(typed);
        Categories = categories;
        IsLegacy = isLegacy;
    }

    /// <summary>文件里带着的分类(按界面顺序)。</summary>
    public IReadOnlyList<SettingsCategory> Categories { get; }

    /// <summary>旧格式:没有导出头的整份 <see cref="AppSettings" />(本版本之前的导出,或手写的)。</summary>
    public bool IsLegacy { get; }

    /// <summary>文件原文(键不分大小写):只用来判断某个键在不在文件里。</summary>
    internal JsonObject Raw { get; }

    /// <summary>按类型读出来的那一份:文件没写的字段是类型的默认值。</summary>
    internal AppSettings Typed { get; }

    /// <summary><see cref="Typed" /> 再写回 JSON:键一律 camelCase、字段齐全,合并时从这里取值。</summary>
    internal JsonObject Canonical { get; }
}

/// <summary>导入合并的结果。</summary>
/// <param name="Settings">合并之后的整份设置(已 <see cref="AppSettings.Normalize" />)。</param>
/// <param name="Applied">实际覆盖了的分类。</param>
/// <param name="ProxyPasswordMissing">代理要认证、文件里没带密码、本机那份又对不上 —— 得重填。</param>
/// <param name="WebhookUrlMissing">打开了 Webhook 告警、文件里没带地址、本机也没填 —— 得重填。</param>
public sealed record SettingsImportMerge(
    AppSettings Settings,
    IReadOnlyList<SettingsCategory> Applied,
    bool ProxyPasswordMissing,
    bool WebhookUrlMissing);

/// <summary>
/// 设置的导出文件:按分类挑着写、机密一律不写;导入时只覆盖文件里带着、且用户勾了的分类。
/// </summary>
/// <remarks>
/// <para>
/// <b>文件形状仍是 <see cref="AppSettings" /> 的 JSON</b>,只是只留选中的那几块,外加一个导出头
/// (<see cref="HeaderProperty" />)。没做成「头 + settings 包一层」:旧版本导入时整份反序列化成
/// <see cref="AppSettings" />、整体覆盖 —— 包一层的文件在旧版本上等于「全部恢复默认」,
/// 这个形状在旧版本上只是没带的分类回到默认值。
/// </para>
/// <para>
/// <b>旧格式照旧能导入</b>:没有导出头的整份设置,哪些分类在文件里(按 JSON 键判断)就算带着哪些。
/// 一个分类的键一个都不在的 JSON(<c>{}</c>、选错的 package.json)认作「不是设置文件」——
/// 以前它们会被当成一份全默认的设置照单全收。
/// </para>
/// </remarks>
public static class SettingsTransfer
{
    /// <summary>导出头所在的键。</summary>
    public const string HeaderProperty = "velaShellExport";

    /// <summary>导出头里的格式标识。</summary>
    public const string FormatId = "velashell-settings";

    /// <summary>当前的导出格式版本。没有导出头的旧文件算版本 1。</summary>
    public const int CurrentVersion = 2;

    /// <summary>
    /// 文件的 JSON 约定:camelCase、缩进、中文原样不转义;读时不分大小写、容忍注释与尾逗号(文件是给人改的)。
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>写出导出文件:只含 <paramref name="categoryIds" /> 这几类,机密与运行状态不写。</summary>
    /// <param name="settings">当前设置(不会被修改)。</param>
    /// <param name="categoryIds">要导出的分类;认不出的忽略。</param>
    /// <param name="exportedAtUtc">导出时间。</param>
    /// <returns>JSON 文本。</returns>
    public static string Export(AppSettings settings, IEnumerable<string> categoryIds, DateTime exportedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(categoryIds);
        SettingsCategory[] selected = Resolve(categoryIds);
        JsonObject full = ToNode(settings);

        var output = new JsonObject
        {
            [HeaderProperty] = new JsonObject
            {
                ["format"] = FormatId,
                ["version"] = CurrentVersion,
                ["exportedAtUtc"] = exportedAtUtc.ToUniversalTime().ToString("O"),
                ["categories"] = new JsonArray([.. selected.Select(c => (JsonNode?)JsonValue.Create(c.Id))]),
            },
        };
        // 按 AppSettings 自己的属性顺序写,读起来与旧导出、与库里落盘的那份是同一个样子。
        foreach ((string key, JsonNode? value) in full)
        {
            if (selected.FirstOrDefault(c => c.Block == key || c.TopLevel.Contains(key)) is not { } owner)
            {
                continue;
            }
            JsonNode? copy = value?.DeepClone();
            if (copy is JsonObject block && owner.Block == key)
            {
                foreach ((string _, string field) in SettingsCategories.SecretFields
                             .Concat(SettingsCategories.StateFields)
                             .Where(f => f.Block == key))
                {
                    block.Remove(field);
                }
            }
            output[key] = copy;
        }
        return output.ToJsonString(Options);
    }

    /// <summary>解析一份导入文件;不是设置文件(不是 JSON 对象、类型对不上、一个分类都认不出)时为 null。</summary>
    /// <param name="json">文件内容。</param>
    /// <returns>解析结果,或 null。</returns>
    public static SettingsImportFile? Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonObject? raw;
        AppSettings? typed;
        try
        {
            raw = JsonNode.Parse(json, NodeOptions, DocumentOptions) as JsonObject;
            // 整份按类型读一遍:字段类型对不上("general": 5)在这里就认作坏文件,
            // 不能等到合并时才炸、或者半份应用上去。
            typed = raw?.Deserialize<AppSettings>(Options);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
        if (raw is null || typed is null)
        {
            return null;
        }
        SettingsCategory[] present =
        [
            .. SettingsCategories.All.Where(c => raw.ContainsKey(c.Block) || c.TopLevel.Any(raw.ContainsKey))
        ];
        return present.Length == 0
            ? null
            : new SettingsImportFile(raw, typed, present, isLegacy: !raw.ContainsKey(HeaderProperty));
    }

    /// <summary>
    /// 把文件里的 <paramref name="categoryIds" /> 这几类合并到 <paramref name="current" /> 上(只覆盖它们,其余原样)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 合并在 JSON 层做:被选中、且文件里带着的根级字段与分块整段换成文件里的,再整份反序列化回来。
    /// 分块里文件没写的字段取类型的默认值(与以前整份导入同一口径);运行状态(窗口尺寸、上次打开的会话)保留本机的。
    /// </para>
    /// <para>
    /// <b>文件里没带机密时:</b>代理的类型、主机、端口、用户名与本机完全相同就留着本机的密码 —— 同一个代理换了台机器而已;
    /// 有一项不同就不留:那是另一台代理,把本机的密码发给它等于把密码交给文件的作者。Webhook 地址一律留本机的:
    /// 导出从不写它,「文件里没有」只说明没导出,不说明要清空。文件里明写着的(旧版本导出过 Webhook 地址、手写的代理密码)照用。
    /// </para>
    /// </remarks>
    /// <param name="current">本机当前的设置(不会被修改)。</param>
    /// <param name="file">导入文件。</param>
    /// <param name="categoryIds">要覆盖的分类;文件里没带的、认不出的忽略。</param>
    /// <returns>合并结果。</returns>
    public static SettingsImportMerge Merge(AppSettings current, SettingsImportFile file, IEnumerable<string> categoryIds)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(categoryIds);
        SettingsCategory[] applied = [.. Resolve(categoryIds).Where(c => file.Categories.Any(p => p.Id == c.Id))];
        JsonObject result = ToNode(current);

        foreach (SettingsCategory category in applied)
        {
            foreach (string key in category.TopLevel.Where(file.Raw.ContainsKey))
            {
                result[key] = file.Canonical[key]?.DeepClone();
            }
            if (!file.Raw.ContainsKey(category.Block))
            {
                continue;
            }
            // 显式写成 null 的分块按「这一类全取默认值」处理,与以前 Normalize 给 null 分块补默认值同一口径。
            JsonObject block = file.Canonical[category.Block]?.DeepClone() as JsonObject
                               ?? ToNode(new AppSettings())[category.Block]!.DeepClone().AsObject();
            var localBlock = result[category.Block] as JsonObject;
            foreach ((string _, string field) in SettingsCategories.StateFields.Where(f => f.Block == category.Block))
            {
                if (localBlock is not null && localBlock.TryGetPropertyValue(field, out JsonNode? kept))
                {
                    block[field] = kept?.DeepClone();
                }
            }
            result[category.Block] = block;
        }

        AppSettings merged = result.Deserialize<AppSettings>(Options) ?? new AppSettings();

        bool proxyPasswordMissing = false;
        bool webhookUrlMissing = false;
        if (applied.Any(c => c.Id == SettingsCategories.Proxy) && file.Raw.ContainsKey("proxy"))
        {
            if (string.IsNullOrEmpty(file.Typed.Proxy?.Password))
            {
                merged.Proxy.Password = SameProxy(merged.Proxy, current.Proxy) ? current.Proxy.Password : string.Empty;
            }
            // 只有 http / socks5 才读用户名密码(none / system 根本不消费它们)。
            proxyPasswordMissing = merged.Proxy.Type is "http" or "socks5"
                                   && merged.Proxy.Username.Length > 0
                                   && merged.Proxy.Password.Length == 0;
        }
        if (applied.Any(c => c.Id == SettingsCategories.Security) && file.Raw.ContainsKey("security"))
        {
            if (string.IsNullOrEmpty(file.Typed.Security?.WebhookUrl))
            {
                merged.Security.WebhookUrl = current.Security.WebhookUrl;
            }
            webhookUrlMissing = merged.Security.AlertWebhook && string.IsNullOrWhiteSpace(merged.Security.WebhookUrl);
        }

        merged.Normalize();
        return new SettingsImportMerge(merged, applied, proxyPasswordMissing, webhookUrlMissing);
    }

    /// <summary>按本文件的约定把设置写成 JSON 对象(键 camelCase,读取不分大小写)。</summary>
    internal static JsonObject ToNode(AppSettings settings) =>
        JsonNode.Parse(JsonSerializer.Serialize(settings, Options), NodeOptions)!.AsObject();

    private static SettingsCategory[] Resolve(IEnumerable<string> ids)
    {
        HashSet<string> wanted = new(ids, StringComparer.OrdinalIgnoreCase);
        return [.. SettingsCategories.All.Where(c => wanted.Contains(c.Id))];
    }

    private static bool SameProxy(ProxyOptions a, ProxyOptions b) =>
        string.Equals(a.Type, b.Type, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host.Trim(), b.Host.Trim(), StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port
        && string.Equals(a.Username, b.Username, StringComparison.Ordinal);
}

using System.Collections.ObjectModel;
using System.Diagnostics;
using ReactiveUI;
using VelaShell.Core.Layouts;
using VelaShell.Core.Resources;
using VelaShell.Docking;
using VelaShell.Docking.Model;
using VelaShell.Presentation.ViewModels;
using VelaShell.Services.Layouts;

namespace VelaShell.ViewModels;

/// <summary>布局模板的增删改与打开要问用户的那几句话(由视图层按属主窗口实现)。</summary>
public interface ILayoutTemplateDialogs
{
    /// <summary>问一个名字;取消返回 null。</summary>
    /// <param name="title">弹窗标题。</param>
    /// <param name="initialValue">输入框里预填的值。</param>
    /// <param name="message">输入框上方的说明;可空。</param>
    /// <returns>用户输入的名字。</returns>
    Task<string?> PromptNameAsync(string title, string initialValue, string? message);

    /// <summary>确认一件事。</summary>
    /// <param name="title">弹窗标题。</param>
    /// <param name="message">正文。</param>
    /// <param name="confirmText">确认按钮的文字。</param>
    /// <param name="danger">确认按钮是否按危险操作渲染(删除)。</param>
    /// <returns>用户点了确认返回 <see langword="true" />。</returns>
    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger);
}

/// <summary>模板列表里的一行。</summary>
/// <param name="template">模板。</param>
public sealed class LayoutTemplateItemViewModel(LayoutTemplate template)
{
    /// <summary>背后的模板。</summary>
    public LayoutTemplate Template { get; } = template;

    /// <summary>模板名。</summary>
    public string Name => Template.Name;

    /// <summary>模板里的标签数。</summary>
    public int DocumentCount { get; } = LayoutTree.Documents(template.Root).Count();

    /// <summary>「N 个标签 · M 个窗格」。</summary>
    public string Summary => Strings.Format("LayoutTemplate_Summary", DocumentCount, LayoutTree.GroupCount(Template.Root));
}

/// <summary>
/// 命名布局模板:把当前的分屏存成模板、改名、覆盖、删除,以及打开一个模板;
/// 外加「恢复会话」顺带的那一份「退出时的布局」。
/// </summary>
/// <remarks>
/// <para>
/// 入口有三处,行为同一套:标题栏的「布局模板」下拉、命令面板(两条命令 + 每个模板一条)、
/// 「管理布局模板」窗口。
/// </para>
/// <para>
/// 存进模板的只有<b>能重新打开</b>的标签:已保存的连接(按配置 Id)与本地终端(按 shell 类型)。
/// 插件面板(AI 聊天等)、快速连接与外部拉起的临时连接(配置不在库里)跳过,存完之后如实说出少了哪几个。
/// </para>
/// </remarks>
public sealed class LayoutTemplatesViewModel : ReactiveObject
{
    /// <summary>打开的标签数超过这个值先确认(与资源管理器的整组打开同一个阈值)。</summary>
    public const int OpenManyConfirmThreshold = 10;

    /// <summary>提示里最多点名几个标签,其余用省略号。</summary>
    private const int MaxNamedInNotice = 5;

    private readonly LayoutTemplateStore _store;
    private readonly DockWorkspace _layout;
    private readonly LayoutTemplateLauncher _launcher;
    private readonly Func<DockDocument, LayoutDocument?> _describe;
    private readonly Func<Guid, Task<bool>> _profileExists;
    private readonly ToastHostViewModel _toasts;
    private readonly Func<DateTimeOffset> _now;
    private readonly List<LayoutTemplate> _templates = [];

    /// <summary>装配布局模板的视图模型。</summary>
    /// <param name="store">模板的持久化。</param>
    /// <param name="layout">停靠工作区。</param>
    /// <param name="opener">查配置、开标签的那一面。</param>
    /// <param name="describe">一个文档在模板里记成什么(见 <see cref="LayoutDocumentDescriber" />)。</param>
    /// <param name="profileExists">一条配置是不是还在库里(临时连接不在)。</param>
    /// <param name="toasts">浮层提示。</param>
    /// <param name="now">取当前时间;单测可注入。</param>
    public LayoutTemplatesViewModel(
        LayoutTemplateStore store,
        DockWorkspace layout,
        ILayoutDocumentOpener opener,
        Func<DockDocument, LayoutDocument?> describe,
        Func<Guid, Task<bool>> profileExists,
        ToastHostViewModel toasts,
        Func<DateTimeOffset>? now = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _describe = describe ?? throw new ArgumentNullException(nameof(describe));
        _profileExists = profileExists ?? throw new ArgumentNullException(nameof(profileExists));
        _toasts = toasts ?? throw new ArgumentNullException(nameof(toasts));
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _launcher = new(layout, opener ?? throw new ArgumentNullException(nameof(opener)));
    }

    /// <summary>全部模板,按名字的自然顺序。</summary>
    public ObservableCollection<LayoutTemplateItemViewModel> Templates { get; } = [];

    /// <summary>有没有模板(空列表的提示用)。</summary>
    public bool HasTemplates => Templates.Count > 0;

    /// <summary>
    /// 标题栏下拉与命令面板用的对话框(由主窗口装上)。管理窗口开着时它把自己的那份传进各个方法,
    /// 弹框才会叠在它上面而不是主窗口上。
    /// </summary>
    public ILayoutTemplateDialogs? Dialogs { get; set; }

    /// <summary>请求打开「管理布局模板」窗口(由主窗口承接:视图层才建得了窗口)。</summary>
    public event Action? ManagerRequested;

    /// <summary>打开「管理布局模板」窗口。</summary>
    public void OpenManager() => ManagerRequested?.Invoke();

    /// <summary>从存储读入模板列表。</summary>
    /// <returns>表示异步操作的任务。</returns>
    public async Task LoadAsync()
    {
        IReadOnlyList<LayoutTemplate> loaded = await _store.LoadAllAsync().ConfigureAwait(true);
        _templates.Clear();
        _templates.AddRange(loaded);
        Publish();
    }

    // ---- 保存 / 覆盖 / 改名 / 删除 ----

    /// <summary>把当前布局存成一个新模板(名字撞上已有的,问一句要不要替换它)。</summary>
    /// <param name="dialogs">这一次用的对话框;不给用 <see cref="Dialogs" />。</param>
    /// <returns>存上了返回 <see langword="true" />。</returns>
    public async Task<bool> SaveCurrentAsync(ILayoutTemplateDialogs? dialogs = null)
    {
        if ((dialogs ?? Dialogs) is not { } ui)
        {
            return false;
        }
        (LayoutNode? root, List<string> skipped) = await CaptureCurrentAsync().ConfigureAwait(true);
        if (root is null)
        {
            _toasts.Warning(Strings.Get("LayoutTemplate_NothingToSave"));
            return false;
        }
        string? name = Normalize(await ui.PromptNameAsync(
                                     Strings.Get("LayoutTemplate_SaveTitle"),
                                     DefaultName(),
                                     Strings.Get("LayoutTemplate_NamePrompt")).ConfigureAwait(true));
        if (name is null)
        {
            return false;
        }
        LayoutTemplate? existing = FindByName(name, except: null);
        if (existing is not null
            && !await ui.ConfirmAsync(
                    Strings.Get("LayoutTemplate_ReplaceTitle"),
                    Strings.Format("LayoutTemplate_ReplaceMessage", existing.Name),
                    Strings.Get("LayoutTemplate_Replace"),
                    danger: false).ConfigureAwait(true))
        {
            return false;
        }
        DateTimeOffset now = _now();
        var template = new LayoutTemplate
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            Name = name,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
            Root = root,
        };
        if (!await TrySaveAsync(template).ConfigureAwait(true))
        {
            return false;
        }
        ReportSaved(template, skipped);
        return true;
    }

    /// <summary>用当前布局覆盖一个已有的模板(名字与 Id 不变)。</summary>
    /// <param name="item">要覆盖的模板。</param>
    /// <param name="dialogs">这一次用的对话框;不给用 <see cref="Dialogs" />。</param>
    /// <returns>覆盖了返回 <see langword="true" />。</returns>
    public async Task<bool> OverwriteAsync(LayoutTemplateItemViewModel item, ILayoutTemplateDialogs? dialogs = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        if ((dialogs ?? Dialogs) is not { } ui)
        {
            return false;
        }
        (LayoutNode? root, List<string> skipped) = await CaptureCurrentAsync().ConfigureAwait(true);
        if (root is null)
        {
            _toasts.Warning(Strings.Get("LayoutTemplate_NothingToSave"));
            return false;
        }
        if (!await ui.ConfirmAsync(
                Strings.Get("LayoutTemplate_OverwriteTitle"),
                Strings.Format("LayoutTemplate_OverwriteMessage", item.Name),
                Strings.Get("LayoutTemplate_Overwrite"),
                danger: false).ConfigureAwait(true))
        {
            return false;
        }
        LayoutTemplate template = Copy(item.Template);
        template.Root = root;
        template.UpdatedAt = _now();
        template.SchemaVersion = LayoutTemplate.CurrentSchemaVersion;
        if (!await TrySaveAsync(template).ConfigureAwait(true))
        {
            return false;
        }
        ReportSaved(template, skipped);
        return true;
    }

    /// <summary>给一个模板改名;新名字与另一个模板重名时拒绝(改名不该悄悄吞掉别的模板)。</summary>
    /// <param name="item">要改名的模板。</param>
    /// <param name="dialogs">这一次用的对话框;不给用 <see cref="Dialogs" />。</param>
    /// <returns>改了返回 <see langword="true" />。</returns>
    public async Task<bool> RenameAsync(LayoutTemplateItemViewModel item, ILayoutTemplateDialogs? dialogs = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        if ((dialogs ?? Dialogs) is not { } ui)
        {
            return false;
        }
        string? name = Normalize(await ui.PromptNameAsync(
                                     Strings.Get("LayoutTemplate_RenameTitle"),
                                     item.Name,
                                     Strings.Get("LayoutTemplate_NamePrompt")).ConfigureAwait(true));
        if (name is null || string.Equals(name, item.Name, StringComparison.Ordinal))
        {
            return false;
        }
        if (FindByName(name, except: item.Template.Id) is { } other)
        {
            _toasts.Warning(Strings.Format("LayoutTemplate_NameTaken", other.Name));
            return false;
        }
        LayoutTemplate template = Copy(item.Template);
        template.Name = name;
        template.UpdatedAt = _now();
        return await TrySaveAsync(template).ConfigureAwait(true);
    }

    /// <summary>删除一个模板(先确认;只删模板,不碰它引用的连接)。</summary>
    /// <param name="item">要删的模板。</param>
    /// <param name="dialogs">这一次用的对话框;不给用 <see cref="Dialogs" />。</param>
    /// <returns>删了返回 <see langword="true" />。</returns>
    public async Task<bool> DeleteAsync(LayoutTemplateItemViewModel item, ILayoutTemplateDialogs? dialogs = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        if ((dialogs ?? Dialogs) is not { } ui
            || !await ui.ConfirmAsync(
                    Strings.Get("LayoutTemplate_DeleteTitle"),
                    Strings.Format("LayoutTemplate_DeleteMessage", item.Name),
                    Strings.Get("Delete"),
                    danger: true).ConfigureAwait(true))
        {
            return false;
        }
        try
        {
            await _store.DeleteAsync(item.Template.Id).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _toasts.Error(Strings.Format("LayoutTemplate_DeleteFailed", ex.Message));
            return false;
        }
        _templates.RemoveAll(template => template.Id == item.Template.Id);
        Publish();
        return true;
    }

    // ---- 打开 ----

    /// <summary>
    /// 打开一个模板:按它的分屏新开连接、排好;配置已删除的标签跳过并提示,其余照开。
    /// </summary>
    /// <param name="item">要打开的模板。</param>
    /// <param name="dialogs">这一次用的对话框;不给用 <see cref="Dialogs" />(只在标签多时用来确认)。</param>
    /// <returns>全部标签的打开流程都结束时完成;用户在确认框上取消时返回 null。</returns>
    public async Task<LayoutOpenResult?> OpenAsync(LayoutTemplateItemViewModel item, ILayoutTemplateDialogs? dialogs = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.DocumentCount > OpenManyConfirmThreshold
            && (dialogs ?? Dialogs) is { } ui
            && !await ui.ConfirmAsync(
                    Strings.Get("Tree_OpenManyTitle"),
                    Strings.Format("Tree_OpenManyConfirm", item.DocumentCount),
                    Strings.Get("Tree_OpenManyAction"),
                    danger: false).ConfigureAwait(true))
        {
            return null;
        }
        LayoutOpenResult result = await _launcher.OpenAsync(item.Template.Root).ConfigureAwait(true);
        if (result.Opened == 0)
        {
            _toasts.Warning(Strings.Format("LayoutTemplate_NothingToOpen", item.Name));
        }
        else if (result.Missing.Count > 0)
        {
            _toasts.Warning(Strings.Format(
                "LayoutTemplate_OpenSkipped",
                item.Name,
                result.Missing.Count,
                JoinNames(result.Missing.Select(DescribeMissing))));
        }
        return result;
    }

    // ---- 「恢复会话」的那一份 ----

    /// <summary>
    /// 拍下「退出时的布局」:只记此刻在线的标签(见 <see cref="LayoutDocumentDescriber.IsLive" />)。
    /// 同步、不查库 —— 退出路径上只做本地写;临时连接的配置不在库里,下次启动时自然对不上、被静默跳过。
    /// </summary>
    /// <returns>布局;一个在线的标签都没有时为 null。</returns>
    public LayoutTemplate? CaptureSessionLayout()
    {
        LayoutNode? root = DockLayoutSnapshot.Capture(
            _layout,
            document => LayoutDocumentDescriber.IsLive(document) ? _describe(document) : null);
        if (root is null)
        {
            return null;
        }
        DateTimeOffset now = _now();
        return new() { Id = Guid.Empty, Name = string.Empty, CreatedAt = now, UpdatedAt = now, Root = root };
    }

    /// <summary>把「退出时的布局」写进存储(null = 清掉旧的)。</summary>
    /// <param name="layout">要记下的布局。</param>
    /// <returns>表示异步操作的任务。</returns>
    public Task SaveSessionLayoutAsync(LayoutTemplate? layout) => _store.SaveLastSessionAsync(layout);

    /// <summary>
    /// 启动时按「退出时的布局」恢复会话。配置已删除的标签静默跳过(其中多半是没入库的临时连接,
    /// 启动时冒一句「少了一个」只会让人摸不着头脑)。
    /// </summary>
    /// <returns>确实还原出了标签返回 <see langword="true" />;没有可还原的布局时返回 <see langword="false" />,调用方退回逐条重连。</returns>
    public async Task<bool> RestoreSessionLayoutAsync()
    {
        if (await _store.LoadLastSessionAsync().ConfigureAwait(true) is not { Root: { } root })
        {
            return false;
        }
        LayoutOpenResult result = await _launcher.OpenAsync(root).ConfigureAwait(true);
        if (result.Missing.Count > 0)
        {
            Trace.WriteLine($"[LayoutTemplates] 恢复上次的布局时跳过了 {result.Missing.Count} 个标签。");
        }
        return result.Opened > 0;
    }

    // ---- 内部 ----

    /// <summary>
    /// 拍下当前布局,并去掉配置不在库里的标签(快速连接、外部拉起的临时配置)。
    /// </summary>
    /// <returns>布局树,以及没能存进去的标签标题。</returns>
    internal async Task<(LayoutNode? Root, List<string> Skipped)> CaptureCurrentAsync()
    {
        var skippedDocuments = new List<DockDocument>();
        LayoutNode? root = DockLayoutSnapshot.Capture(_layout, _describe, skippedDocuments);
        List<string> skipped = [.. skippedDocuments.Select(document => document.Title)];
        if (root is null)
        {
            return (null, skipped);
        }
        var exists = new Dictionary<Guid, bool>();
        foreach (Guid id in LayoutTree.Documents(root)
                                      .SelectMany(document => (Guid?[])[document.ProfileId, document.SecondProfileId])
                                      .OfType<Guid>()
                                      .Distinct())
        {
            try
            {
                exists[id] = await _profileExists(id).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Trace.WriteLine($"[LayoutTemplates] 查询配置 {id} 失败:{ex.Message}");
                exists[id] = false;
            }
        }
        var dropped = new List<LayoutDocument>();
        root = LayoutTree.Prune(
            root,
            document => (document.ProfileId is not { } first || exists.GetValueOrDefault(first))
                        && (document.SecondProfileId is not { } second || exists.GetValueOrDefault(second)),
            dropped);
        skipped.AddRange(dropped.Select(document => document.Title));
        root?.Proportion = null;
        return (root, skipped);
    }

    private async Task<bool> TrySaveAsync(LayoutTemplate template)
    {
        try
        {
            await _store.SaveAsync(template).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _toasts.Error(Strings.Format("LayoutTemplate_SaveFailed", ex.Message));
            return false;
        }
        _templates.RemoveAll(existing => existing.Id == template.Id);
        _templates.Add(template);
        Publish();
        return true;
    }

    private void ReportSaved(LayoutTemplate template, List<string> skipped)
    {
        int documents = LayoutTree.Documents(template.Root).Count();
        if (skipped.Count == 0)
        {
            _toasts.Info(Strings.Format("LayoutTemplate_Saved", template.Name, documents, LayoutTree.GroupCount(template.Root)));
            return;
        }
        _toasts.Warning(Strings.Format("LayoutTemplate_SavedPartially", template.Name, skipped.Count, JoinNames(skipped)));
    }

    private void Publish()
    {
        IReadOnlyList<LayoutTemplate> sorted = LayoutTemplateStore.Sort(_templates);
        _templates.Clear();
        _templates.AddRange(sorted);
        Templates.Clear();
        foreach (LayoutTemplate template in sorted)
        {
            Templates.Add(new(template));
        }
        this.RaisePropertyChanged(nameof(HasTemplates));
    }

    private LayoutTemplate? FindByName(string name, Guid? except) =>
        _templates.FirstOrDefault(template =>
            template.Id != except && string.Equals(template.Name, name, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>默认名「布局 N」:N 取第一个没被占用的序号。</summary>
    private string DefaultName()
    {
        for (int n = _templates.Count + 1; ; n++)
        {
            string candidate = Strings.Format("LayoutTemplate_DefaultName", n);
            if (FindByName(candidate, except: null) is null)
            {
                return candidate;
            }
        }
    }

    private static string? Normalize(string? name)
    {
        string trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return null;
        }
        return trimmed.Length > LayoutTemplate.MaxNameLength ? trimmed[..LayoutTemplate.MaxNameLength] : trimmed;
    }

    private static LayoutTemplate Copy(LayoutTemplate template) => new()
    {
        SchemaVersion = template.SchemaVersion,
        Id = template.Id,
        Name = template.Name,
        CreatedAt = template.CreatedAt,
        UpdatedAt = template.UpdatedAt,
        Root = template.Root,
    };

    private static string DescribeMissing(LayoutMissingDocument missing)
    {
        string title = string.IsNullOrWhiteSpace(missing.Title) ? Strings.Get("LayoutTemplate_Untitled") : missing.Title;
        return missing.Reason switch
        {
            LayoutMissingReason.ProfileDeleted => Strings.Format("LayoutTemplate_MissingProfile", title),
            LayoutMissingReason.ShellUnavailable => Strings.Format("LayoutTemplate_MissingShell", title),
            _ => Strings.Format("LayoutTemplate_MissingUnsupported", title),
        };
    }

    private static string JoinNames(IEnumerable<string> names)
    {
        List<string> all = [.. names.Select(name => string.IsNullOrWhiteSpace(name) ? Strings.Get("LayoutTemplate_Untitled") : name)];
        string joined = string.Join(Strings.Get("LayoutTemplate_ListSeparator"), all.Take(MaxNamedInNotice));
        return all.Count > MaxNamedInNotice ? joined + "…" : joined;
    }
}

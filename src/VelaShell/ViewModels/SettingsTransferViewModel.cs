using System.Collections.ObjectModel;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.ViewModels;

/// <summary>设置导出 / 导入对话框的两种用法。</summary>
public enum SettingsTransferMode
{
    /// <summary>导出:列出全部分类。</summary>
    Export,

    /// <summary>导入:只列文件里带着的分类。</summary>
    Import
}

/// <summary>对话框里的一个分类勾选项。</summary>
public sealed class SettingsTransferItem : ReactiveObject
{
    /// <summary>创建勾选项;敏感分类默认不勾。</summary>
    /// <param name="category">分类。</param>
    /// <param name="warning">勾上之后显示的警示;非敏感分类为空。</param>
    public SettingsTransferItem(SettingsCategory category, string warning)
    {
        Category = category;
        Warning = warning;
        IsChecked = !category.IsSensitive;
    }

    /// <summary>分类。</summary>
    public SettingsCategory Category { get; }

    /// <summary>界面上的名称。</summary>
    public string Label => Category.Label;

    /// <summary>敏感分类(代理、安全审计)。</summary>
    public bool IsSensitive => Category.IsSensitive;

    /// <summary>勾上之后显示的警示。</summary>
    public string Warning { get; }

    /// <summary>勾上了没有。</summary>
    public bool IsChecked
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(ShowWarning));
        }
    }

    /// <summary>敏感分类勾上时才显示警示:没勾的时候它不会进文件、也不会被导入,说了只是噪声。</summary>
    public bool ShowWarning => IsSensitive && IsChecked;
}

/// <summary>
/// 设置导出 / 导入对话框:勾选分类。<b>敏感分类(代理、安全审计)默认不勾</b>,勾上就在下面写明它带着什么、会改掉什么。
/// </summary>
/// <remarks>
/// <para>
/// 选了「勾上时警示」而不是「勾上时强制加密」:设置里真正的机密只有代理密码与告警 Webhook 地址两项,
/// 导出一律不写它们(<see cref="Core.Import.SettingsTransfer" />),导入时文件里没有就保留本机的;
/// 敏感分类里剩下的是地址、账号、信任目标与策略开关,不是密码,却也不该出现在一个随手发出去的文件里 ——
/// 默认不勾、勾上明说,就够把「顺手全导出」挡住。带机密跨设备迁移已经有云同步的端到端口令(§13-C),
/// 再给设置文件配一套口令,只是让同一件事有两条路、两份要维护的加密格式。
/// </para>
/// <para>
/// 导入同样默认不勾敏感分类:别人给的设置文件里若带着安全审计,导进来就能顺手关掉外部拉起的确认、
/// 加上几条「不再询问」的目标;带着代理,就能让之后的全部出站连接改走对方指定的服务器。
/// </para>
/// </remarks>
public sealed class SettingsTransferViewModel : ReactiveObject
{
    /// <summary>创建对话框的视图模型。</summary>
    /// <param name="mode">导出或导入。</param>
    /// <param name="categories">列出哪些分类:导出给全部,导入给文件里带着的。</param>
    /// <param name="fileName">导入时的文件名(只用来显示)。</param>
    public SettingsTransferViewModel(SettingsTransferMode mode, IReadOnlyList<SettingsCategory> categories, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(categories);
        Mode = mode;
        Items = [.. categories.Select(c => new SettingsTransferItem(c, WarningFor(mode, c)))];
        foreach (SettingsTransferItem item in Items)
        {
            item.WhenAnyValue(x => x.IsChecked).Subscribe(_ => this.RaisePropertyChanged(nameof(CanConfirm)));
        }
        Summary = mode == SettingsTransferMode.Export
            ? Strings.Get("SetTransfer_ExportSummary")
            : Strings.Format("SetTransfer_ImportSummary", fileName ?? string.Empty);
    }

    /// <summary>导出或导入。</summary>
    public SettingsTransferMode Mode { get; }

    /// <summary>是导出。</summary>
    public bool IsExport => Mode == SettingsTransferMode.Export;

    /// <summary>标题。</summary>
    public string Title => Strings.Get(IsExport ? "SetGeneral_ExportDialogTitle" : "SetGeneral_ImportDialogTitle");

    /// <summary>对话框顶部的说明。</summary>
    public string Summary { get; }

    /// <summary>主按钮的文字。</summary>
    public string ConfirmText => Strings.Get(IsExport ? "SetTransfer_ExportButton" : "SetTransfer_ImportButton");

    /// <summary>分类勾选项(按界面顺序)。</summary>
    public ObservableCollection<SettingsTransferItem> Items { get; }

    /// <summary>至少勾了一类才能确定。</summary>
    public bool CanConfirm => Items.Any(i => i.IsChecked);

    /// <summary>勾上了的分类标识(按界面顺序)。</summary>
    public IReadOnlyList<string> SelectedIds => [.. Items.Where(i => i.IsChecked).Select(i => i.Category.Id)];

    /// <summary>敏感分类勾上之后的警示;导出说它带着什么,导入说它会改掉什么。</summary>
    internal static string WarningFor(SettingsTransferMode mode, SettingsCategory category) =>
        (mode, category.Id) switch
        {
            (SettingsTransferMode.Export, SettingsCategories.Proxy) => Strings.Get("SetTransfer_ProxyExportWarning"),
            (SettingsTransferMode.Export, SettingsCategories.Security) => Strings.Get("SetTransfer_SecurityExportWarning"),
            (SettingsTransferMode.Import, SettingsCategories.Proxy) => Strings.Get("SetTransfer_ProxyImportWarning"),
            (SettingsTransferMode.Import, SettingsCategories.Security) => Strings.Get("SetTransfer_SecurityImportWarning"),
            _ => string.Empty
        };
}

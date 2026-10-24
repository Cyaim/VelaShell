using Avalonia.Controls;
using Avalonia.Interactivity;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>
/// 布局模板要问用户的那几句话,以给定窗口为属主弹出:主窗口一份(标题栏下拉、命令面板),
/// 管理窗口一份(它开着时弹框要叠在它上面,叠在主窗口上会被它挡住)。
/// </summary>
/// <param name="owner">属主窗口。</param>
internal sealed class LayoutTemplateDialogs(Window owner) : ILayoutTemplateDialogs
{
    /// <inheritdoc />
    public Task<string?> PromptNameAsync(string title, string initialValue, string? message) =>
        MessageDialog.PromptAsync(owner, title, initialValue, message);

    /// <inheritdoc />
    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger) =>
        MessageDialog.ConfirmAsync(
            owner,
            title,
            message,
            confirmText,
            Strings.Cancel,
            danger ? MessageDialogKind.Warning : MessageDialogKind.Question,
            danger);
}

/// <summary>标题栏「布局模板」按钮的下拉菜单:每个模板一项(点了就打开),末尾是「存为模板」与「管理」。</summary>
internal static class LayoutTemplatesMenu
{
    /// <summary>按当前的模板列表建菜单(每次点按钮现建,列表变了不用另外同步)。</summary>
    /// <param name="templates">布局模板视图模型。</param>
    /// <param name="canSave">当前布局里有没有标签(没有时「存为模板」置灰)。</param>
    /// <returns>菜单。</returns>
    public static MenuFlyout Build(LayoutTemplatesViewModel templates, bool canSave)
    {
        var menu = new MenuFlyout();
        if (templates.Templates.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = Strings.Get("LayoutTemplate_None"), IsEnabled = false });
        }
        foreach (LayoutTemplateItemViewModel template in templates.Templates.ToArray())
        {
            var open = new MenuItem
            {
                Header = LiteralHeader($"{template.Name}  ·  {template.Summary}"),
            };
            open.Click += (_, _) => _ = templates.OpenAsync(template);
            menu.Items.Add(open);
        }
        menu.Items.Add(new Separator());
        var save = new MenuItem { Header = Strings.Get("LayoutTemplate_SaveCurrent"), IsEnabled = canSave };
        save.Click += (_, _) => _ = templates.SaveCurrentAsync();
        menu.Items.Add(save);
        var manage = new MenuItem { Header = Strings.Get("LayoutTemplate_Manage") };
        manage.Click += (_, _) => templates.OpenManager();
        menu.Items.Add(manage);
        return menu;
    }

    /// <summary>
    /// 模板名是用户数据:字符串菜单头会按访问键解析,第一个下划线被吃掉
    /// (与目录收藏菜单 <c>SftpDocumentView.LiteralHeader</c> 同一个坑)。
    /// </summary>
    private static TextBlock LiteralHeader(string text) => new() { Text = text };

    /// <summary>标题栏按钮的点击处理:在按钮下方弹出菜单。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="viewModel">主窗口视图模型。</param>
    /// <param name="e">事件参数。</param>
    public static void ShowFor(object? sender, MainWindowViewModel viewModel, RoutedEventArgs e)
    {
        if (sender is not Control anchor)
        {
            return;
        }
        Build(viewModel.LayoutTemplates, viewModel.Layout.AllDocuments().Any()).ShowAt(anchor);
        e.Handled = true;
    }
}

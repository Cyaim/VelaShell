using Avalonia.Controls;
using Avalonia.Interactivity;
using VelaShell.ViewModels;

namespace VelaShell.Views.Settings;

/// <summary>输出规则设置页:用户的高亮与自动应答规则(列表 + 编辑框 + 测试)。</summary>
/// <remarks>
/// 列表行里的上移 / 下移 / 编辑 / 删除走代码后置而不是绑定,理由与代码片段页相同:
/// 行的 DataContext 是条目自己,要够到页面的视图模型得写 <c>$parent[UserControl].DataContext.…</c>,
/// 页面还没挂进可视树那一刻这条路径是 null,每开一次设置就刷一屏 "Value is null"。
/// </remarks>
public partial class OutputRulesPage : UserControl
{
    /// <summary>创建页面。</summary>
    public OutputRulesPage() => InitializeComponent();

    private OutputRulesViewModel? Rules => (DataContext as SettingsViewModel)?.OutputRules;

    private static OutputRuleRowViewModel? RowOf(object? sender) =>
        (sender as Control)?.DataContext as OutputRuleRowViewModel;

    private void MoveUp_Click(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            Rules?.Move(row, -1);
        }
    }

    private void MoveDown_Click(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            Rules?.Move(row, 1);
        }
    }

    private void EditRule_Click(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            Rules?.BeginEdit(row);
        }
    }

    private void DeleteRule_Click(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            Rules?.Delete(row);
        }
    }
}

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VelaShell.Presentation.ViewModels;

namespace VelaShell.Views;

/// <summary>
/// 资源管理器顶上的过滤框(展开的侧栏与折叠细条的浮层各放一个,绑同一个 <see cref="SessionTreeViewModel" />)。
/// </summary>
public partial class SessionTreeFilterBox : UserControl
{
    /// <summary>初始化过滤框。</summary>
    public SessionTreeFilterBox() => InitializeComponent();

    /// <summary>
    /// Esc 清空过滤(框里没字时不吞,交给外层 —— 浮层里的那一个靠它收起);
    /// 过滤后恰好只剩一条连接时 Enter 直接连它,多于一条不猜用户要哪一条。
    /// </summary>
    private void FilterInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SessionTreeViewModel tree || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Escape when tree.IsFiltering || !string.IsNullOrEmpty(tree.FilterText):
                tree.ClearFilter();
                e.Handled = true;
                break;
            case Key.Enter when tree.SingleFilterMatch is { } only:
                tree.SelectedNode = only;
                tree.RequestConnect(only.Id);
                e.Handled = true;
                break;
        }
    }

    private void ClearFilter_Click(object? sender, RoutedEventArgs e)
    {
        (DataContext as SessionTreeViewModel)?.ClearFilter();
        FilterInput.Focus();
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Styling;
using VelaShell.Controls.Controls;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Services;

namespace VelaShell.Controls;

/// <summary>
/// 图标字段:一个长得像输入框的按钮,点开是一格格图标(<see cref="SessionTabIcons" />)和一键「跟协议」。
/// 连接对话框里给这条连接挑标签页图标用 —— 「像挑颜色一样挑图标」,外观与 <see cref="ColorPickerField" /> 一致。
/// </summary>
/// <remarks>
/// <see cref="Value" /> 是双向绑定的图标 id;null = 跟协议,此时字段上画的是协议的默认图标(<see cref="DefaultIconKey" />)
/// 并写着 <see cref="EmptyText" />,都弱一档。认不出的 id 当作跟协议显示,也不会被写回成别的值。
/// 点一格就写回并收起浮层 —— 挑图标不像调颜色,没有「拖到一半」这回事。
/// </remarks>
public partial class IconPickerField : UserControl
{
    /// <summary>当前图标 id;null 表示跟协议。默认双向绑定。</summary>
    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<IconPickerField, string?>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>跟协议时字段上画的那枚图标的资源键(<c>Icon.&lt;name&gt;</c>);null 时不画。</summary>
    public static readonly StyledProperty<string?> DefaultIconKeyProperty =
        AvaloniaProperty.Register<IconPickerField, string?>(nameof(DefaultIconKey));

    /// <summary>跟协议时字段上的文字,也是浮层底部那一键的文字(如「跟随协议」)。</summary>
    public static readonly StyledProperty<string?> EmptyTextProperty =
        AvaloniaProperty.Register<IconPickerField, string?>(nameof(EmptyText));

    /// <summary>图标浮层(挂在字段按钮上;Flyout 不进名称作用域,没有生成字段)。</summary>
    private Flyout Picker => (Flyout)FieldButton.Flyout!;

    /// <summary>创建图标字段。</summary>
    public IconPickerField()
    {
        InitializeComponent();
        DefaultButton.Click += OnDefaultClick;
        BuildCells();
        Refresh();
    }

    /// <inheritdoc cref="ValueProperty" />
    public string? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <inheritdoc cref="DefaultIconKeyProperty" />
    public string? DefaultIconKey
    {
        get => GetValue(DefaultIconKeyProperty);
        set => SetValue(DefaultIconKeyProperty, value);
    }

    /// <inheritdoc cref="EmptyTextProperty" />
    public string? EmptyText
    {
        get => GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    /// <summary>打开图标浮层(与点字段一个效果;测试与键盘入口用)。</summary>
    public void Open() => Picker.ShowAt(FieldButton);

    /// <summary>浮层里的图标格(测试用)。</summary>
    internal IReadOnlyList<Button> CellsForTest => [.. IconPanel.Children.OfType<Button>()];

    /// <summary>浮层底部「跟协议」那一键(测试用)。</summary>
    internal Button DefaultButtonForTest => DefaultButton;

    /// <summary>字段上显示的文字(测试用)。</summary>
    internal string? ValueTextForTest => ValueText.Text;

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty
            || change.Property == DefaultIconKeyProperty
            || change.Property == EmptyTextProperty)
        {
            Refresh();
        }
    }

    /// <summary>每个可挑的图标一格:悬停提示与无障碍名称是图标的名字(五语)。</summary>
    private void BuildCells()
    {
        IconPanel.Children.Clear();
        foreach (SessionTabIconChoice choice in SessionTabIcons.All)
        {
            var cell = new Button
            {
                Theme = this.FindResource("IconCellTheme") as ControlTheme,
                Tag = choice.Id,
                Classes = { "icon-cell" },
                Content = new LucideIcon
                {
                    Width = 15,
                    Height = 15,
                    Data = ConnectionIcon.GeometryFor(SessionTabIcons.ResourceKey(choice.Id))
                }
            };
            string name = Strings.Get(choice.NameKey);
            ToolTip.SetTip(cell, name);
            Avalonia.Automation.AutomationProperties.SetName(cell, name);
            cell.Click += OnCellClick;
            IconPanel.Children.Add(cell);
        }
    }

    /// <summary>字段上的图标与文字、浮层里当前那一格,都跟着 <see cref="Value" /> 走。</summary>
    private void Refresh()
    {
        SessionTabIconChoice? chosen = SessionTabIcons.Find(Value);
        ValueIcon.Data = chosen is not null
            ? ConnectionIcon.GeometryFor(SessionTabIcons.ResourceKey(chosen.Id))
            : ConnectionIcon.GeometryFor(DefaultIconKey);
        ValueIcon.Classes.Set("empty", chosen is null);
        ValueText.Text = chosen is not null ? Strings.Get(chosen.NameKey) : EmptyText;
        ValueText.Classes.Set("empty", chosen is null);
        DefaultButton.Content = EmptyText;
        DefaultButton.IsEnabled = chosen is not null;
        foreach (Button cell in IconPanel.Children.OfType<Button>())
        {
            cell.Classes.Set("current", chosen is not null && Equals(cell.Tag, chosen.Id));
        }
    }

    private void OnCellClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            Value = id;
            Picker.Hide();
        }
    }

    private void OnDefaultClick(object? sender, RoutedEventArgs e)
    {
        Value = null;
        Picker.Hide();
    }
}

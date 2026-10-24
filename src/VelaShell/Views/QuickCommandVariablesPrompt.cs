using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.Views;

/// <summary>
/// 快捷命令的变量询问框:每个 <c>{{变量}}</c> 一行输入(预填默认值),下方实时预览替换后的命令。
/// 外壳复用 <see cref="MessageDialog.ShowCustomAsync" />,文字与输入框的配色走那里的内容区样式类。
/// </summary>
public static class QuickCommandVariablesPrompt
{
    /// <summary>弹框询问变量值;确认时返回「变量名 → 值」,取消返回 null。</summary>
    /// <param name="owner">属主窗口。</param>
    /// <param name="commandName">命令显示名(标题);没有时用命令正文。</param>
    /// <param name="template">解析好的模板。</param>
    /// <param name="initialValues">
    /// 每一格的初值(宿主按「上一次填的值」给,见 <c>QuickCommandVariableMemory</c>);没给的格子用占位的默认值。
    /// </param>
    public static async Task<IReadOnlyDictionary<string, string>?> ShowAsync(Window owner,
        string? commandName,
        QuickCommandTemplate template,
        IReadOnlyDictionary<string, string>? initialValues = null)
    {
        var values = template.Variables.ToDictionary(
            v => v.Name,
            v => initialValues is not null && initialValues.TryGetValue(v.Name, out string? initial) ? initial : v.DefaultValue,
            StringComparer.Ordinal);
        var preview = new TextBlock
        {
            Classes = { "mono-accent" },
            TextWrapping = TextWrapping.Wrap,
            Text = template.Render(values)
        };
        var fields = new Grid
        {
            ColumnDefinitions = [with("Auto,12,*")],
            RowSpacing = 8
        };
        TextBox? first = null;
        for (int i = 0; i < template.Variables.Count; i++)
        {
            QuickCommandVariable variable = template.Variables[i];
            fields.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock
            {
                Classes = { "mono" },
                Text = variable.Name,
                VerticalAlignment = VerticalAlignment.Center
            };
            var input = new TextBox { Text = values[variable.Name] };
            AutomationProperties.SetName(input, variable.Name);
            input.TextChanged += (_, _) =>
            {
                values[variable.Name] = input.Text ?? string.Empty;
                preview.Text = template.Render(values);
            };
            Grid.SetRow(label, i);
            Grid.SetRow(input, i);
            Grid.SetColumn(input, 2);
            fields.Children.Add(label);
            fields.Children.Add(input);
            first ??= input;
        }
        // 打开即可直接键入第一个值;Enter 落到对话框的默认按钮(发送)上。
        first?.Loaded += (_, _) =>
        {
            first.Focus();
            first.SelectAll();
        };
        var content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                fields,
                new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock { Classes = { "dim" }, Text = Strings.Get("QuickCmd_VariablesPreview") },
                        preview
                    }
                }
            }
        };
        string title = Strings.Format("QuickCmd_VariablesTitle",
            string.IsNullOrWhiteSpace(commandName) ? template.Text : commandName);
        bool confirmed = await MessageDialog.ShowCustomAsync(owner, title, content,
            Strings.Get("QuickCmd_VariablesSend"), kind: MessageDialogKind.Question);
        return confirmed ? values : null;
    }
}

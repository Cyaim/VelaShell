using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VelaShell.ViewModels;

namespace VelaShell.Views.Settings;

/// <summary>快捷键设置页:查看全部快捷键,改键、解绑或恢复可自定义的那些。</summary>
/// <remarks>
/// 行内按钮与录键框都只是把「哪一行」交给 <see cref="SettingsViewModel" />;判定与改动全在视图模型里。
/// </remarks>
public partial class ShortcutsPage : UserControl
{
    /// <summary>初始化快捷键设置页并加载 XAML 组件。</summary>
    public ShortcutsPage() => InitializeComponent();

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    private static ShortcutItem? ItemOf(object? sender) => (sender as Control)?.DataContext as ShortcutItem;

    private void RecordShortcut_Click(object? sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            ViewModel?.BeginShortcutRecording(item);
        }
    }

    private void UnbindShortcut_Click(object? sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            ViewModel?.UnbindShortcut(item);
        }
    }

    private void ResetShortcut_Click(object? sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            ViewModel?.ResetShortcut(item);
        }
    }

    private void ResetAllShortcuts_Click(object? sender, RoutedEventArgs e) => ViewModel?.ResetAllShortcuts();

    private void ConfirmReplace_Click(object? sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            ViewModel?.ConfirmShortcutReplace(item);
        }
    }

    private void CancelRecording_Click(object? sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            ViewModel?.CancelShortcutRecording(item);
        }
    }

    [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members",
        Justification = "XAML 里录键框的 GestureCaptured 事件引用它;参数不是 EventArgs,分析器认不出是事件处理器。")]
    private void Recorder_GestureCaptured(object? sender, KeyGesture gesture)
    {
        if (ItemOf(sender) is { } item)
        {
            ViewModel?.ApplyRecordedShortcut(item, gesture);
        }
    }

    private void Recorder_Cancelled(object? sender, EventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            ViewModel?.CancelShortcutRecording(item);
        }
    }
}

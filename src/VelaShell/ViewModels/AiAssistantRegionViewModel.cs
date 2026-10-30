using Avalonia.Controls;
using ReactiveUI;

namespace VelaShell.ViewModels;

/// <summary>AI 专属区的布局状态。隐藏或折叠只改变布局,内容的生命周期由插件句柄管理。</summary>
public sealed class AiAssistantRegionViewModel : ReactiveObject
{
    /// <summary>聊天内容的最小可拖动宽度。</summary>
    public const double MinimumWidth = 300;
    /// <summary>折叠后保留的展开入口宽度。</summary>
    public const double RailWidth = 32;
    private double _availableWidth = 900;
    private double _overlayWidth = 900;

    /// <summary>插件创建的原始控件,隐藏期间仍保留。</summary>
    public Control? Content { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    /// <summary>插件提供的本地化标题。</summary>
    public string Title { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    /// <summary>是否显示面板或边条。</summary>
    public bool IsVisible { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    /// <summary>是否展开聊天内容。</summary>
    public bool IsExpanded { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    /// <summary>宽度足够时是否固定占位。</summary>
    public bool IsPinned { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = true;
    /// <summary>用户拖动的宽度,临时缩小窗口不会改写它。</summary>
    public double PreferredWidth { get; private set; } = 380;
    /// <summary>取消固定或空间不足时使用自动隐藏浮层。</summary>
    public bool IsAutoHide => !IsPinned || _availableWidth < MinimumWidth;
    /// <summary>是否实际占用工作区宽度;窄窗口自动改用浮层。</summary>
    public bool IsDocked => !IsAutoHide && IsExpanded;
    /// <summary>是否保留右侧展开入口。</summary>
    public bool ShowRail => IsVisible && !IsDocked;
    /// <summary>是否显示插件内容。</summary>
    public bool ShowPane => IsVisible && IsExpanded;
    /// <summary>按当前窗口空间约束后的内容宽度。</summary>
    public double PaneWidth => Math.Max(MinimumWidth, Math.Min(PreferredWidth, IsDocked ? _availableWidth : _overlayWidth));
    /// <summary>主工作区需要让出的宽度。</summary>
    public double ReservedWidth => !IsVisible ? 0 : IsDocked ? PaneWidth + 5 : RailWidth;
    /// <summary>面板含拖拽条、边条的显示宽度。</summary>
    public double DisplayWidth => !IsVisible ? 0 : (IsExpanded ? PaneWidth + 5 : 0) + (ShowRail ? RailWidth : 0);

    internal void Attach(Control content, string title)
    {
        if (Content is not null) throw new InvalidOperationException("The AI region already has content.");
        Content = content;
        Title = title;
        Activate();
    }

    internal void Detach(Control content)
    {
        if (!ReferenceEquals(Content, content)) return;
        Hide();
        Content = null;
        Title = "";
    }

    /// <summary>重新显示并展开已有助手,不改变文档焦点。</summary>
    public void Activate()
    {
        if (Content is null) return;
        IsVisible = true;
        IsExpanded = true;
        RefreshLayout();
    }

    /// <summary>隐藏整个专属区,保留插件会话。</summary>
    public void Hide()
    {
        IsVisible = false;
        IsExpanded = false;
        RefreshLayout();
    }

    /// <summary>临时收起为边条。</summary>
    public void Collapse()
    {
        IsExpanded = false;
        RefreshLayout();
    }

    /// <summary>切换固定与自动隐藏模式。</summary>
    public void TogglePin()
    {
        IsPinned = !IsPinned;
        RefreshLayout();
    }

    /// <summary>从宿主外观设置恢复布局偏好。</summary>
    public void Restore(double width, bool pinned)
    {
        PreferredWidth = double.IsFinite(width) ? Math.Clamp(width, MinimumWidth, 1200) : 380;
        IsPinned = pinned;
        RefreshLayout();
    }

    /// <summary>调整宽度并保留可用的终端空间。</summary>
    public void Resize(double width)
    {
        if (!double.IsFinite(width)) return;
        PreferredWidth = Math.Clamp(width, MinimumWidth, Math.Clamp(IsDocked ? _availableWidth : _overlayWidth, MinimumWidth, 1200));
        RefreshLayout();
    }

    internal void SetAvailableWidth(double dockedWidth, double overlayWidth)
    {
        _availableWidth = Math.Max(0, dockedWidth);
        _overlayWidth = Math.Max(0, overlayWidth);
        RefreshLayout();
    }

    private void RefreshLayout()
    {
        this.RaisePropertyChanged(nameof(IsAutoHide));
        this.RaisePropertyChanged(nameof(IsDocked));
        this.RaisePropertyChanged(nameof(ShowRail));
        this.RaisePropertyChanged(nameof(ShowPane));
        this.RaisePropertyChanged(nameof(PaneWidth));
        this.RaisePropertyChanged(nameof(ReservedWidth));
        this.RaisePropertyChanged(nameof(DisplayWidth));
    }
}

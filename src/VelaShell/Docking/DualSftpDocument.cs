using Avalonia.Controls;
using Avalonia.Media;
using VelaShell.Core.Net;
using VelaShell.Services;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Docking;

/// <summary>「远程 + 远程」双栏文件标签的停靠文档。</summary>
public sealed class DualSftpDocument : SftpDocumentBase
{
    /// <summary>从给定的视图模型初始化双栏远程文档。</summary>
    public DualSftpDocument(DualSftpDocumentViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        // 两个会话 id 拼起来作文档 id:任一单独拿出来都可能与某个单栏文档撞上
        // (同一条会话不会同时属于两个文档,但 id 的唯一性不该依赖这一点)。
        Id = $"{viewModel.SessionIds[0]:N}-{viewModel.SessionIds[1]:N}";
        Title = viewModel.Title;
        IsSessionDocument = true;
    }

    /// <summary>后台视图模型。</summary>
    public DualSftpDocumentViewModel ViewModel { get; }

    /// <inheritdoc />
    public override ISftpDocumentContent Content => ViewModel;

    /// <summary>标签页色条取左栏那台机器的标识色(左栏是资源管理器里先选中的那条)。</summary>
    public override IBrush ConnectionAccentBrush => ConnectionAccent.BrushForProfile(ViewModel.LeftProfile);

    /// <summary>
    /// 协议图标同样取左栏:两栏都是文件浏览器,图标答的只是「这是一个文件标签」。
    /// 左栏是插件协议(S3…)时用插件自报的图标,与单栏文档同一口径。
    /// </summary>
    public override TabIcon? TabIcon => ConnectionIcon.ForSession(ViewModel.LeftProfile, ViewModel.LeftPluginIcon);

    /// <inheritdoc />
    public override string ConnectionTooltip =>
        $"{Describe(ViewModel.LeftProfile)}  ⇄  {Describe(ViewModel.RightProfile)}";

    /// <inheritdoc />
    public override Control CreateView() => new DualSftpDocumentView { DataContext = ViewModel };

    private static string Describe(Core.Models.SessionProfile profile) =>
        HostAddress.FormatTarget(profile.Username, profile.Host, profile.Port);
}

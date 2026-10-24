using ReactiveUI;
using VelaShell.Core.Resources;
using VelaShell.Infrastructure.Plugins;

namespace VelaShell.ViewModels;

/// <summary>
/// 插件管理页「查看日志」打开的只读窗口:显示这个插件自己那份日志的尾部(<see cref="PluginLogFiles" />),
/// 能刷新、能打开所在文件夹。
/// </summary>
/// <remarks>
/// 用户报「插件不工作」时第一件要看的东西。只读尾部(默认 256 KB),不做跟随滚动的实时流:
/// 排障时要的是「刚才那一下发生了什么」,点一下刷新就够,常开的文件监视器不值这个成本。
/// </remarks>
public sealed class PluginLogViewModel : ReactiveObject
{
    private readonly Func<PluginLogTail> _read;
    private readonly int _tailBytes;

    /// <summary>构造。</summary>
    /// <param name="pluginId">插件 id。</param>
    /// <param name="displayName">插件显示名(标题用)。</param>
    /// <param name="read">读尾部(<see cref="PluginManager.ReadLogTail" />);在线程池上调。</param>
    /// <param name="directory">日志所在目录(「打开所在文件夹」);没配日志文件时为 null。</param>
    /// <param name="tailBytes">读的尾部大小(副标题里写明只显示了多少)。</param>
    public PluginLogViewModel(string pluginId, string displayName, Func<PluginLogTail> read, string? directory,
        int tailBytes = PluginLogFiles.DefaultTailBytes)
    {
        ArgumentNullException.ThrowIfNull(read);
        PluginId = pluginId;
        DisplayName = displayName;
        _read = read;
        _tailBytes = tailBytes;
        Directory = directory;
    }

    /// <summary>插件 id。</summary>
    public string PluginId { get; }

    /// <summary>插件显示名。</summary>
    public string DisplayName { get; }

    /// <summary>窗口标题。</summary>
    public string Title => Strings.Format("PluginLog_Title", DisplayName);

    /// <summary>日志所在目录;没配日志文件时为 null(「打开所在文件夹」随之不可用)。</summary>
    public string? Directory { get; }

    /// <summary>能不能打开所在文件夹。</summary>
    public bool CanOpenFolder => Directory is not null;

    /// <summary>日志尾部正文。</summary>
    public string Text
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>还没有任何日志(显示空态说明而不是一块空白)。</summary>
    public bool IsEmpty
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    /// <summary>空态说明:这里会记些什么、还没有的话可能是因为什么。</summary>
    public string EmptyText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>副标题:文件路径,只取了尾部时注明。</summary>
    public string Subtitle
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>正在读。</summary>
    public bool IsLoading
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>重读尾部。读失败时显示为空(<see cref="PluginLogFiles.ReadTail" /> 本身不抛)。</summary>
    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            PluginLogTail tail = await Task.Run(_read).ConfigureAwait(true);
            Text = tail.Text;
            IsEmpty = tail.Text.Length == 0;
            EmptyText = tail.FilePath is null
                ? Strings.Format("PluginLog_NotOnDisk", PluginId)
                : Strings.Get("PluginLog_Empty");
            Subtitle = tail.FilePath is not { } path
                ? string.Empty
                : tail.Truncated
                    ? Strings.Format("PluginLog_TailOf", TransferItemViewModel.FormatBytes(_tailBytes), path)
                    : path;
        }
        finally
        {
            IsLoading = false;
        }
    }
}

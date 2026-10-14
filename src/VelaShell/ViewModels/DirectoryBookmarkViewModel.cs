using ReactiveUI;
using VelaShell.Core.Models;

namespace VelaShell.ViewModels;

/// <summary>双栏文档中一条目录收藏的可绑定表示。</summary>
/// <remarks>创建一条收藏行。</remarks>
public sealed class DirectoryBookmarkViewModel(RemoteDirectoryBookmark bookmark) : ReactiveObject
{
    private RemoteDirectoryBookmark _bookmark = bookmark?.Clone() ?? throw new ArgumentNullException(nameof(bookmark));

    /// <summary>稳定标识。</summary>
    public Guid Id => _bookmark.Id;

    /// <summary>显示名称。</summary>
    public string Name => _bookmark.Name;

    /// <summary>本地目录。</summary>
    public string LocalPath => _bookmark.LocalPath;

    /// <summary>远程目录。</summary>
    public string RemotePath => _bookmark.RemotePath;

    /// <summary>返回用于编辑或应用的独立副本。</summary>
    public RemoteDirectoryBookmark Snapshot => _bookmark.Clone();

    /// <summary>替换行内容,不与配置对象共享引用。</summary>
    internal void Update(RemoteDirectoryBookmark bookmark)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        _bookmark = bookmark.Clone();
        this.RaisePropertyChanged(nameof(Id));
        this.RaisePropertyChanged(nameof(Name));
        this.RaisePropertyChanged(nameof(LocalPath));
        this.RaisePropertyChanged(nameof(RemotePath));
    }
}

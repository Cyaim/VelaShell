using Avalonia.Controls;
using Avalonia.Interactivity;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>独立的 SFTP 文档视图,采用双栏布局。</summary>
public partial class SftpDocumentView : UserControl
{
    /// <summary>初始化 SFTP 文档视图。</summary>
    public SftpDocumentView()
    {
        InitializeComponent();
        FilePaneLayoutMemory.AttachSplit(PaneGrid, PaneSplitter);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SftpDocumentViewModel vm)
            {
                vm.ShowSyncWindow = ShowSyncWindowAsync;
                vm.EditBookmarkDialog = EditBookmarkAsync;
                vm.ShowBookmarkManager = ShowBookmarkManagerAsync;
                vm.ConfirmBookmarkDelete = ConfirmBookmarkDeleteAsync;
            }
        };
    }

    private void OnBookmarkListClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SftpDocumentViewModel vm || sender is not Control anchor)
        {
            return;
        }

        BuildBookmarkMenu(vm).ShowAt(anchor);
    }

    /// <summary>「目录收藏」下拉菜单:每条收藏一组「应用 / 编辑 / 删除」,末尾是「管理收藏」。</summary>
    internal static MenuFlyout BuildBookmarkMenu(SftpDocumentViewModel vm)
    {
        var menu = new MenuFlyout();
        if (vm.Bookmarks.Count == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = Strings.Get("Bookmark_NoItems"),
                IsEnabled = false,
            });
        }
        else
        {
            foreach (DirectoryBookmarkViewModel bookmark in vm.Bookmarks.ToArray())
            {
                var apply = new MenuItem
                {
                    Header = LiteralHeader(Strings.Format("Bookmark_MenuEntry", bookmark.Name, bookmark.LocalPath, bookmark.RemotePath)),
                };
                apply.Click += (_, _) => _ = vm.ApplyBookmarkAsync(bookmark);
                menu.Items.Add(apply);

                var edit = new MenuItem
                {
                    Header = LiteralHeader(Strings.Format("Bookmark_EditMenu", bookmark.Name)),
                };
                edit.Click += (_, _) => _ = vm.EditBookmarkAsync(bookmark);
                menu.Items.Add(edit);

                var delete = new MenuItem
                {
                    Header = LiteralHeader(Strings.Format("Bookmark_DeleteMenu", bookmark.Name)),
                };
                delete.Click += (_, _) => _ = vm.DeleteBookmarkAsync(bookmark);
                menu.Items.Add(delete);
                menu.Items.Add(new Separator());
            }
            // 上一个收藏后的分隔线不需要重复保留。
            if (menu.Items[^1] is Separator)
            {
                menu.Items.RemoveAt(menu.Items.Count - 1);
            }
        }

        menu.Items.Add(new Separator());
        var manage = new MenuItem { Header = Strings.Get("Bookmark_Manage") };
        manage.Click += (_, _) => _ = vm.ShowBookmarkManager?.Invoke() ?? Task.CompletedTask;
        menu.Items.Add(manage);
        return menu;
    }

    /// <summary>
    /// 带用户数据(收藏名、路径)的菜单头用 TextBlock 而不是字符串:字符串头会按访问键解析,
    /// 第一个下划线被吃掉、后一个字母加下划线 —— <c>/srv/my_app</c> 显示成 <c>/srv/myapp</c>
    /// (与 FileBrowserView 面包屑那条注释同一个坑)。
    /// </summary>
    private static TextBlock LiteralHeader(string text) => new() { Text = text };

    private Task ShowBookmarkManagerAsync()
    {
        if (DataContext is not SftpDocumentViewModel vm
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return Task.CompletedTask;
        }
        if (owner.OwnedWindows.FirstOrDefault(window =>
                window is DirectoryBookmarksWindow && ReferenceEquals(window.DataContext, vm)) is { } open)
        {
            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }
            open.Activate();
            return Task.CompletedTask;
        }

        new DirectoryBookmarksWindow { DataContext = vm }.Show(owner);
        return Task.CompletedTask;
    }

    private Task<RemoteDirectoryBookmark?> EditBookmarkAsync(RemoteDirectoryBookmark initial) =>
        TopLevel.GetTopLevel(this) is Window owner
            ? DirectoryBookmarkDialogs.EditAsync(owner, initial)
            : Task.FromResult<RemoteDirectoryBookmark?>(null);

    private Task<bool> ConfirmBookmarkDeleteAsync(string name) =>
        TopLevel.GetTopLevel(this) is Window owner
            ? DirectoryBookmarkDialogs.ConfirmDeleteAsync(owner, name)
            : Task.FromResult(false);

    /// <summary>
    /// 显示同步窗口;这个视图模型的窗口已经开着就把它提到前面。窗口按视图模型找而不是记在视图字段里:
    /// 切标签时文档视图会被重建,字段跟着丢,而窗口与视图模型都还活着。
    /// </summary>
    private Task ShowSyncWindowAsync(DirectorySyncViewModel sync)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return Task.CompletedTask;
        }
        if (owner.OwnedWindows.OfType<DirectorySyncWindow>().FirstOrDefault(w => ReferenceEquals(w.DataContext, sync)) is { } open)
        {
            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }
            open.Activate();
            return Task.CompletedTask;
        }
        new DirectorySyncWindow { DataContext = sync }.Show(owner);
        return Task.CompletedTask;
    }
}

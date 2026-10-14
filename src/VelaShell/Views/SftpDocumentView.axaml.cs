using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
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
                    Header = Strings.Format("Bookmark_MenuEntry", bookmark.Name, bookmark.LocalPath, bookmark.RemotePath),
                };
                apply.Click += (_, _) => _ = vm.ApplyBookmarkAsync(bookmark);
                menu.Items.Add(apply);

                var edit = new MenuItem
                {
                    Header = Strings.Format("Bookmark_EditMenu", bookmark.Name),
                };
                edit.Click += (_, _) => _ = vm.EditBookmarkAsync(bookmark);
                menu.Items.Add(edit);

                var delete = new MenuItem
                {
                    Header = Strings.Format("Bookmark_DeleteMenu", bookmark.Name),
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
        menu.ShowAt(anchor);
    }

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

    private async Task<RemoteDirectoryBookmark?> EditBookmarkAsync(RemoteDirectoryBookmark initial)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return null;
        }

        var name = new TextBox { Text = initial.Name };
        var localPath = new TextBox { Text = initial.LocalPath };
        var remotePath = new TextBox { Text = initial.RemotePath };
        var form = new StackPanel { Spacing = 6 };
        AddEditorField(form, "Bookmark_Name", name);
        AddEditorField(form, "Bookmark_LocalPath", localPath);
        AddEditorField(form, "Bookmark_RemotePath", remotePath);

        bool accepted = await MessageDialog.ShowCustomAsync(
            owner,
            Strings.Get(initial.Id == Guid.Empty ? "Bookmark_AddTitle" : "Bookmark_EditTitle"),
            form,
            Strings.Get("Save"),
            Strings.Get("Cancel"),
            showCancel: true,
            kind: MessageDialogKind.Question);
        return accepted
            ? new RemoteDirectoryBookmark
            {
                Id = initial.Id,
                Name = name.Text ?? string.Empty,
                LocalPath = localPath.Text ?? string.Empty,
                RemotePath = remotePath.Text ?? string.Empty,
            }
            : null;
    }

    private static void AddEditorField(StackPanel form, string labelKey, TextBox field)
    {
        form.Children.Add(new TextBlock { Text = Strings.Get(labelKey), Classes = { "dim" } });
        form.Children.Add(field);
    }

    private Task<bool> ConfirmBookmarkDeleteAsync(string name) =>
        TopLevel.GetTopLevel(this) is Window owner
            ? MessageDialog.ConfirmAsync(
                owner,
                Strings.Get("Bookmark_DeleteTitle"),
                Strings.Format("Bookmark_DeleteMessage", name),
                confirmText: Strings.Get("Delete"),
                cancelText: Strings.Get("Cancel"),
                kind: MessageDialogKind.Warning,
                danger: true)
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

using Avalonia.Controls;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.Views;

/// <summary>
/// 目录收藏的编辑框与删除确认。文档视图和收藏管理窗口各自拿自己的窗口当属主来弹:
/// 文档视图切标签时会被重建、离开窗口树,管理窗口借它弹框就会什么也弹不出来。
/// </summary>
internal static class DirectoryBookmarkDialogs
{
    /// <summary>弹出收藏编辑框;取消时返回 null。</summary>
    /// <param name="owner">属主窗口。</param>
    /// <param name="initial">编辑前的收藏;<see cref="RemoteDirectoryBookmark.Id" /> 为空表示新增。</param>
    public static async Task<RemoteDirectoryBookmark?> EditAsync(Window owner, RemoteDirectoryBookmark initial)
    {
        var name = new TextBox { Text = initial.Name };
        var localPath = new TextBox { Text = initial.LocalPath };
        var remotePath = new TextBox { Text = initial.RemotePath };
        var form = new StackPanel { Spacing = 6 };
        AddField(form, "Bookmark_Name", name);
        AddField(form, "Bookmark_LocalPath", localPath);
        AddField(form, "Bookmark_RemotePath", remotePath);

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

    /// <summary>确认删除一条收藏。</summary>
    /// <param name="owner">属主窗口。</param>
    /// <param name="name">收藏名称。</param>
    public static Task<bool> ConfirmDeleteAsync(Window owner, string name) =>
        MessageDialog.ConfirmAsync(
            owner,
            Strings.Get("Bookmark_DeleteTitle"),
            Strings.Format("Bookmark_DeleteMessage", name),
            confirmText: Strings.Get("Delete"),
            cancelText: Strings.Get("Cancel"),
            kind: MessageDialogKind.Warning,
            danger: true);

    private static void AddField(StackPanel form, string labelKey, TextBox field)
    {
        form.Children.Add(new TextBlock { Text = Strings.Get(labelKey), Classes = { "dim" } });
        form.Children.Add(field);
    }
}

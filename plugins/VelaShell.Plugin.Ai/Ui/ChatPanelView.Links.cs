using Avalonia.Controls;
using Avalonia.Platform.Storage;
using LiveMarkdown.Avalonia;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 回复正文里的链接点开之后怎么办。
/// </summary>
/// <remarks>
/// 模型经常在回答里给出服务器上的文件路径,写成 <c>[名字](/root/xxx.md)</c>。
/// 那既不是网址也不是本机文件,默认什么都不会发生 —— 用户点了没反应,只能自己去终端里找。
/// 现在按目标形状分三条路走:网址交给浏览器,本机路径交给系统默认程序,
/// <b>远端绝对路径经 SFTP 下载下来</b>(存到哪儿由用户挑)。
/// </remarks>
public partial class ChatPanelView
{
    private void OnMarkdownLinkClicked(LinkClickedEventArgs e)
    {
        string href = e?.HRef?.ToString() ?? "";
        if (href.Length == 0)
        {
            return;
        }
        e!.Handled = true;
        if (Uri.TryCreate(href, UriKind.Absolute, out Uri? absolute)
            && absolute.Scheme is "http" or "https" or "mailto")
        {
            _ = LaunchAsync(absolute);
            return;
        }
        // Windows 盘符路径 / UNC:本机文件,交给系统默认程序
        if (Path.IsPathRooted(href) && !href.StartsWith('/'))
        {
            _ = LaunchLocalAsync(href);
            return;
        }
        if (href.StartsWith('/'))
        {
            _ = DownloadRemoteAsync(href);
            return;
        }
        // 相对路径/锚点之类:没有能对上的东西,别装作能打开
        StatusText.Text = _loc.F("LinkUnsupported", href);
    }

    private async Task LaunchAsync(Uri uri)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            {
                await launcher.LaunchUriAsync(uri);
            }
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"Opening '{uri}' failed: {ex.Message}");
            StatusText.Text = $"{_loc["Error"]}: {ex.Message}";
        }
    }

    private async Task LaunchLocalAsync(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                StatusText.Text = _loc.F("LinkMissingLocal", path);
                return;
            }
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            {
                await launcher.LaunchFileInfoAsync(new FileInfo(path));
            }
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"Opening '{path}' failed: {ex.Message}");
            StatusText.Text = $"{_loc["Error"]}: {ex.Message}";
        }
    }

    /// <summary>
    /// 把远端文件拉下来。存到哪儿让用户挑 —— 悄悄落到某个目录里,人一样找不着。
    /// </summary>
    private async Task DownloadRemoteAsync(string remotePath)
    {
        // 做不了就别弹保存框:让用户挑完位置才说宿主不支持,等于白挑一次。
        if (!_context.RemoteFs.SupportsTransferOperations || !_context.RemoteFs.SupportsDownloadOperations)
        {
            StatusText.Text = $"{_loc["Error"]}: {_loc["TransferDownloadUnsupported"]}";
            return;
        }
        if (SelectedSessionId is not { } sessionId)
        {
            StatusText.Text = _loc["NoSession"];
            return;
        }
        try
        {
            RemoteFileEntry? entry = await _context.RemoteFs.StatAsync(sessionId, remotePath);
            if (entry is null)
            {
                StatusText.Text = _loc.F("LinkMissingRemote", remotePath);
                return;
            }
            if (entry.IsDirectory)
            {
                StatusText.Text = _loc.F("LinkIsDirectory", remotePath);
                return;
            }
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            {
                return;
            }
            IStorageFile? target = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = _loc["LinkDownload"],
                SuggestedFileName = Path.GetFileName(remotePath.TrimEnd('/')),
                // 选到已有文件时由系统框先问一句"要替换吗";点了替换,下面才按覆盖去请宿主确认
                ShowOverwritePrompt = true
            });
            if (target?.TryGetLocalPath() is not { } localPath)
            {
                return;
            }
            await StartLinkDownloadAsync(sessionId, remotePath, localPath);
        }
        catch (Exception ex)
        {
            _context.Log.Warn($"Downloading '{remotePath}' failed: {ex.Message}");
            StatusText.Text = $"{_loc["Error"]}: {ex.Message}";
        }
    }

    /// <summary>保存位置已经选好:起后台下载并挂一张进度卡。</summary>
    /// <remarks>
    /// 选的是已有文件,说明用户在系统保存框里已经确认过替换 —— 这时仍按"不覆盖"去请求,
    /// 宿主落盘只会报一句"目标已存在",等于让人白确认一次。覆盖意图如实带给宿主,
    /// 宿主自己的逐任务确认框也就照实写着"替换"。
    /// </remarks>
    private async Task StartLinkDownloadAsync(string sessionId, string remotePath, string localPath)
    {
        FileTransferRunScope scope = BeginLinkTransferScope(sessionId);
        string operationId = FileTransferJobRegistry.StableOperationId(scope.OwnerId, scope.RunId,
            "link:" + remotePath + "->" + localPath);
        RemoteTransferSnapshot snapshot = await scope.StartDownloadAsync(sessionId, remotePath, localPath,
            operationId, overwrite: File.Exists(localPath), CancellationToken.None);
        StatusText.Text = TransferPresentation.Text(_loc, snapshot);
        _ = new LinkTransferBinding(this, scope, snapshot.TransferId, localPath);
    }
}

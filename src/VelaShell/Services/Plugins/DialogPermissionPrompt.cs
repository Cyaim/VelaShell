using VelaShell.Infrastructure.Plugins;
using VelaShell.Views;

namespace VelaShell.Services.Plugins;

/// <summary><see cref="IPluginPermissionPrompt" /> 的实现:弹出 <see cref="PluginPermissionDialog" />。</summary>
internal sealed class DialogPermissionPrompt : IPluginPermissionPrompt
{
    public Task<PluginPermissionDecision> RequestTerminalWriteAsync(string pluginId, string sessionLabel,
        string inputPreview, CancellationToken cancellationToken)
        => PluginPermissionDialog.AskAsync(pluginId, sessionLabel, inputPreview);

    public Task<PluginPermissionDecision> RequestFileUploadAsync(string pluginId, string sessionLabel,
        string localPath, string remotePath, bool overwrite, CancellationToken cancellationToken)
        => PluginPermissionDialog.AskFileUploadAsync(pluginId, sessionLabel, localPath, remotePath, overwrite, cancellationToken);

    public Task<PluginPermissionDecision> RequestFileDownloadAsync(string pluginId, string sessionLabel,
        string remotePath, string localPath, bool overwrite, CancellationToken cancellationToken)
        => PluginPermissionDialog.AskFileDownloadAsync(pluginId, sessionLabel, remotePath, localPath, overwrite, cancellationToken);

    public Task<PluginPermissionDecision> RequestSessionOpenAsync(string pluginId, string target, string reason,
        CancellationToken cancellationToken)
        => PluginPermissionDialog.AskSessionOpenAsync(pluginId, target, reason);
}

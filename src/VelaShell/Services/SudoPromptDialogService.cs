using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using VelaShell.Core.Ssh;
using VelaShell.Views;

namespace VelaShell.Services;

/// <summary>sudo 专用可信入口；无桌面、弹窗失败或关闭均不发送秘密。</summary>
public sealed class SudoPromptDialogService : ISudoPrompt
{
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    /// <inheritdoc />
    public Task<bool> ConfirmAsync(SudoExecutionContext context, string command, CancellationToken cancellationToken) =>
        OnUiAsync(owner => SudoPromptDialog.ConfirmAsync(owner, context, command, cancellationToken), false, cancellationToken);

    /// <inheritdoc />
    public Task<SudoPassword?> RequestPasswordAsync(SudoExecutionContext context, string command, int attempt, CancellationToken cancellationToken) =>
        OnUiAsync(owner => SudoPromptDialog.RequestPasswordAsync(owner, context, command, attempt, cancellationToken), (SudoPassword?)null, cancellationToken);

    private async Task<T> OnUiAsync<T>(Func<Window, Task<T>> show, T refused, CancellationToken cancellationToken)
    {
        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            T answer = await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (cancellationToken.IsCancellationRequested || Application.Current?.ApplicationLifetime
                    is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
                {
                    return refused;
                }
                try { return await show(owner); }
                catch (Exception) when (!cancellationToken.IsCancellationRequested) { return refused; }
            });
            if (cancellationToken.IsCancellationRequested)
            {
                (answer as IDisposable)?.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return answer;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }
}

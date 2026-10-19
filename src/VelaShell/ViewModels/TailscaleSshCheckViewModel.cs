using System.Globalization;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;

namespace VelaShell.ViewModels;

/// <summary>本次 SSH 认证的手动浏览器操作;操作结果不决定认证结果。</summary>
public sealed class TailscaleSshCheckViewModel : ReactiveObject
{
    private readonly Func<Uri, Task<bool>> _openBrowser;
    private readonly Func<string, Task> _copyLink;
    private readonly Action _cancel;
    private Uri _verificationUri;
    private string _errorMessage = string.Empty;
    private bool _isCompleted;
    private int _uriRevision;

    /// <summary>创建单次认证跳的等待状态;不会自动打开链接。</summary>
    /// <param name="target">当前认证跳。</param>
    /// <param name="verificationUri">当前验证链接。</param>
    /// <param name="openBrowser">打开浏览器。</param>
    /// <param name="copyLink">复制链接。</param>
    /// <param name="cancel">取消本次认证。</param>
    /// <param name="expiresAt">本次等待的截止时刻;到点仍未批准,本次连接按超时结束。</param>
    public TailscaleSshCheckViewModel(string target, Uri verificationUri,
        Func<Uri, Task<bool>> openBrowser, Func<string, Task> copyLink, Action cancel,
        DateTimeOffset? expiresAt = null)
    {
        Target = target;
        _verificationUri = verificationUri;
        _openBrowser = openBrowser;
        _copyLink = copyLink;
        _cancel = cancel;
        // 服务端会等很久(SSO + 多因素也来得及),写明最长等多久、到几点,而不是让人对着窗口猜。
        DeadlineText = expiresAt is { } end
            ? Strings.Format("Ssh_TailscaleCheckDeadline",
                Math.Max(1, (int)Math.Ceiling((end - DateTimeOffset.Now).TotalMinutes)),
                end.ToLocalTime().ToString("t", CultureInfo.CurrentCulture))
            : string.Empty;
        IObservable<bool> active = this.WhenAnyValue(x => x.IsCompleted).Select(completed => !completed);
        OpenBrowserCommand = ReactiveCommand.CreateFromTask(OpenBrowserAsync, active);
        CopyLinkCommand = ReactiveCommand.CreateFromTask(CopyLinkAsync, active);
        CancelCommand = ReactiveCommand.Create(Cancel, active);
    }

    /// <summary>当前认证跳,不是最终会话配置的身份。</summary>
    public string Target { get; }

    /// <summary>最长等多久、到几点;没有截止时刻时为空串。</summary>
    public string DeadlineText { get; }

    /// <summary>仅供本窗口展示及用户操作的当前链接,不得持久化。</summary>
    public Uri VerificationUri
    {
        get => _verificationUri;
        private set => this.RaiseAndSetIfChanged(ref _verificationUri, value);
    }

    /// <summary>不包含链接或平台异常的本地化操作错误。</summary>
    public string ErrorMessage
    {
        get => _errorMessage;
        private set => this.RaiseAndSetIfChanged(ref _errorMessage, value);
    }

    /// <summary>结束后旧命令失效。</summary>
    public bool IsCompleted
    {
        get => _isCompleted;
        private set => this.RaiseAndSetIfChanged(ref _isCompleted, value);
    }

    /// <summary>由用户主动打开当前链接。</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenBrowserCommand { get; }

    /// <summary>由用户主动复制当前链接。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CopyLinkCommand { get; }

    /// <summary>只取消本次认证,最多执行一次。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }

    /// <summary>同一认证跳更新链接;旧异步操作的反馈随之失效。</summary>
    public void UpdateVerificationUri(Uri uri)
    {
        if (IsCompleted || VerificationUri == uri)
        {
            return;
        }
        _uriRevision++;
        VerificationUri = uri;
        ErrorMessage = string.Empty;
    }

    /// <summary>程序性结束,不会调用用户取消动作。</summary>
    public void Complete()
    {
        IsCompleted = true;
        ErrorMessage = string.Empty;
    }

    private void Cancel()
    {
        if (IsCompleted)
        {
            return;
        }
        Complete();
        _cancel();
    }

    private async Task OpenBrowserAsync()
    {
        if (IsCompleted)
        {
            return;
        }
        Uri uri = VerificationUri;
        int revision = _uriRevision;
        bool opened = false;
        try
        {
            if (TailscaleSshCheckUri.IsAllowed(uri))
            {
                opened = await _openBrowser(uri);
            }
        }
        catch (Exception)
        {
            // 平台异常可能包含验证 token,只向此窗口报告固定的安全文案。
        }
        if (!IsCompleted && revision == _uriRevision)
        {
            ErrorMessage = opened ? string.Empty : Strings.Get("Ssh_TailscaleCheckOpenFailed");
        }
    }

    private async Task CopyLinkAsync()
    {
        if (IsCompleted)
        {
            return;
        }
        Uri uri = VerificationUri;
        int revision = _uriRevision;
        bool copied = false;
        try
        {
            if (TailscaleSshCheckUri.IsAllowed(uri))
            {
                await _copyLink(uri.AbsoluteUri);
                copied = true;
            }
        }
        catch (Exception)
        {
            // 不记录可能含验证 token 的剪贴板异常。
        }
        if (!IsCompleted && revision == _uriRevision)
        {
            ErrorMessage = copied ? string.Empty : Strings.Get("Ssh_TailscaleCheckCopyFailed");
        }
    }
}

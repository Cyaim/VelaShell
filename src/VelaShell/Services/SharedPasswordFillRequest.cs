using System.Security.Cryptography;
using System.Text;
using Avalonia.Threading;
using VelaShell.Core.Data;
using VelaShell.Terminal;
using VelaShell.Terminal.Rendering;
using VelaShell.ViewModels;

namespace VelaShell.Services;

internal enum PasswordFillResult { Queued, Expired, Unavailable, InvalidPassword, ReadFailed }

/// <summary>一次用户主动发起的填入请求;失效后不能因切回原标签而恢复。</summary>
internal sealed class SharedPasswordFillRequest : IDisposable
{
    private readonly TerminalTabViewModel _tab;
    private readonly VelaTerminalControl _control;
    private readonly SshTerminalBridge? _bridge;
    private readonly long _connectionGeneration;
    private readonly long _inputGeneration;
    private readonly Func<bool> _isActive;
    private readonly (int Row, int Col, int Screen, int Encoding, string Line) _prompt;
    private bool _expired;
    private bool _sending;

    internal SharedPasswordFillRequest(TerminalTabViewModel tab, Func<bool> isActive)
    {
        Dispatcher.UIThread.VerifyAccess();
        _tab = tab;
        _control = (VelaTerminalControl)tab.TerminalEmulator.Control;
        _bridge = tab.Bridge;
        _inputGeneration = _bridge?.InputGeneration ?? 0;
        _connectionGeneration = tab.ConnectionGeneration;
        _isActive = isActive;
        _prompt = CapturePrompt();
        _control.TypedInput += OnInput;
        _control.OutputUpdated += OnOutput;
    }

    private (int, int, int, int, string) CapturePrompt() =>
        (_control.CursorRow, _control.CursorCol, _control.ScreenGeneration, _control.SessionEncoding.CodePage,
            _control.GetBufferLine(_control.CursorRow));

    private void OnInput(byte[] _) => Invalidate();
    private void OnOutput()
    {
        if (CapturePrompt() != _prompt)
        {
            Invalidate();
        }
    }

    internal void Invalidate() => _expired = true;

    internal bool IsValid => !_expired && _tab.IsConnected && _tab.ShellStream is { CanWrite: true } && _bridge is not null
        && ReferenceEquals(_bridge, _tab.Bridge) && _connectionGeneration == _tab.ConnectionGeneration
        && _bridge.InputGeneration == _inputGeneration
        && _isActive() && CapturePrompt() == _prompt;

    internal async Task<PasswordFillResult> FillAsync(ISharedCredentialRepository repository, Guid id)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_sending || !IsValid)
        {
            return PasswordFillResult.Expired;
        }
        _sending = true;
        try
        {
            string? password;
            try
            {
                password = await repository.GetPasswordAsync(id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return PasswordFillResult.ReadFailed;
            }
            // 无 await 地复核并入队,不能把解密期间切换到的新会话当成发送目标。
            if (!IsValid)
            {
                return PasswordFillResult.Expired;
            }
            if (string.IsNullOrEmpty(password))
            {
                return PasswordFillResult.Unavailable;
            }
            if (password.Any(char.IsControl))
            {
                return PasswordFillResult.InvalidPassword;
            }
            byte[] bytes;
            try
            {
                Encoding encoding = (Encoding)_control.SessionEncoding.Clone();
                encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
                bytes = encoding.GetBytes(password);
            }
            catch (EncoderFallbackException)
            {
                return PasswordFillResult.InvalidPassword;
            }
            if (!IsValid)
            {
                CryptographicOperations.ZeroMemory(bytes);
                return PasswordFillResult.Expired;
            }
            // 不经 TypedInput:密码不得进入广播、补全跟踪、输入诊断或剪贴板。桥持有 bytes,不能立即擦除。
            if (!_bridge!.TrySendRaw(bytes, _inputGeneration))
            {
                CryptographicOperations.ZeroMemory(bytes);
                _expired = true;
                return PasswordFillResult.Expired;
            }
            _expired = true;
            return PasswordFillResult.Queued;
        }
        finally
        {
            _sending = false;
        }
    }

    public void Dispose()
    {
        _expired = true;
        _control.TypedInput -= OnInput;
        _control.OutputUpdated -= OnOutput;
    }
}

using System.Security.Cryptography;
using System.Text;

namespace VelaShell.Core.Ssh;

/// <summary>宿主可信来源及真实连接目标；不由插件或远端提供。</summary>
public sealed record SudoExecutionContext(string SourcePluginId, string Target);

/// <summary>受控 sudo 执行的无秘密状态。</summary>
public enum SudoExecutionState
{
    /// <summary>等待宿主可信审批。</summary>
    AwaitingApproval,
    /// <summary>等待宿主认证输入。</summary>
    AwaitingAuthentication,
    /// <summary>远端 root 命令已启动。</summary>
    Running
}

/// <summary>受控 sudo 执行的结果，不把不确定结果伪装为进程退出。</summary>
public enum SudoExecutionOutcome
{
    /// <summary>执行结果不确定，禁止自动重跑。</summary>
    Unknown,
    /// <summary>收到真实退出码。</summary>
    Exited,
    /// <summary>审批或密码输入取消。</summary>
    AuthenticationCancelled,
    /// <summary>认证失败或认证期限到期。</summary>
    AuthenticationFailed,
    /// <summary>PTY、终端模式、认证方法或可信 UI 不受支持。</summary>
    NotSupported
}

/// <summary>PTY stdout/stderr 合并的原始 UTF-8 片段；认证阶段内容永不交付。</summary>
public sealed record SudoExecutionEvent(SudoExecutionState? State = null, string? Text = null);

/// <summary>受控 sudo 执行的结果。</summary>
public sealed record SudoExecutionResult(SudoExecutionOutcome Outcome, int? ExitCode = null, long OutputChunks = 0);

/// <summary>只由宿主 UI 实现的 sudo 审批及密码入口；不属于插件能力。</summary>
public interface ISudoPrompt
{
    /// <summary>每次完整命令以 root 执行之前确认，包括 NOPASSWD。</summary>
    Task<bool> ConfirmAsync(SudoExecutionContext context, string command, CancellationToken cancellationToken);

    /// <summary>取得本次认证的可清零 UTF-8 密码；null 表示拒绝/无 UI。</summary>
    Task<SudoPassword?> RequestPasswordAsync(SudoExecutionContext context, string command, int attempt, CancellationToken cancellationToken);
}

/// <summary>短命的可清零 UTF-8 密码。禁止换行、NUL、终端控制字符及不合法 UTF-16。</summary>
public sealed class SudoPassword : IDisposable
{
    private byte[]? _bytes;

    /// <summary>复制并编码输入；此类型不保存字符串且不提供可泄漏密码的 ToString。</summary>
    public SudoPassword(ReadOnlySpan<char> password)
    {
        if (password.Length > 4096)
        {
            throw new ArgumentException("Password exceeds the input limit.", nameof(password));
        }
        foreach (char character in password)
        {
            if (char.IsControl(character) || character is '\u2028' or '\u2029')
            {
                throw new ArgumentException("Password contains unsupported control characters.", nameof(password));
            }
        }
        UTF8Encoding encoding = new(false, true);
        _bytes = new byte[encoding.GetByteCount(password)];
        encoding.GetBytes(password, _bytes);
    }

    /// <summary>认证发送期间借用；不允许写入日志、文件或其它通道。</summary>
    public ReadOnlyMemory<byte> Utf8 => _bytes ?? throw new ObjectDisposedException(nameof(SudoPassword));

    /// <summary>清零可控缓冲；重复释放安全。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _bytes, null) is { } bytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

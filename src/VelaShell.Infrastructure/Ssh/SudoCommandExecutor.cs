using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using VelaShell.Core.Ssh;
using VelaShell.Ssh.Channels;
using VelaShell.Ssh.Diagnostics;
using VelaShell.Ssh.Session;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 一次受控 sudo 完成认证与执行，不先 sudo -v 再另开通道。
/// 密码仍交给受信的远端；随机标记不能防御已经被攻陷的服务器。
/// </summary>
internal static class SudoCommandExecutor
{
    internal static readonly TimeSpan AuthenticationTimeout = TimeSpan.FromMinutes(2);
    private const int MaximumAuthenticationCharacters = 64 * 1024;

    internal static string Quote(string text) => "'" + text.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    /// <summary>
    /// 交给登录 shell 的命令行：外层只含各家 shell 单引号里都按字面处理的字符，
    /// 真正的 POSIX 脚本（<see cref="BuildScript" />）按 UTF-8 字节编成 <c>printf %b</c> 的 <c>\0ooo</c> 转义，
    /// 由 <c>/bin/sh</c> 自己还原后 eval。
    /// </summary>
    /// <remarks>
    /// sshd 用用户的登录 shell 执行命令行。fish / csh 不认 POSIX 脚本（<c>modes=…</c>、<c>case…esac</c> 直接语法错误，
    /// 被当成认证失败，密码框都不弹）；而把脚本单引号包一层交给 <c>/bin/sh</c> 也不够：fish 的单引号里
    /// <c>\\</c> 与 <c>\'</c> 仍是转义，会悄悄改掉已批准的 root 命令；csh 的单引号里不许换行，也挡不住 <c>!</c>。
    /// 编码之后不管登录 shell 是哪家，读到的都是同一个 <c>/bin/sh</c> 调用，内层两级 <see cref="Quote" /> 原样不动。
    /// </remarks>
    internal static string BuildCommand(string command, string ready, string passwordPrompt, string started) =>
        "exec /bin/sh -c 'eval \"$(printf %b \"" + EncodeForPrintf(BuildScript(command, ready, passwordPrompt, started)) + "\")\"'";

    /// <summary>
    /// 字母、数字与 <c> -_./,:=+@</c> 原样保留，其余字节一律写成 <c>\0ooo</c>。
    /// 保留的这几个在 sh / bash / zsh / fish / csh 的单引号里、在 sh 的双引号里、在 <c>printf %b</c> 的实参里都没有特殊含义。
    /// </summary>
    internal static string EncodeForPrintf(string script)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(script);
        StringBuilder encoded = new(bytes.Length * 2);
        foreach (byte value in bytes)
        {
            if (char.IsAsciiLetterOrDigit((char)value) || " -_./,:=+@".Contains((char)value, StringComparison.Ordinal))
            {
                encoded.Append((char)value);
            }
            else
            {
                encoded.Append("\\0").Append(Convert.ToString(value, 8).PadLeft(3, '0'));
            }
        }
        return encoded.ToString();
    }

    /// <summary>由 <c>/bin/sh</c> 执行的受控脚本：回读终端状态、打印就绪标记、再 <c>exec sudo</c> 进 root shell。</summary>
    internal static string BuildScript(string command, string ready, string passwordPrompt, string started)
    {
        // 询问 sudo 前回读实际终端状态。即使 raw 模式也拒绝控制字符，
        // 因为 sudo/PAM 读密码时可能临时修改终端模式。
        // sudo 拒绝时可能回显整个 root argv，原始启动标记只能由真正启动的 shell 生成。
        string encodedStart = started.Replace("\u001e", "\\036", StringComparison.Ordinal).Replace("\u001f", "\\037", StringComparison.Ordinal);
        string root = "printf '%b' " + Quote(encodedStart) + " || exit 125; exec /bin/sh -c " + Quote(command) + " </dev/null";
        return "stty raw -echo || exit 125; modes=\" $(stty -a) \" || exit 125; "
            + "case \"$modes\" in *' -echo '*) ;; *) exit 125;; esac; "
            + "case \"$modes\" in *' -icanon '*) ;; *) exit 125;; esac; "
            + "printf '%s' " + Quote(ready) + " || exit 125; "
            + "exec sudo -S -u root -p " + Quote(passwordPrompt) + " -- /bin/sh -c " + Quote(root);
    }

    internal static async Task<SudoExecutionResult> ExecuteAsync(SshConnection connection, string command,
        SudoExecutionContext context, ISudoPrompt prompt, Func<SudoExecutionEvent, ValueTask> onEvent, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(onEvent);
        if (command.Contains('\0', StringComparison.Ordinal) || string.IsNullOrWhiteSpace(context.SourcePluginId)
            || string.IsNullOrWhiteSpace(context.Target))
        {
            return new(SudoExecutionOutcome.NotSupported);
        }
        await onEvent(new(SudoExecutionState.AwaitingApproval)).ConfigureAwait(false);
        if (!await prompt.ConfirmAsync(context, command, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(SudoExecutionOutcome.AuthenticationCancelled);
        }

        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        string ready = "\u001eVELA_READY_" + nonce + "\u001f";
        string passwordPrompt = "\u001eVELA_AUTH_" + nonce + "\u001f";
        string started = "\u001eVELA_START_" + nonce + "\u001f";
        IAsyncDisposable lease = connection.SuppressSensitivePayload();
        SshShell? shell = null;
        bool running = false;
        long chunks = 0;
        using var authentication = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connection.Disconnected);
        authentication.CancelAfter(AuthenticationTimeout);
        using var passwordLifetime = CancellationTokenSource.CreateLinkedTokenSource(authentication.Token);
        Task<SudoPassword?>? pendingPassword = null;
        Task<string?>? pendingRead = null;
        try
        {
            shell = await connection.OpenShellAsync(new SshShellOptions
            {
                TerminalType = "dumb",
                Modes = SshTerminalModes.Empty.With(SshTerminalModeOpcode.Echo, 0)
                    .With(SshTerminalModeOpcode.CanonicalInput, 0)
                    .With(SshTerminalModeOpcode.MapCrToNlOnInput, 0),
                Command = BuildCommand(command, ready, passwordPrompt, started),
            }, authentication.Token).ConfigureAwait(false);

            // 非法字节按 U+FFFD 替换，与普通执行一致：root 命令输出 GBK 日志、二进制或非 UTF-8 文件名很常见，
            // 严格解码会把已在跑的命令判成 Unknown、还因关通道给它送去 SIGHUP。
            // 三个标记全是 ASCII，按最大子段替换既造不出、也吞不掉它们，认证阶段同样不需要严格解码。
            Decoder decoder = new UTF8Encoding(false, false).GetDecoder();
            string authenticationText = string.Empty;
            bool echoConfirmed = false;
            int attempts = 0;
            while (true)
            {
                CancellationToken readToken = running ? cancellationToken : authentication.Token;
                pendingRead ??= ReadChunkAsync(shell.StandardOutput, decoder, readToken);
                if (!running && pendingPassword is not null)
                {
                    // 弹窗期间继续读：EOF、断线或启动标记都会撤销尚未发送的答案。
                    await Task.WhenAny(pendingRead, pendingPassword).ConfigureAwait(false);
                    if (!pendingRead.IsCompleted && pendingPassword.IsCompleted)
                    {
                        using SudoPassword? password = await pendingPassword.ConfigureAwait(false);
                        pendingPassword = null;
                        authentication.Token.ThrowIfCancellationRequested();
                        if (password is null)
                        {
                            return new(SudoExecutionOutcome.AuthenticationCancelled);
                        }
                        // 此边界上已经到达的远端内容优先于密码发送，宁可失败也不误注入。
                        if (pendingRead.IsCompleted || shell.Channel.Closed.IsCancellationRequested)
                        {
                            return new(SudoExecutionOutcome.AuthenticationFailed);
                        }
                        await shell.StandardInput.WriteAsync(password.Utf8, authentication.Token).ConfigureAwait(false);
                        await shell.StandardInput.WriteAsync("\n"u8.ToArray(), authentication.Token).ConfigureAwait(false);
                        continue;
                    }
                }

                string? text = await pendingRead.ConfigureAwait(false);
                pendingRead = null;
                if (text is null)
                {
                    await passwordLifetime.CancelAsync().ConfigureAwait(false);
                    SshExitStatus exit = await shell.WaitAsync(running ? cancellationToken : authentication.Token).ConfigureAwait(false);
                    return running ? new(exit.ExitCode.HasValue ? SudoExecutionOutcome.Exited : SudoExecutionOutcome.Unknown, exit.ExitCode, chunks)
                        : new(exit.ExitCode is null or 0 ? SudoExecutionOutcome.Unknown : SudoExecutionOutcome.AuthenticationFailed);
                }
                if (running)
                {
                    if (text.Length > 0)
                    {
                        await onEvent(new(Text: text)).ConfigureAwait(false);
                        chunks++;
                    }
                    continue;
                }
                authenticationText += text;
                if (authenticationText.Length > MaximumAuthenticationCharacters)
                {
                    return new(SudoExecutionOutcome.NotSupported);
                }
                if (!echoConfirmed)
                {
                    int position = authenticationText.IndexOf(ready, StringComparison.Ordinal);
                    if (position < 0)
                    {
                        continue;
                    }
                    echoConfirmed = true;
                    authenticationText = authenticationText[(position + ready.Length)..];
                }
                int start = authenticationText.IndexOf(started, StringComparison.Ordinal);
                if (start >= 0)
                {
                    running = true; // 不可逆：启动后的输出永远不能重新打开认证入口。
                    // 先报 Running 再做别的等待：其后撞上死线时，调用方必须知道 root 命令可能已经在跑
                    // （记 Unknown），不能当成没启动（TimedOut）去重试一遍。
                    await onEvent(new(SudoExecutionState.Running)).ConfigureAwait(false);
                    authentication.CancelAfter(Timeout.InfiniteTimeSpan);
                    await passwordLifetime.CancelAsync().ConfigureAwait(false);
                    await shell.CompleteStandardInputAsync(cancellationToken).ConfigureAwait(false);
                    string tail = authenticationText[(start + started.Length)..];
                    authenticationText = string.Empty;
                    if (tail.Length > 0)
                    {
                        await onEvent(new(Text: tail)).ConfigureAwait(false);
                        chunks++;
                    }
                    continue;
                }
                int request = authenticationText.IndexOf(passwordPrompt, StringComparison.Ordinal);
                if (request >= 0)
                {
                    if (pendingPassword is not null || ++attempts > 3)
                    {
                        return new(SudoExecutionOutcome.AuthenticationFailed);
                    }
                    authenticationText = authenticationText[(request + passwordPrompt.Length)..];
                    await onEvent(new(SudoExecutionState.AwaitingAuthentication)).ConfigureAwait(false);
                    pendingPassword = prompt.RequestPasswordAsync(context, command, attempts, passwordLifetime.Token);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(running || connection.Disconnected.IsCancellationRequested ? SudoExecutionOutcome.Unknown : SudoExecutionOutcome.AuthenticationFailed);
        }
        catch (SshChannelException) when (!running)
        {
            return new(SudoExecutionOutcome.NotSupported);
        }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException)
        {
            return new(running ? SudoExecutionOutcome.Unknown : SudoExecutionOutcome.NotSupported);
        }
        finally
        {
            await passwordLifetime.CancelAsync().ConfigureAwait(false);
            await authentication.CancelAsync().ConfigureAwait(false);
            if (pendingPassword is not null)
            {
                try { (await pendingPassword.ConfigureAwait(false))?.Dispose(); } catch (Exception) { }
            }
            if (shell is not null)
            {
                if (cancellationToken.IsCancellationRequested || !running)
                {
                    try { await shell.SendSignalAsync("TERM", CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
                }
                await shell.DisposeAsync().ConfigureAwait(false);
                // Dispose 有时限且只保证本端收尾。受观察的排空任务持有租约，
                // 直到对端 CLOSE 与发送泵结束，或整条连接停止诊断；本地超时不能解除保护。
                _ = ReleaseAfterDrainAsync(shell, connection, lease);
            }
            else
            {
                // 开通道失败时，OpenShellAsync 可能没有交出仍在关闭的通道。
                // 保守持有至连接结束，不能为恢复诊断可见性而暴露迟到认证内容。
                _ = ReleaseAfterDisconnectAsync(connection, lease);
            }
            if (pendingRead is not null)
            {
                try { await pendingRead.ConfigureAwait(false); } catch (Exception) { }
            }
        }
    }

    private static async Task ReleaseAfterDrainAsync(SshShell shell, SshConnection connection, IAsyncDisposable lease)
    {
        try
        {
            Task drain = shell.Channel.WaitForSensitivePayloadDrainAsync().AsTask();
            await Task.WhenAny(drain, connection.Completion).ConfigureAwait(false);
            if (drain.IsCompleted)
            {
                await drain.ConfigureAwait(false);
            }
            await lease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 排空失败不能证明安全，继续持有租约直到连接停止诊断。
            await ReleaseAfterDisconnectAsync(connection, lease).ConfigureAwait(false);
        }
    }

    private static async Task ReleaseAfterDisconnectAsync(SshConnection connection, IAsyncDisposable lease)
    {
        await connection.Completion.ConfigureAwait(false);
        await lease.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task<string?> ReadChunkAsync(PipeReader reader, Decoder decoder, CancellationToken cancellationToken)
    {
        ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        ReadOnlySequence<byte> bytes = result.Buffer;
        ReadOnlySequence<byte> part = bytes.Slice(0, Math.Min(bytes.Length, 8192));
        bool completed = result.IsCompleted && part.Length == bytes.Length;
        byte[] data = part.ToArray();
        char[] characters = new char[Encoding.UTF8.GetMaxCharCount(data.Length)];
        try
        {
            int count = decoder.GetChars(data.AsSpan(), characters.AsSpan(), completed);
            reader.AdvanceTo(part.End, part.End);
            return count == 0 && completed ? null : new string(characters, 0, count);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
            Array.Clear(characters);
        }
    }
}

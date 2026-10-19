using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using VelaShell.Core.Resources;

namespace VelaShell.Infrastructure.Diagnostics;

/// <summary>诊断包里一个插件的一行:id、版本、状态与运行方式;加载失败时带上原因。</summary>
public sealed record DiagnosticPluginInfo(string Id, string? Version, string State, string? HostMode, bool IsDevelopment, string? Error);

/// <summary>写一个诊断包要的材料。</summary>
/// <param name="AppVersion">应用版本(关于页显示的那个)。</param>
/// <param name="LogsDirectory">日志目录;没有时包里只有概况。</param>
/// <param name="Plugins">已发现的插件。</param>
/// <param name="Redactor">脱敏器;包里每个文件都过一遍。</param>
public sealed record DiagnosticBundleRequest(
    string AppVersion,
    string? LogsDirectory,
    IReadOnlyList<DiagnosticPluginInfo> Plugins,
    DiagnosticRedactor Redactor);

/// <summary>写好的诊断包:收进去几份日志,各类脱敏换了多少处。</summary>
public sealed record DiagnosticBundleResult(int LogFiles, IReadOnlyDictionary<string, int> Redactions);

/// <summary>
/// 一键导出诊断包:日志、崩溃记录、版本与平台、已装插件,脱敏后打成一个 zip,只落在用户选的位置。
/// </summary>
/// <remarks>
/// <para>
/// 报障时用户原先要被教着去翻 <c>~/.velashell/logs</c>,还得自己判断哪几份有用、里面有没有不想给人看的东西。
/// 这里一次收齐,先脱敏再写(见 <see cref="DiagnosticRedactor" />),不上传、不外呼 —— 发不发、发给谁由用户决定。
/// </para>
/// <para>
/// 设置与连接配置<b>不</b>收:那里有代理口令、密钥路径、共享凭据,脱敏规则兜不全。
/// </para>
/// </remarks>
public static class DiagnosticBundle
{
    /// <summary>收进包的日志原文总量上限;从最新的往旧的收,超了就停。</summary>
    internal const long MaxLogBytes = 32L * 1024 * 1024;

    /// <summary>写诊断包。已有同名文件时覆盖。</summary>
    /// <param name="zipPath">包的落盘位置。</param>
    /// <param name="request">材料。</param>
    /// <param name="cancellationToken">取消。</param>
    public static async Task<DiagnosticBundleResult> WriteAsync(string zipPath, DiagnosticBundleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentNullException.ThrowIfNull(request);

        // 先写到旁边的临时文件、写完再换名:中途失败或取消不在用户选的位置留下半个包
        string temporary = zipPath + ".partial";
        int logFiles = 0;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (FileInfo log in LogFiles(request.LogsDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? text = await ReadSharedAsync(log, cancellationToken).ConfigureAwait(false);
                    if (text is null) continue;
                    await AddTextAsync(zip, "logs/" + log.Name, request.Redactor.Redact(text), cancellationToken).ConfigureAwait(false);
                    logFiles++;
                }
                await AddTextAsync(zip, "summary.txt", request.Redactor.Redact(Summary(request)), cancellationToken).ConfigureAwait(false);
                // 说明最后写:要列出前面各个文件一共换了多少处
                await AddTextAsync(zip, "README.txt", ReadMe(request.Redactor.Counts, logFiles), cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, zipPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
        return new DiagnosticBundleResult(logFiles, new Dictionary<string, int>(request.Redactor.Counts));
    }

    /// <summary>日志目录顶层的 <c>*.log</c> 与崩溃记录,新的在前,总量封顶。</summary>
    internal static IEnumerable<FileInfo> LogFiles(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) yield break;
        FileInfo[] files;
        try
        {
            files =
            [
                .. new DirectoryInfo(directory).EnumerateFiles()
                    .Where(file => file.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase)
                        || file.Name.StartsWith("crash-", StringComparison.OrdinalIgnoreCase)
                           && file.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        long total = 0;
        foreach (FileInfo file in files)
        {
            if (total + file.Length > MaxLogBytes) yield break;
            total += file.Length;
            yield return file;
        }
    }

    private static string Summary(DiagnosticBundleRequest request)
    {
        var text = new StringBuilder();
        void Line(string key, object? value) => text.Append(CultureInfo.InvariantCulture, $"{key}: {value}\n");
        Line("VelaShell", request.AppVersion);
        Line("Generated", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        Line("OS", RuntimeInformation.OSDescription);
        Line("OS architecture", RuntimeInformation.OSArchitecture);
        Line("Process architecture", RuntimeInformation.ProcessArchitecture);
        Line(".NET", RuntimeInformation.FrameworkDescription);
        Line("UI culture", CultureInfo.CurrentUICulture.Name);
        Line("Process uptime", (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).ToString(@"d\.hh\:mm\:ss", CultureInfo.InvariantCulture));
        text.Append('\n').Append(CultureInfo.InvariantCulture, $"Plugins ({request.Plugins.Count}):\n");
        foreach (DiagnosticPluginInfo plugin in request.Plugins.OrderBy(plugin => plugin.Id, StringComparer.OrdinalIgnoreCase))
        {
            text.Append(CultureInfo.InvariantCulture, $"  {plugin.Id} {plugin.Version ?? "?"} state={plugin.State}");
            if (!string.IsNullOrEmpty(plugin.HostMode)) text.Append(" mode=").Append(plugin.HostMode);
            if (plugin.IsDevelopment) text.Append(" dev");
            if (!string.IsNullOrWhiteSpace(plugin.Error)) text.Append(" error=").Append(plugin.Error.ReplaceLineEndings(" "));
            text.Append('\n');
        }
        return text.ToString();
    }

    private static string ReadMe(IReadOnlyDictionary<string, int> counts, int logFiles)
    {
        var text = new StringBuilder();
        text.Append(Strings.Get("DiagBundle_ReadMeTitle")).Append("\n\n");
        text.Append(Strings.Get("DiagBundle_ReadMeLocalOnly")).Append("\n\n");
        text.Append(CultureInfo.InvariantCulture, $"logs/: {logFiles} file(s)    summary.txt: version, platform, plugins\n");
        text.Append(Strings.Get("DiagBundle_ReadMeExcluded")).Append("\n\n");
        text.Append(Strings.Get("DiagBundle_ReadMeRedacted")).Append('\n');
        if (counts.Count == 0) text.Append("  (none)\n");
        foreach ((string kind, int count) in counts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            text.Append(CultureInfo.InvariantCulture, $"  {kind}: {count}\n");
        }
        return text.ToString();
    }

    /// <summary>日志可能正被追加写入:按共享读打开。读不了的跳过,不让一份日志挡住整个包。</summary>
    private static async Task<string?> ReadSharedAsync(FileInfo file, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task AddTextAsync(ZipArchive zip, string name, string text, CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using Stream stream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(new UTF8Encoding(false).GetBytes(text), cancellationToken).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

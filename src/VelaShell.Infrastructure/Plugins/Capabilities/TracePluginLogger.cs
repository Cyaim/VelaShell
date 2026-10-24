using System.Diagnostics;
using VelaShell.PluginSdk.Logging;

namespace VelaShell.Infrastructure.Plugins.Capabilities;

/// <summary>
/// 插件日志的默认落点:宿主 Trace 管道,带插件 id 与级别前缀;配了 <see cref="PluginLogFiles" /> 时
/// 另落一份到这个插件自己的日志文件(插件管理页「查看日志」读的就是它)。零分配热路径不适用日志,无需更重的机制。
/// </summary>
internal sealed class TracePluginLogger(string pluginId, PluginLogFiles? files = null) : IPluginLogger
{
    public void Write(PluginLogLevel level, string message, Exception? exception = null)
    {
        string text = exception is null ? message : $"{message} — {exception}";
        Trace.WriteLine($"[Plugin:{pluginId}] [{level}] {text}");
        files?.Append(pluginId, $"[{level}] {text}");
    }

    /// <summary>隔离进程 stdout / stderr 的一行(未处理异常的堆栈、插件自己 <c>Console.WriteLine</c> 的都在这里)。</summary>
    /// <param name="isError">是否来自 stderr。</param>
    /// <param name="line">那一行。</param>
    public void WriteProcessOutput(bool isError, string line)
    {
        Trace.WriteLine($"[PluginHost:{pluginId}] {line}");
        files?.Append(pluginId, $"[{(isError ? "stderr" : "stdout")}] {line}");
    }

    /// <summary>宿主替插件记的生命周期事件(激活、激活失败、崩溃、重启),只进插件自己的日志文件 —— 宿主日志里已经有一行。</summary>
    /// <param name="message">事件说明。</param>
    public void WriteHostEvent(string message) => files?.Append(pluginId, $"[Host] {message}");
}

namespace VelaShell.Core.Models;

/// <summary>配置类审计记录的来源(<see cref="AuditEntry.Source" />)。</summary>
public static class AuditSources
{
    /// <summary>本机编辑:编辑框、设置页、拖分组、连接成功的回写……没有设来源作用域时都算这一种。</summary>
    public const string Local = "local";

    /// <summary>云同步拉取(含恢复历史版本):应用远端载荷时落的盘。</summary>
    public const string CloudSync = "cloud-sync";

    /// <summary>导入:连接文件(VelaShell JSON / CSV),以及从 Xshell、WinSCP、ssh_config 导入会话。</summary>
    public const string Import = "import";

    /// <summary>登录框里勾了「同时更新共享凭据」,把刚输的那份存回凭据。</summary>
    public const string LoginPrompt = "login-prompt";
}

/// <summary>
/// 配置改动的来源作用域:在一段异步流程里声明「接下来的保存是云同步 / 导入 / 登录框带来的」,
/// 审计装饰器写记录时取 <see cref="Current" />。
/// </summary>
/// <remarks>
/// <para>
/// 用 <see cref="AsyncLocal{T}" /> 而不是一个静态开关:云同步在后台跑的同时,用户可能正在编辑框里改另一条配置 ——
/// 静态开关会把那次手改也记成「云同步」。<see cref="AsyncLocal{T}" /> 只跟着设它的那条异步流程走
/// (含其中 <c>await</c> 的下游与当时起的任务),别的流程看不见。
/// </para>
/// <para>
/// 作用域内同步引发的事件(<c>SettingsSaved</c>、共享凭据的 <c>Changed</c>)也在同一条流程里,
/// 订阅方若因此又存了一次,那一次同样记成这个来源 —— 它本来就是这次同步带出来的。
/// </para>
/// </remarks>
public static class AuditSourceScope
{
    private static readonly AsyncLocal<string?> Source = new();

    /// <summary>当前流程的来源;没有设作用域时为 <see cref="AuditSources.Local" />。</summary>
    public static string Current => Source.Value ?? AuditSources.Local;

    /// <summary>进入一个来源作用域,<c>Dispose</c> 时退回进入之前的来源(可以嵌套)。</summary>
    /// <param name="source"><see cref="AuditSources" /> 里的一个值。</param>
    /// <returns>退出作用域用的句柄,配合 <c>using</c>。</returns>
    public static IDisposable Enter(string source)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        string? previous = Source.Value;
        Source.Value = source;
        return new Exit(previous);
    }

    private sealed class Exit(string? previous) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done)
            {
                return;
            }
            _done = true;
            Source.Value = previous;
        }
    }
}

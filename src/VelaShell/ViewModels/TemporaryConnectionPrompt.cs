using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Presentation.Services;

namespace VelaShell.ViewModels;

/// <summary>
/// 登录框与临时连接之间的两头:弹框前认出要连的是不在库里的临时配置,弹框后按「另存为连接」把它存成正式配置。
/// </summary>
/// <remarks>
/// <para>
/// 临时配置是外部拉起(Xshell 兼容登录)现搭的、「最近连接」里那条配置已删时重建的。连接工作流只回写库里已有的配置
/// (plan.md §252),所以它们只活在本次运行 —— 登录框上的「记住密码」对它们勾了也存不下。
/// 改成「另存为连接」:存成正式配置之后,密码才按「记住密码」(与常规页的总开关)记下(维护者 2026-10-25 定,第二轮第 15 条)。
/// </para>
/// <para>
/// 从窗口代码后置里拆出来,是为了不弹窗也能测。
/// </para>
/// </remarks>
public static class TemporaryConnectionPrompt
{
    /// <summary>要连的配置是不是不在库里的临时配置。</summary>
    /// <param name="sessions">会话仓储;没有时(单测、设计期)一律当作已保存的,登录框照旧给「记住密码」。</param>
    /// <param name="profile">要连接的配置。</param>
    /// <returns>库里没有这条配置时为 true;读不出来时为 false(退回以前的样子,不冒险)。</returns>
    public static async Task<bool> IsTemporaryAsync(ISessionRepository? sessions, SessionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (sessions is null)
        {
            return false;
        }
        try
        {
            return await sessions.GetSessionAsync(profile.Id).ConfigureAwait(true) is null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// 把登录框交回来的这一份存成正式的连接配置(进资源管理器,未分组)。
    /// </summary>
    /// <remarks>
    /// 经工作流保存:密码落不落盘照「记住密码」与常规页的总开关(<see cref="IConnectionWorkflowService.SaveProfileAsync" />)。
    /// 存进去之后它就不再是临时配置了:连上时工作流照常回写最近连接时间,下次启动可以恢复,也随云同步走。
    /// 审计里记成「登录框」,与在登录框里顺手更新共享凭据同一个来源。
    /// </remarks>
    /// <param name="workflow">连接工作流。</param>
    /// <param name="profile">登录框交回来的配置副本(带着这次输入的凭据)。</param>
    /// <returns>表示保存完成的任务。</returns>
    public static async Task SaveAsConnectionAsync(IConnectionWorkflowService workflow, SessionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(profile);
        using IDisposable source = AuditSourceScope.Enter(AuditSources.LoginPrompt);
        await workflow.SaveProfileAsync(profile).ConfigureAwait(true);
    }
}

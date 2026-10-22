using Microsoft.Extensions.AI;
using VelaShell.Plugin.Ai.Agent;
using VelaShell.Plugin.Ai.Configuration;
using VelaShell.PluginSdk.RemoteFs;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Ai.Tests;

/// <summary>
/// <c>send_file</c>:把已下载的文件当附件发进 IM 聊天。
/// </summary>
/// <remarks>
/// <b>这整个文件都是安全用例。</b>一个"能读本机文件并推进群里"的工具,就是一条完整的
/// 外泄通道 —— 群里任何一个人说一句"帮我看看 id_rsa",模型没有理由怀疑这句话。
/// 挡住它的不是黑名单(数不完),是<b>白名单一个目录</b>:只能发
/// <c>download_remote_file</c> 自己下下来的那些,于是发得出去的东西必然来自一台
/// 这次授权允许操作的服务器 —— 那一关在下载的时候就过了。
/// </remarks>
[TestClass]
public sealed class SendFileToolTests
{
    private static async Task<string> InvokeAsync(AgentToolbox toolbox, string path)
    {
        AIFunction function = toolbox.CreateTools(ChatMode.Agent).OfType<AIFunction>().Single(f => f.Name == "send_file");
        object? result = await function.InvokeAsync(
            [with(new Dictionary<string, object?> { ["path"] = path })], CancellationToken.None);
        return result?.ToString() ?? "";
    }

    private static async Task<string> InvokeAsync(AgentToolbox toolbox, string tool, Dictionary<string, object?> arguments)
    {
        AIFunction function = toolbox.CreateTools(ChatMode.Agent).OfType<AIFunction>().Single(f => f.Name == tool);
        object? result = await function.InvokeAsync([with(arguments)], CancellationToken.None);
        return result?.ToString() ?? "";
    }

    private static AgentToolbox WithSink(TestPluginContext context, List<string> sent)
        => new(context)
        {
            Approval = ApprovalMode.Bypass,
            FileSender = (path, _) =>
            {
                sent.Add(path);
                return Task.FromResult<string?>(null);
            }
        };

    /// <summary>下载目录里的文件照常发得出去。</summary>
    [TestMethod]
    public async Task AFileInTheDownloadsFolder_IsSent()
    {
        using var context = new TestPluginContext();
        string path = Downloaded(context, "2026-09-03.txt");
        List<string> sent = [];

        string result = await InvokeAsync(WithSink(context, sent), path);

        Assert.Contains("Sent", result);
        Assert.Contains(path, sent);
    }

    /// <summary>
    /// <b>下载目录之外的一律拒绝。</b>用户自己的密钥、桌面、浏览器数据,一个都够不着。
    /// </summary>
    [TestMethod]
    public async Task AFileOutsideTheDownloadsFolder_IsRefused()
    {
        using var context = new TestPluginContext();
        string outside = Path.Combine(context.DataDirectory, "id_rsa");
        File.WriteAllText(outside, "PRIVATE KEY");
        List<string> sent = [];

        string result = await InvokeAsync(WithSink(context, sent), outside);

        Assert.Contains("Refused", result);
        Assert.IsEmpty(sent, "范围之外的文件一个字节都不该发出去");
    }

    /// <summary>
    /// <c>..</c> 穿不出去 —— 路径先规范化再比。
    /// </summary>
    /// <remarks>
    /// 这是路径白名单最典型的绕法:名义上还在 <c>downloads/</c> 下面,
    /// 拼出来却落到了用户的主目录里。
    /// </remarks>
    [TestMethod]
    public async Task ATraversalOutOfTheDownloadsFolder_IsRefused()
    {
        using var context = new TestPluginContext();
        string secret = Path.Combine(context.DataDirectory, "secrets.txt");
        File.WriteAllText(secret, "token");
        string traversal = Path.Combine(context.DataDirectory, "downloads", "..", "secrets.txt");
        List<string> sent = [];

        string result = await InvokeAsync(WithSink(context, sent), traversal);

        Assert.Contains("Refused", result);
        Assert.IsEmpty(sent);
    }

    /// <summary>
    /// 前缀比对必须带目录分隔符,否则 <c>downloads-x/</c> 会被当成 <c>downloads/</c> 里面。
    /// </summary>
    [TestMethod]
    public async Task ASiblingFolderWithTheSamePrefix_IsRefused()
    {
        using var context = new TestPluginContext();
        string folder = Path.Combine(context.DataDirectory, "downloads-secret");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "x.txt");
        File.WriteAllText(path, "nope");
        List<string> sent = [];

        string result = await InvokeAsync(WithSink(context, sent), path);

        Assert.Contains("Refused", result);
        Assert.IsEmpty(sent);
    }

    /// <summary>路径对但文件不在,说的是"先下下来",而不是一句转义失败。</summary>
    [TestMethod]
    public async Task AMissingFile_SaysToDownloadItFirst()
    {
        using var context = new TestPluginContext();
        Directory.CreateDirectory(Path.Combine(context.DataDirectory, "downloads"));
        string path = Path.Combine(context.DataDirectory, "downloads", "not-there.txt");

        string result = await InvokeAsync(WithSink(context, []), path);

        Assert.Contains("No such local file", result);
    }

    /// <summary>审批被拒就不发,而且不该换个姿势再试。</summary>
    [TestMethod]
    public async Task ADeniedApproval_StopsTheSend()
    {
        using var context = new TestPluginContext();
        string path = Downloaded(context, "log.txt");
        List<string> sent = [];
        var toolbox = new AgentToolbox(context)
        {
            Approval = ApprovalMode.Ask,
            ApprovalHandler = _ => Task.FromResult(false),
            FileSender = (p, _) =>
            {
                sent.Add(p);
                return Task.FromResult<string?>(null);
            }
        };

        string result = await InvokeAsync(toolbox, path);

        Assert.Contains("DENIED", result);
        Assert.IsEmpty(sent);
    }

    /// <summary>
    /// <b>没有落点就不注册这个工具。</b>聊天面板与对外 MCP 走的就是这条路。
    /// </summary>
    /// <remarks>
    /// 摆一个永远失败的工具比不摆更糟:模型看得见它,就会反复去试,
    /// 然后把这次失败当成自己的问题,绕着弯子重来。
    /// </remarks>
    [TestMethod]
    public void WithNoChatToSendInto_TheToolIsNotEvenRegistered()
    {
        using var context = new TestPluginContext();

        string[] names = [.. new AgentToolbox(context).CreateTools(ChatMode.Agent)
            .OfType<AIFunction>().Select(f => f.Name)];

        Assert.DoesNotContain("send_file", names);
    }

    /// <summary>渠道那头失败时,原话回给模型,而不是假装发出去了。</summary>
    [TestMethod]
    public async Task AChannelFailure_IsReportedNotSwallowed()
    {
        using var context = new TestPluginContext();
        string path = Downloaded(context, "big.tar.gz");
        var toolbox = new AgentToolbox(context)
        {
            Approval = ApprovalMode.Bypass,
            FileSender = (_, _) => Task.FromResult<string?>("Feishu accepts at most 30 MB.")
        };

        string result = await InvokeAsync(toolbox, path);

        Assert.Contains("30 MB", result);
        Assert.DoesNotContain("Sent", result);
    }

    // ———————————————————— 下载还没成功提交 ————————————————————
    //
    // 新下载成功提交之前,downloads/ 里那个同名文件要么不存在、要么是上一次留下的旧文件 ——
    // 两种都不是模型以为的那份。宿主写的是随机临时文件、成功才原子改名,所以只有 Succeeded 才算数。

    /// <summary>下载进行中,同名旧文件不发;宿主报告 Succeeded(新文件已提交)之后才放行。</summary>
    [TestMethod]
    public async Task AnOldFileUnderARunningDownload_IsRefusedUntilItSucceeds()
    {
        using var context = new TestPluginContext();
        string path = Downloaded(context, "app.log");
        string session = RemoteFile(context, "/var/log/app.log");
        var finish = new TaskCompletionSource<RemoteTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        RemoteTransferSnapshot? started = null;
        context.FakeRemoteFs.StartDownloadHandler = (id, remote, local, options, _) =>
            Task.FromResult(started = Snapshot(id, remote, local, options.OperationId, RemoteTransferState.Running));
        context.FakeRemoteFs.WaitTransferHandler = (_, _, _, ct) => finish.Task.WaitAsync(ct);
        List<string> sent = [];
        await using AgentToolbox toolbox = WithSink(context, sent);

        Assert.Contains("transfer_id", await InvokeAsync(toolbox, "download_remote_file",
            new() { ["remotePath"] = "/var/log/app.log", ["sessionId"] = session }));
        string refused = await InvokeAsync(toolbox, path);

        Assert.Contains("Refused", refused);
        Assert.IsEmpty(sent, "下载还在跑,磁盘上那份是上一次的旧文件");

        File.WriteAllText(path, "fresh");
        finish.SetResult(started! with { State = RemoteTransferState.Succeeded, Sequence = 2, RemoteStopConfirmed = true });
        await InvokeAsync(toolbox, "transfer_wait", new() { ["transferId"] = started.TransferId, ["waitSeconds"] = 5 });

        Assert.Contains("Sent", await InvokeAsync(toolbox, path));
        Assert.HasCount(1, sent);
    }

    /// <summary>
    /// 失败、取消、超时、结果未知都没有提交新文件:同名旧文件不发。重下一次成功就放行;
    /// 最近一次又没成功,磁盘上那份就又成了旧文件。
    /// </summary>
    [TestMethod]
    public async Task AnUnsuccessfulDownload_LeavesTheOldFileUnsendable_UntilARetrySucceeds()
    {
        foreach (RemoteTransferState ended in new[] { RemoteTransferState.Failed, RemoteTransferState.Cancelled,
                     RemoteTransferState.TimedOut, RemoteTransferState.Unknown })
        {
            using var context = new TestPluginContext();
            string path = Downloaded(context, "app.log");
            string session = RemoteFile(context, "/var/log/app.log");
            RemoteTransferState next = ended;
            context.FakeRemoteFs.StartDownloadHandler = (id, remote, local, options, _) =>
                Task.FromResult(Snapshot(id, remote, local, options.OperationId, next));
            List<string> sent = [];
            await using AgentToolbox toolbox = WithSink(context, sent);
            Dictionary<string, object?> download = new() { ["remotePath"] = "/var/log/app.log", ["sessionId"] = session };

            await InvokeAsync(toolbox, "download_remote_file", download);
            Assert.Contains("Refused", await InvokeAsync(toolbox, path), ended.ToString());

            next = RemoteTransferState.Succeeded;
            await InvokeAsync(toolbox, "download_remote_file", download);
            Assert.Contains("Sent", await InvokeAsync(toolbox, path), ended.ToString());

            next = ended;
            await InvokeAsync(toolbox, "download_remote_file", download);
            Assert.Contains("Refused", await InvokeAsync(toolbox, path), ended.ToString());
            Assert.HasCount(1, sent, ended.ToString());
        }
    }

    /// <summary>启动回执还没回来(宿主可能已经在写)时同样不发。</summary>
    [TestMethod]
    public async Task AnOldFileUnderAPendingDownloadStart_IsRefused()
    {
        using var context = new TestPluginContext();
        string path = Downloaded(context, "app.log");
        string session = RemoteFile(context, "/var/log/app.log");
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receipt = new TaskCompletionSource<RemoteTransferSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.FakeRemoteFs.StartDownloadHandler = (_, _, _, _, _) =>
        {
            accepted.TrySetResult();
            return receipt.Task;
        };
        List<string> sent = [];
        await using AgentToolbox toolbox = WithSink(context, sent);

        Task<string> download = InvokeAsync(toolbox, "download_remote_file",
            new() { ["remotePath"] = "/var/log/app.log", ["sessionId"] = session });
        await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("Refused", await InvokeAsync(toolbox, path));

        receipt.SetResult(Snapshot(session, "/var/log/app.log", path, "late", RemoteTransferState.Succeeded));
        await download;
        Assert.Contains("Sent", await InvokeAsync(toolbox, path));
        Assert.HasCount(1, sent);
    }

    /// <summary>停止未确认的 Unknown 可能还在往这个路径写,之后哪怕重下成功也不发。</summary>
    [TestMethod]
    public async Task AnUnconfirmedStop_KeepsTheTargetUnsendable()
    {
        using var context = new TestPluginContext();
        string path = Downloaded(context, "app.log");
        string session = RemoteFile(context, "/var/log/app.log");
        bool first = true;
        context.FakeRemoteFs.StartDownloadHandler = (id, remote, local, options, _) =>
        {
            RemoteTransferSnapshot snapshot = first
                ? Snapshot(id, remote, local, options.OperationId, RemoteTransferState.Unknown, stopConfirmed: false)
                : Snapshot(id, remote, local, options.OperationId, RemoteTransferState.Succeeded);
            first = false;
            return Task.FromResult(snapshot);
        };
        List<string> sent = [];
        await using AgentToolbox toolbox = WithSink(context, sent);
        Dictionary<string, object?> download = new() { ["remotePath"] = "/var/log/app.log", ["sessionId"] = session };

        await InvokeAsync(toolbox, "download_remote_file", download);
        await InvokeAsync(toolbox, "download_remote_file", download);

        Assert.Contains("Refused", await InvokeAsync(toolbox, path));
        Assert.IsEmpty(sent);
    }

    /// <summary>
    /// 明确被拒的启动什么都没写,不改变这个路径上的结论;回执丢失的启动则可能已经在写,照样不发。
    /// </summary>
    [TestMethod]
    public async Task ARejectedStart_ChangesNothing_ButALostReceiptBlocks()
    {
        using var context = new TestPluginContext();
        string path = Downloaded(context, "app.log");
        string session = RemoteFile(context, "/var/log/app.log");
        Exception? failure = null;
        context.FakeRemoteFs.StartDownloadHandler = (id, remote, local, options, _) => failure is null
            ? Task.FromResult(Snapshot(id, remote, local, options.OperationId, RemoteTransferState.Succeeded))
            : Task.FromException<RemoteTransferSnapshot>(failure);
        List<string> sent = [];
        await using AgentToolbox toolbox = WithSink(context, sent);
        Dictionary<string, object?> download = new() { ["remotePath"] = "/var/log/app.log", ["sessionId"] = session };

        await InvokeAsync(toolbox, "download_remote_file", download);
        failure = new ArgumentException("rejected before execution");
        Assert.Contains("rejected before execution", await InvokeAsync(toolbox, "download_remote_file", download));
        Assert.Contains("Sent", await InvokeAsync(toolbox, path));

        failure = new IOException("receipt lost");
        Assert.Contains("Unknown", await InvokeAsync(toolbox, "download_remote_file", download));
        Assert.Contains("Refused", await InvokeAsync(toolbox, path));
        Assert.HasCount(1, sent);
    }

    private static string Downloaded(TestPluginContext context, string name)
    {
        string folder = Path.Combine(context.DataDirectory, "downloads");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, "2026-09-03 10:00:00 INFO started");
        return path;
    }

    /// <summary>支持后台下载的宿主,远端预置一个文件;返回会话 id。</summary>
    private static string RemoteFile(TestPluginContext context, string remotePath)
    {
        string session = context.FakeSessions.AddConnected().SessionId;
        context.FakeRemoteFs.SupportsTransferOperations = true;
        context.FakeRemoteFs.SupportsDownloadOperations = true;
        context.FakeRemoteFs.AddFile(session, remotePath, new byte[64]);
        return session;
    }

    private static RemoteTransferSnapshot Snapshot(string session, string remote, string local, string operation,
        RemoteTransferState state, bool stopConfirmed = true) => new()
        {
            TransferId = "download-" + operation,
            OperationId = operation,
            SessionId = session,
            LocalPath = local,
            RemotePath = remote,
            Direction = RemoteTransferDirection.Download,
            State = state,
            RemoteStopConfirmed = state != RemoteTransferState.Running && stopConfirmed,
            Generation = 1,
            Sequence = 1,
            TotalBytes = 64,
            TransferredBytes = state == RemoteTransferState.Succeeded ? 64 : 0
        };
}

using System.Diagnostics;
using VelaShell.Core.Services;

namespace VelaShell.Core.Tests.Services;

/// <summary>
/// 在本机的 sh + awk 上真跑 GPU 探针,喂给它一棵假的 /proc 与 /sys。
/// </summary>
/// <remarks>
/// #622 的用例只往解析器里喂手写的 <c>__GI__</c> 行,从没执行过那段 awk —— 于是它在 mawk / gawk / busybox 上
/// 恒为空(打不开已经关掉的 fd 就整个退出、每个文件开头又把累计量清零)也照样全绿。
/// 这里把探针里的 <c>/proc/</c>、<c>/sys/</c> 换成夹具目录,在 Linux / macOS 的 CI 上跑真的 shell 与 awk;
/// 本机装了哪种 awk 就多跑哪种,没装的标为未执行。Windows 没有 POSIX sh,整类标为未执行。
/// </remarks>
[TestClass]
[TestCategory("Metrics")]
public sealed class IntelGpuProbeTests
{
    private const string Integrated = "0000:00:02.0";
    private const string Discrete = "0000:03:00.0";
    private const string Unbound = "0000:06:00.0";

    private string _root = "";

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "velashell-gpu-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (_root.Length > 0 && Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    /// <summary>
    /// i915 核显 + xe 独显,两轮采样:只读指向 /dev/dri 的 fd、同一客户端的多个 fd 只算一次、
    /// 已经关掉的 fd 不拖垮整段,差分出的利用率取最忙的引擎。
    /// </summary>
    /// <param name="mode"><c>sh</c> 用系统自带的 sh 与 awk;其余把 awk(busybox 是全部工具)换成指定实现。</param>
    [TestMethod]
    [DataRow("sh")]
    [DataRow("mawk")]
    [DataRow("gawk")]
    [DataRow("original-awk")]
    [DataRow("busybox")]
    public async Task GpuProbe_ReadsOnlyDrmFdsAndDiffsPerClient(string mode)
    {
        (string shell, string[] prefix, string? shim) = Toolchain(mode);
        string first = BuildFixture("s1", second: false);
        string second = BuildFixture("s2", second: true);

        SessionMetrics before = SessionMetrics.Parse(
            await RunAsync(shell, prefix, shim, Rooted(SessionMetrics.BuildCommand(MetricsScope.Gpu), first)))!;
        SessionMetrics after = SessionMetrics.Parse(
            await RunAsync(shell, prefix, shim, Rooted(SessionMetrics.BuildCommand(MetricsScope.Gpu), second)))!;

        GpuDevice integrated = before.Gpus.Single(g => g.Card == "card0");
        Assert.AreEqual(GpuMemoryKind.SharedSystem, integrated.MemoryKind);
        // 客户端 7(2 MiB,两个 fd)+ 客户端 9(4 MiB);指向 /dev/null 的那个 fd 写着 999 MiB,不能被读到。
        Assert.AreEqual(6L * 1024 * 1024, integrated.MemUsedBytes);
        Assert.AreEqual(16384000L * 1024, integrated.MemTotalBytes);
        Assert.IsTrue(integrated.ClientScopedUtil);
        GpuUsageCounter counters = before.GpuUsageCounters.Single(c => c.Card == "card0");
        Assert.AreSequenceEqual(["7", "9"], [.. counters.Engines.Select(e => e.Client).Distinct()], Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        Assert.AreEqual(1000.0, counters.UptimeSeconds);

        GpuDevice discrete = before.Gpus.Single(g => g.Card == "card1");
        Assert.AreEqual(GpuMemoryKind.Dedicated, discrete.MemoryKind);
        Assert.AreEqual(512L * 1024 * 1024, discrete.MemUsedBytes);
        // 非 PCI 的 card2(没有 vendor)与 amdgpu 的客户端都不该出现。
        // 只数 DRM 卡:跑测试的机器上若有 nvidia-smi(WSL 直通 NVIDIA 就有),它报的卡没有 DRM 名字。
        Assert.HasCount(2, before.Gpus.Where(g => g.Card.Length > 0));

        // 本地间隔给 1 秒,远端 uptime 走了 2 秒:分母必须用远端的。
        after.ApplyGpuUsage(before.GpuUsageCounters, 1);
        // render:客户端 7 走了 0.6 秒 / 2 秒 = 30%;video:1.6 秒 / 2 秒 / 2 个实例 = 40%。
        // 客户端 9 退出、13 新来,都不参与差分。
        Assert.AreEqual(40.0, after.Gpus.Single(g => g.Card == "card0").UtilPercent!.Value, 0.001);
        // xe:rcs 500/1000 = 50%;vcs 1600/1000/2 = 80%。
        Assert.AreEqual(80.0, after.Gpus.Single(g => g.Card == "card1").UtilPercent!.Value, 0.001);
    }

    [TestMethod]
    public async Task StaticProbe_SkipsNonPciDrmDevices()
    {
        (string shell, string[] prefix, string? shim) = Toolchain("sh");
        string root = BuildFixture("s1", second: false);

        SessionStaticInfo info = SessionMetrics.ParseStatic(
            await RunAsync(shell, prefix, shim, Rooted(SessionMetrics.StaticCommand, root)));

        GpuCardInfo[] drm = [.. info.GpuCards.Where(c => c.HasDrm)];
        Assert.AreSequenceEqual(["card0", "card1"], [.. drm.Select(c => c.Card)]);
        Assert.AreSequenceEqual(["i915", "xe"], [.. drm.Select(c => c.Driver)]);
        Assert.AreSequenceEqual([Integrated, Discrete], [.. drm.Select(c => c.Slot)]);
        // 直通进来、宿主没装驱动的卡:驱动留空,不能把 sysfs 的链接名 "driver" 当成驱动
        GpuCardInfo unbound = info.GpuCards.Single(c => c.Slot == Unbound);
        Assert.IsFalse(unbound.HasDrm);
        Assert.AreEqual("", unbound.Driver);
    }

    /// <summary>
    /// 把探针里的绝对路径挪进夹具目录。夹具里没有 <c>/etc/os-release</c>:没有它的主机(CentOS 6、macOS 跑测试的机器)
    /// 上,基础段读它那一步不能让 POSIX sh 整个退出、把后面的 GPU 段一起吞掉。
    /// </summary>
    private static string Rooted(string command, string root) =>
        command.Replace("/proc/", root + "/proc/", StringComparison.Ordinal)
            .Replace("/sys/", root + "/sys/", StringComparison.Ordinal)
            .Replace("/etc/", root + "/etc/", StringComparison.Ordinal);

    /// <summary>选出要跑的 shell 与替身目录;本机没有对应实现时标为未执行。</summary>
    private (string Shell, string[] Prefix, string? Shim) Toolchain(string mode)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("[SKIP] GPU 探针要在 POSIX sh 与 awk 上跑(Linux / macOS)。");
        }
        if (mode == "sh")
        {
            return ("/bin/sh", [], null);
        }
        string? tool = FindExecutable(mode);
        if (tool is null)
        {
            Assert.Inconclusive($"[SKIP] 本机没有 {mode}。");
        }
        string shim = Path.Combine(_root, "bin");
        Directory.CreateDirectory(shim);
        if (mode == "busybox")
        {
            // 只有 busybox 的主机(Alpine、路由器):ls / cat / readlink 的输出格式也换成它的。
            foreach (string applet in new[] { "awk", "ls", "cat", "readlink", "basename", "head" })
            {
                File.CreateSymbolicLink(Path.Combine(shim, applet), tool);
            }
            return (tool, ["sh"], shim);
        }
        File.CreateSymbolicLink(Path.Combine(shim, "awk"), tool);
        return ("/bin/sh", [], shim);
    }

    private static string? FindExecutable(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, name))
            .FirstOrDefault(File.Exists);

    private async Task<string> RunAsync(string shell, string[] prefix, string? shim, string command)
    {
        var start = new ProcessStartInfo(shell)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string argument in prefix)
        {
            start.ArgumentList.Add(argument);
        }
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command);
        if (shim is not null)
        {
            start.Environment["PATH"] = shim + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        }
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.CancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        string output = await stdout;
        TestContext.WriteLine(output);
        TestContext.WriteLine(await stderr);
        return output;
    }

    /// <summary>
    /// 夹具:i915 核显(card0)、xe 独显(card1)、一个非 PCI 的 card2、一块没绑驱动的 PCI 显示设备,
    /// 以及几个进程的 fd。
    /// <paramref name="second" /> 为 true 时是两秒(远端 uptime)之后的第二轮。
    /// </summary>
    private string BuildFixture(string name, bool second)
    {
        string root = Path.Combine(_root, name);
        Write(root, $"sys/devices/pci0000:00/{Integrated}/vendor", "0x8086\n");
        Write(root, $"sys/devices/pci0000:00/{Discrete}/vendor", "0x8086\n");
        Directory.CreateDirectory(Path.Combine(root, "sys/devices/platform/gpu"));
        Directory.CreateDirectory(Path.Combine(root, "sys/class/drm/card0-DP-1"));
        Link(root, $"sys/bus/pci/drivers/i915/{Integrated}", $"../../../../devices/pci0000:00/{Integrated}");
        Link(root, $"sys/bus/pci/drivers/xe/{Discrete}", $"../../../../devices/pci0000:00/{Discrete}");
        Link(root, $"sys/devices/pci0000:00/{Integrated}/driver", "../../../bus/pci/drivers/i915");
        Link(root, $"sys/devices/pci0000:00/{Discrete}/driver", "../../../bus/pci/drivers/xe");
        Link(root, "sys/class/drm/card0/device", $"../../../devices/pci0000:00/{Integrated}");
        Link(root, "sys/class/drm/card1/device", $"../../../devices/pci0000:00/{Discrete}");
        Link(root, "sys/class/drm/card2/device", "../../../devices/platform/gpu");
        // 没绑驱动的 PCI 显示设备:有 class / vendor,没有 driver 链接
        Write(root, $"sys/bus/pci/devices/{Unbound}/class", "0x030000\n");
        Write(root, $"sys/bus/pci/devices/{Unbound}/vendor", "0x10de\n");
        Write(root, $"sys/bus/pci/devices/{Unbound}/device", "0x2204\n");
        Write(root, "proc/meminfo", "MemTotal:       16384000 kB\nMemFree:         8000000 kB\n");
        Write(root, "proc/uptime", second ? "1002.00 4008.00\n" : "1000.00 4000.00\n");

        string client7 = Fdinfo("i915", 7, Integrated,
            "drm-total-system0:\t4 MiB\ndrm-resident-system0:\t2 MiB\n" +
            "drm-total-stolen-system0:\t0\ndrm-resident-stolen-system0:\t0\n" +
            $"drm-engine-render:\t{(second ? 1600000000 : 1000000000)} ns\ndrm-engine-copy:\t0 ns\n" +
            $"drm-engine-video:\t{(second ? 1600000000 : 0)} ns\ndrm-engine-capacity-video:\t2\n" +
            "drm-engine-video-enhance:\t0 ns\n");
        Fd(root, 100, 3, "/dev/dri/renderD128", client7);
        // 指向 /dev/null 的 fd:内容故意写成 DRM 客户端的样子,读到了就会把数字带歪。
        Fd(root, 100, 4, "/dev/null", Fdinfo("i915", 99, Integrated,
            "drm-engine-render:\t999999999999 ns\ndrm-resident-system0:\t999 MiB\n"));
        // 同一个客户端的第二个 fd(dup / fork 继承),只能算一次。
        Fd(root, 100, 5, "/dev/dri/renderD128", client7);
        if (second)
        {
            Fd(root, 500, 3, "/dev/dri/renderD128", Fdinfo("i915", 13, Integrated, "drm-engine-render:\t5000000000 ns\n"));
        }
        else
        {
            Fd(root, 200, 6, "/dev/dri/card0", Fdinfo("i915", 9, Integrated,
                "drm-resident-system0:\t4 MiB\ndrm-engine-render:\t2000000000 ns\n"));
            // ls 看见了、cat 时已经关掉的 fd:旧探针的 awk 在这里整个退出。
            Fd(root, 200, 8, "/dev/dri/renderD128", null);
        }
        Fd(root, 300, 3, "/dev/dri/renderD129", Fdinfo("xe", 11, Discrete,
            "drm-total-system:\t16 MiB\ndrm-resident-system:\t16 MiB\ndrm-resident-gtt:\t4 MiB\n" +
            "drm-resident-vram0:\t512 MiB\ndrm-resident-stolen:\t0\n" +
            $"drm-cycles-rcs:\t{(second ? 600 : 100)}\ndrm-total-cycles-rcs:\t{(second ? 2000 : 1000)}\n" +
            $"drm-cycles-vcs:\t{(second ? 1600 : 0)}\ndrm-total-cycles-vcs:\t{(second ? 2000 : 1000)}\n" +
            "drm-engine-capacity-vcs:\t2\n"));
        Fd(root, 300, 9, "/dev/pts/0", "pos:\t0\nflags:\t02\nmnt_id:\t26\ntty-index:\t0\n");
        Fd(root, 400, 1, "/dev/dri/renderD130", Fdinfo("amdgpu", 3, "0000:05:00.0", "drm-engine-gfx:\t5 ns\n"));
        return root;
    }

    private static string Fdinfo(string driver, int client, string pdev, string body) =>
        $"pos:\t0\nflags:\t02100002\nmnt_id:\t26\nino:\t1100\ndrm-driver:\t{driver}\n" +
        $"drm-client-id:\t{client}\ndrm-pdev:\t{pdev}\n" + body;

    private static void Fd(string root, int pid, int fd, string target, string? fdinfo)
    {
        Link(root, $"proc/{pid}/fd/{fd}", target);
        Directory.CreateDirectory(Path.Combine(root, $"proc/{pid}/fdinfo"));
        if (fdinfo is not null)
        {
            Write(root, $"proc/{pid}/fdinfo/{fd}", fdinfo);
        }
    }

    private static void Write(string root, string path, string content)
    {
        string full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static void Link(string root, string path, string target)
    {
        string full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.CreateSymbolicLink(full, target);
    }
}

using VelaShell.Core.Ssh;

namespace VelaShell.Core.XServer;

/// <summary>
/// 经 SSH 转发 X11 时请远端的 GTK 4 程序用 cairo 渲染:连接后注入远端 shell 的那一句 <c>GSK_RENDERER=cairo</c>。
/// </summary>
/// <remarks>
/// GTK 4 默认用 GL 渲染。经 SSH 转发时远端拿不到 GPU,Mesa 退到 llvmpipe 在远端 CPU 上画,再把<b>整个窗口</b>的像素经 PutImage 发过来,
/// 每一帧都是整窗:gnome-calculator 点一下按钮 GL 是 29 MB、cairo 是 0.15 MB,打开模式下拉菜单 11 MB 对 3.6 MB —— 局域网上也卡。
/// X 服务端没法替客户端选渲染器(Mesa 的软件 EGL 只靠核心协议就能工作),所以与 <see cref="XServerInputMethod" /> 的 <c>XMODIFIERS</c>
/// 一样借连接后的静默注入在 shell 里设上。已经设了的(用户自己要 GL、别的渲染器)不动;只影响从这个 shell 里启动的程序。
/// </remarks>
public static class RemoteGtkRenderer
{
    /// <summary>GTK 4 选渲染器的环境变量。</summary>
    public const string Variable = "GSK_RENDERER";

    /// <summary>注入的值。</summary>
    public const string Value = "cairo";

    /// <summary>
    /// 在远端 shell 里设 <c>GSK_RENDERER</c>(没设过才设)的一句,按 shell 种类写;探不出种类、对端不是 POSIX / fish 时为空串(不注入)。
    /// </summary>
    /// <param name="kind">远端 shell 的种类(<see cref="RemoteShellProbe" /> 的结论)。</param>
    public static string ShellExport(RemoteShellKind kind) => kind switch
    {
        RemoteShellKind.Bash or RemoteShellKind.Zsh or RemoteShellKind.PosixSh => $"[ -n \"${{{Variable}-}}\" ] || export {Variable}={Value}",
        RemoteShellKind.Fish => $"set -q {Variable}; or set -gx {Variable} {Value}",
        _ => string.Empty,
    };
}

namespace VelaShell.Terminal.Emulation;

/// <summary>
/// OSC 133(FinalTerm / FTCS 语义提示符)在一行上留下的标记。
/// </summary>
/// <remarks>
/// <para>
/// 协议本身不画任何东西,只是让 shell 在字节流里打四个位置标记,把一屏输出切成结构化的
/// <b>命令块</b>:<c>[提示符][用户敲的命令][命令的输出][退出码]</c>。
/// </para>
/// <list type="bullet">
/// <item><c>OSC 133 ; A ST</c> —— 提示符开始 → <see cref="Prompt" /></item>
/// <item><c>OSC 133 ; B ST</c> —— 提示符结束、用户输入开始 → 记在行上的<b>列</b>(<see cref="TerminalRow.InputStart" />),不占这个枚举</item>
/// <item><c>OSC 133 ; C ST</c> —— 用户回车了,命令输出从这里开始 → <see cref="Output" />,列另记在 <see cref="TerminalRow.InputEnd" /></item>
/// <item><c>OSC 133 ; D ; 退出码 ST</c> —— 命令结束,退出码记在该块的提示符行上</item>
/// </list>
/// <para>
/// <b>B 不是行标记。</b>它标的是一个<b>列</b>位置(命令文本从提示符的第几列开始),跳转 / 选中输出 /
/// 折叠都只按<b>行</b>定界,用不到它;「复制这条命令」「重跑」要把命令文本从 B 切到 C,才用得到。
/// 列在改列宽重排后会整体挪位,<c>ReflowResize</c> 像搬光标那样把它换算成逻辑行内的偏移,
/// 跟着重新换行再落回去(见 <see cref="TerminalRow.InputStart" />)。
/// </para>
/// </remarks>
public enum PromptMark : byte
{
    /// <summary>普通行,不是任何命令块的边界。</summary>
    None = 0,

    /// <summary>提示符所在行(<c>OSC 133 ; A</c>)。命令块由它起头,退出码也记在这一行上。</summary>
    Prompt = 1,

    /// <summary>命令输出的首行(<c>OSC 133 ; C</c>)。「选中这条命令的输出」由它定上界。</summary>
    Output = 2
}

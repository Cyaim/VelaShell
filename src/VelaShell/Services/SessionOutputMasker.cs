using System.Text;
using System.Text.RegularExpressions;

namespace VelaShell.Services;

/// <summary>
/// 会话日志与会话录制的输出遮盖:落盘之前,把终端输出里形如口令、令牌、私钥的片段换成 <c>***</c>。只动记录的那一份,屏幕上照常显示。
/// </summary>
/// <remarks>
/// <para>
/// 只录输出、不录输入是对的(密码没有回显),但输出里照样有密钥:<c>cat .env</c>、<c>kubectl get secret -o yaml</c>、
/// <c>env | grep TOKEN</c>。会话日志与录制存的是原始字节,事后谁拿到文件谁就看得到。
/// 这里挂在桥的旁路(<c>DataReceived</c>)与记录之间,显示路径一个字节都不经过它。
/// </para>
/// <para>
/// 按字节处理:每个字节按 Latin-1 映射成一个字符再匹配,规则全是 ASCII,换出来的也只有 ASCII,
/// 终端编码是 UTF-8 还是 GBK 都原样往返。规则(大小写不敏感):
/// 名字里带 password / passwd / passphrase / secret / token / api key / access key / private key / credential 的
/// <c>名字=值</c>、<c>名字: 值</c>(含 JSON 与 YAML 的写法,名字与分隔符之间允许有 <c>grep --color</c> 插进来的颜色序列)只换值;
/// <c>Bearer</c> / <c>Basic</c> 后面的凭据;PEM 私钥块的正文;几种自带前缀的令牌(AWS <c>AKIA…</c> / <c>ASIA…</c>、
/// GitHub <c>ghp_…</c> 一类、Slack <c>xox?-…</c>、OpenAI 式 <c>sk-…</c>)。宁可多遮,不放过。
/// </para>
/// <para>
/// 一段输出可能在任何字节处被切开。整行(到 <c>\n</c>)才匹配;最后不完整的一行,只有它看上去<b>正写到一个值的中间</b>
/// (比如收到 <c>API_KEY=ab</c> 而后面还没来)或者正在私钥块里时才扣下,等下一段来了拼起来再看 —— 其余的当场放出去,
/// vim、top 这类不换行的全屏程序不受影响。扣下的部分有上限,超了就照现有内容遮完放出。
/// </para>
/// <para>
/// 线程:<see cref="Write" /> 跑在终端读线程上;<see cref="Flush" /> 在停止记录时从 UI 线程调,而旧桥可能还在往这里写
/// (停止记录不退订,见 <c>MainWindowViewModel.StopSessionLogging</c>),两者用一把锁串起来。
/// </para>
/// </remarks>
public sealed partial class SessionOutputMasker
{
    /// <summary>扣下的不完整一行最多攒这么多字节;超了照现有内容遮完放出。</summary>
    internal const int MaxHeldBytes = 4096;

    /// <summary>遮盖后的占位。</summary>
    private const string Mask = "***";

    private readonly Action<byte[]> _sink;
    private readonly Lock _gate = new();
    private byte[] _held = [];
    private bool _inPrivateKey;

    /// <summary>遮完的输出交给 <paramref name="sink" />(会话日志或录制的 <c>Write</c>)。</summary>
    public SessionOutputMasker(Action<byte[]> sink) => _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    /// <summary>收下一段输出(桥的 <c>DataReceived</c>)。</summary>
    public void Write(byte[] chunk)
    {
        if (chunk.Length == 0)
        {
            return;
        }
        lock (_gate)
        {
            WriteCore(chunk);
        }
    }

    private void WriteCore(byte[] chunk)
    {
        byte[] data = _held.Length == 0 ? chunk : [.. _held, .. chunk];
        _held = [];
        int lastNewline = Array.LastIndexOf(data, (byte)'\n');
        var output = new StringBuilder(data.Length);
        string text = Encoding.Latin1.GetString(data);
        string complete = lastNewline < 0 ? string.Empty : text[..(lastNewline + 1)];
        string tail = text[(lastNewline + 1)..];
        if (complete.Length > 0)
        {
            MaskLines(complete, output);
        }
        if (tail.Length > 0)
        {
            if ((_inPrivateKey || ValueInProgress().IsMatch(tail)) && tail.Length < MaxHeldBytes)
            {
                _held = Encoding.Latin1.GetBytes(tail);
            }
            else
            {
                MaskLines(tail, output);
            }
        }
        if (output.Length > 0)
        {
            _sink(Encoding.Latin1.GetBytes(output.ToString()));
        }
    }

    /// <summary>收尾:扣着的那一点遮完放出(会话结束、停止记录时)。</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_held.Length == 0)
            {
                return;
            }
            var output = new StringBuilder(_held.Length);
            MaskLines(Encoding.Latin1.GetString(_held), output);
            _held = [];
            if (output.Length > 0)
            {
                _sink(Encoding.Latin1.GetBytes(output.ToString()));
            }
        }
    }

    /// <summary>逐行遮盖;行尾(含 <c>\r\n</c>)原样保留,录制回放的行结构不变。</summary>
    private void MaskLines(string text, StringBuilder output)
    {
        int start = 0;
        while (start < text.Length)
        {
            int newline = text.IndexOf('\n', start);
            int end = newline < 0 ? text.Length : newline + 1;
            output.Append(MaskLine(text[start..end]));
            start = end;
        }
    }

    private string MaskLine(string line)
    {
        if (_inPrivateKey)
        {
            if (PrivateKeyEnd().IsMatch(line))
            {
                _inPrivateKey = false;
                return line;
            }
            // 私钥正文:整行换掉,行尾留着
            int body = line.Length - (line.EndsWith("\r\n", StringComparison.Ordinal) ? 2 : line.EndsWith('\n') ? 1 : 0);
            return body == 0 ? line : Mask + line[body..];
        }
        if (PrivateKeyBegin().IsMatch(line))
        {
            _inPrivateKey = !PrivateKeyEnd().IsMatch(line);
            return line;
        }

        // Bearer 先换:「token: Bearer abc」若先按名字=值换,值只吃到 Bearer 这个词,后面的 abc 就漏了
        line = AuthorizationScheme().Replace(line, match => match.Groups["scheme"].Value + " " + Mask);
        line = KeyValue().Replace(line, match =>
        {
            string value = match.Groups["value"].Value;
            char quote = value.Length >= 2 && value[0] is '"' or '\'' ? value[0] : '\0';
            string masked = quote == '\0' ? Mask : $"{quote}{Mask}{quote}";
            return match.Groups["key"].Value + match.Groups["sep"].Value + masked;
        });
        return PrefixedToken().Replace(line, Mask);
    }

    /// <summary>
    /// <c>名字=值</c> / <c>名字: 值</c>:名字里带口令一类的词,值是引号串或到空白、引号、逗号、分号、ESC 为止的一段。
    /// 名字与分隔符之间允许颜色序列(<c>grep --color</c> 只给匹配到的名字上色)。值为空的(<c>Password: </c> 提示)不动。
    /// </summary>
    [GeneratedRegex("""(?<key>[A-Za-z0-9_.\-]*(?:password|passwd|passphrase|secret|token|api[_\-]?key|access[_\-]?key|private[_\-]?key|credential)[A-Za-z0-9_.\-]*["']?)(?<sep>(?:\x1b\[[0-9;]*[A-Za-z])*[ \t]*[:=][ \t]*)(?<value>"[^"\r\n]+"|'[^'\r\n]+'|[^ \t\r\n"',;\x1b]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyValue();

    /// <summary>
    /// 不完整的一行正写到口令类的值、<c>Bearer</c> 凭据、带前缀的令牌或私钥块开头的中间:扣下等后半段。
    /// 停在分隔符后的空白上的(<c>Password: </c>、<c>[sudo] password for u: </c>)是在等输入的提示,不扣 —— 扣了提示要等到下一段输出才进记录。
    /// </summary>
    [GeneratedRegex("""(?:(?:password|passwd|passphrase|secret|token|api[_\-]?key|access[_\-]?key|private[_\-]?key|credential)[A-Za-z0-9_.\-]*["']?(?:\x1b\[[0-9;]*[A-Za-z])*(?:[ \t]*[:=][ \t]*["']?[^ \t\r\n"',;\x1b]+|[ \t]*[:=])|\b(?:bearer|basic)[ \t]+[^ \t\r\n]*|\b(?:AKIA|ASIA|gh[pousr]_|xox[abprs]-|sk-)[A-Za-z0-9_\-]*|-----BEGIN[A-Z0-9 ]*)$""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ValueInProgress();

    /// <remarks>
    /// 本类的正则里空白一律只认 ASCII 的空格与制表符:按 Latin-1 映射后,UTF-8 的续字节 0x85、0xA0 会被 <c>\s</c> 当成空白,
    /// 值就会在一个汉字中间断开。
    /// </remarks>
    [GeneratedRegex(@"(?<scheme>\b(?:bearer|basic))[ \t]+[A-Za-z0-9._~+/\-]{8,}=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationScheme();

    /// <summary>自带前缀、不必看上下文就认得出的令牌。</summary>
    [GeneratedRegex(@"\b(?:(?:AKIA|ASIA)[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9]{30,}|xox[abprs]-[A-Za-z0-9\-]{10,}|sk-[A-Za-z0-9_\-]{20,})\b", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixedToken();

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyBegin();

    [GeneratedRegex(@"-----END [A-Z0-9 ]*PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyEnd();
}

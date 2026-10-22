using System.Buffers;
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
/// 终端编码是 UTF-8 还是 GBK 都原样往返;词边界也只按 ASCII 算(映射之后 UTF-8 的 0xAA、0xB5、0xBA、0xC0–0xFF 在 <c>\b</c> 眼里是字母,
/// 紧挨着汉字的令牌就认不出了)。规则(大小写不敏感):
/// 名字里带 password / passwd / passphrase / secret / token / api key / access key / private key / credential 的
/// <c>名字=值</c>、<c>名字: 值</c>(含 JSON 与 YAML 的写法、JSON 里转义过的 <c>\"名字\":</c>;名字与分隔符之间允许 <c>grep --color</c>
/// 插进来的颜色序列,分隔符与值之间允许 jq、bat 这类工具给值上的色)只换值,双引号串认反斜杠转义;
/// <c>Bearer</c> / <c>Basic</c> 后面的凭据;PEM 私钥块的正文(写在一行里的,BEGIN 与 END 之间那段);几种自带前缀的令牌(AWS <c>AKIA…</c> / <c>ASIA…</c>、
/// GitHub <c>ghp_…</c> 一类、Slack <c>xox?-…</c>、OpenAI 式 <c>sk-…</c>)。宁可多遮,不放过。
/// </para>
/// <para>
/// 一段输出可能在任何字节处被切开,打字回显更是一个字节一段。当前行已经放出去的原文(大约最后 <see cref="MaxContextBytes" /> 字节)留作匹配上下文,
/// 新到的字节接在它后面把全部规则(含私钥块的开头)重跑一遍,只改写值落在还没放出去的字节里的那些匹配 ——
/// 名字、颜色序列、<c>Bea|rer</c>、<c>-----BEGI|N</c> 切在哪都认得出。上下文截断时不截在任何一个匹配的中间,吞掉的长值也不进上下文:
/// 值里像「名字: 值」的字样不会被当成新的匹配、把后面真正的名字吞掉。私钥块的开头与结尾另按原文的最后几十个字节认。
/// 名字与分隔符当场放出,只扣<b>值</b>,扣到值结束(空白、收尾的引号、行尾……)为止;
/// 值还没开始的(<c>Password:</c>、<c>Password: </c> 这类等输入的提示)一个字节也不扣,vim、top 这类不换行的全屏程序也不受影响。
/// 扣下的值有上限,超了先放一个 <c>***</c>,值剩下的部分照它自己的结束条件吞完,尾巴不会漏出去。
/// </para>
/// <para>
/// 规则全是线性的:<see cref="Write" /> 跑在桥的读循环上,<see cref="Flush" /> 在 UI 线程上,一条会回溯爆炸的正则在这里就是界面卡死。
/// </para>
/// <para>
/// 线程:<see cref="Write" /> 跑在终端读线程上;<see cref="Flush" /> 在停止记录时从 UI 线程调,而旧桥可能还在往这里写
/// (停止记录不退订,见 <c>MainWindowViewModel.StopSessionLogging</c>),两者用一把锁串起来。
/// </para>
/// </remarks>
public sealed partial class SessionOutputMasker
{
    /// <summary>扣下的值最多攒这么多字节;超了先放一个 <c>***</c>,值剩下的部分吞到它结束为止。</summary>
    internal const int MaxHeldBytes = 4096;

    /// <summary>
    /// 当前行已放出去的部分大约留多少字节作匹配上下文:名字、分隔符、颜色序列都在这个范围里。
    /// 截断点落在一个匹配中间时往前挪到它的开头(见 <see cref="Advance" />)。
    /// </summary>
    internal const int MaxContextBytes = 512;

    /// <summary>认被切开的 <c>-----BEGIN/END … PRIVATE KEY-----</c> 要留的原文字节数(吞私钥正文时末尾也先留这么多不吞)。</summary>
    private const int MarkerTailBytes = 64;

    /// <summary>遮盖后的占位。</summary>
    private const string Mask = "***";

    /// <summary>不加引号的值到这些字符为止:空白、引号、逗号、分号,以及 C0 控制字符与 DEL(OSC 标题收尾的 BEL 不能被值吞掉)。</summary>
    private static readonly SearchValues<char> PlainValueEnds =
        SearchValues.Create(" \"',;\u007f" + string.Concat(Enumerable.Range(0, 0x20).Select(c => (char)c)));

    /// <summary><c>Bearer</c> / <c>Basic</c> 凭据的字符。</summary>
    private static readonly SearchValues<char> CredentialChars =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._~+/-=");

    /// <summary>带前缀的令牌的字符。</summary>
    private static readonly SearchValues<char> TokenChars =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-");

    private readonly Action<byte[]> _sink;
    private readonly Lock _gate = new();

    /// <summary>当前行已经放出去的原文(截在匹配与匹配之间,见 <see cref="Advance" />):只作匹配上下文,不再输出。</summary>
    private string _context = string.Empty;

    /// <summary>
    /// <c>Bearer</c> 与令牌从上下文的第几个字符起扫:截断过的上下文多留截断点前的一个字符,只给它们开头的「前面不是字母数字」看
    /// (<c>xBearer …</c> 截成 <c>Bearer …</c> 不能凭空多出一个凭据)。
    /// </summary>
    private int _scanFrom;

    /// <summary>当前行处理过的原文的最后 <see cref="MarkerTailBytes" /> 个字节:认私钥块的开头与结尾用。</summary>
    private string _keyTail = string.Empty;

    /// <summary>扣着没放的原文(总在当前行里):写到一半的值,或者吞值时留着拼下一段的那几个字节。</summary>
    private string _held = string.Empty;

    /// <summary>在多行私钥块里(上一行有 BEGIN 而没有 END):整行遮,直到出现 END 的那一行。</summary>
    private bool _inPrivateKey;

    /// <summary>当前行处理过的部分里出现过 BEGIN、还没见到 END:后面到行尾都是私钥正文。</summary>
    private bool _inKeyBody;

    /// <summary>值超了上限(或者收尾)已经放过一个 <c>***</c>:剩下的部分照这种值的结束条件吞掉。</summary>
    private ValueKind _swallowing;

    /// <summary>遮完的输出交给 <paramref name="sink" />(会话日志或录制的 <c>Write</c>)。</summary>
    public SessionOutputMasker(Action<byte[]> sink) => _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    /// <summary>值的种类:决定它到哪结束,扣到上限之后据此把剩下的部分吞完。</summary>
    private enum ValueKind
    {
        None,

        /// <summary>不加引号的值:到空白、引号、逗号、分号或控制字符为止。</summary>
        Plain,

        /// <summary>双引号串(含 JSON 里转义过的 <c>\"…\"</c>):到不带转义的 <c>"</c> 或行尾为止。</summary>
        DoubleQuoted,

        /// <summary>单引号串:到 <c>'</c> 或行尾为止(shell 的单引号里反斜杠不转义)。</summary>
        SingleQuoted,

        /// <summary><c>Bearer</c> / <c>Basic</c> 的凭据。</summary>
        Credential,

        /// <summary>带前缀的令牌。</summary>
        Token,

        /// <summary>同一行里 BEGIN 之后的私钥正文:到 END 或行尾为止。</summary>
        KeyBody,

        /// <summary>多行私钥块里的一行:到 END 或行尾为止。</summary>
        KeyLine
    }

    /// <summary>收下一段输出(桥的 <c>DataReceived</c>)。</summary>
    public void Write(byte[] chunk)
    {
        if (chunk.Length == 0)
        {
            return;
        }
        lock (_gate)
        {
            Process(Encoding.Latin1.GetString(chunk), final: false);
        }
    }

    /// <summary>收尾:扣着的那一点遮完放出(会话结束、停止记录时)。</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_held.Length > 0)
            {
                Process(string.Empty, final: true);
            }
        }
    }

    /// <summary>
    /// 接着扣着的部分处理新到的输出,逐行放出;<paramref name="final" /> 时不再扣,写到一半的值当场遮完。
    /// 行尾(含 <c>\r\n</c>)原样保留,录制回放的行结构不变。
    /// </summary>
    private void Process(string text, bool final)
    {
        string data = _held + text;
        _held = string.Empty;
        var output = new StringBuilder(data.Length);
        int start = 0;
        while (start < data.Length)
        {
            int newline = data.IndexOf('\n', start);
            int end = newline < 0 ? data.Length : newline + 1;
            Segment(data[start..end], complete: newline >= 0, final, output);
            start = end;
        }
        if (output.Length > 0)
        {
            _sink(Encoding.Latin1.GetBytes(output.ToString()));
        }
    }

    /// <summary>当前行新到的一段:从已放出处起,到行尾(<paramref name="complete" />)或已收到内容的末尾。</summary>
    private void Segment(string segment, bool complete, bool final, StringBuilder output)
    {
        if (_swallowing != ValueKind.None)
        {
            int end = SwallowEnd(segment, out int keep);
            if (end < 0)
            {
                // 值还没完:吞掉;末尾几个字节先留着,下一段来了拼起来再看(收尾时一并丢掉)
                Consume(segment[..^keep]);
                _held = final ? string.Empty : segment[^keep..];
                return;
            }
            if ((_swallowing is ValueKind.DoubleQuoted or ValueKind.SingleQuoted) && segment[end] is not ('\r' or '\n'))
            {
                // 收尾的引号算在值里,由这里放出,遮完的样子是完整的 "***"
                output.Append(segment[end]);
                end++;
            }
            Consume(segment[..end]);
            _swallowing = ValueKind.None;
            segment = segment[end..];
            // 上下文从值的结尾重新起:吞掉的值里像「名字: 值」的字样不能再拿来匹配
            _context = string.Empty;
            _scanFrom = 0;
        }
        if (_inPrivateKey)
        {
            KeyLine(segment, complete, final, output);
        }
        else
        {
            Line(segment, complete, final, output);
        }
    }

    /// <summary>平常的一行:上下文接上新到的一段跑全部规则,放出已经定了的部分;伸到末尾、后面可能还有的值扣下。</summary>
    private void Line(string segment, bool complete, bool final, StringBuilder output)
    {
        string line = _context + segment;
        int from = _context.Length;
        var markers = new List<(int End, bool Begin)>();
        var matches = new List<(int Start, int End)>();
        List<Secret> secrets = FindSecrets(line, from, complete, markers, matches);
        int pos = from;
        foreach (Secret secret in secrets)
        {
            // 开头已经放出去的(带前缀的令牌被切在前缀里):只遮还没放的那部分
            int start = Math.Max(secret.Start, from);
            output.Append(line, pos, start - pos);
            if (secret.Open)
            {
                if (!final && line.Length - start < MaxHeldBytes)
                {
                    Advance(line, from, start, markers, matches);
                    _held = line[start..];
                    return;
                }
                // 收尾或者扣到上限:不再等,先遮,值剩下的部分(包括之后才到的)照它的结束条件吞掉。
                // 从伸到末尾的那个值自己的正文(引号之后)吞起,结束条件才对得上;前面的部分一并算作遮掉了
                output.Append(secret.Start < from ? string.Empty : secret.Quote).Append(Mask);
                int content = Math.Max(secret.ContentStart, from);
                Advance(line, from, content, markers, matches);
                _swallowing = secret.Kind;
                Segment(line[content..], complete: false, final, output);
                return;
            }
            output.Append(secret.Start < from ? Mask : secret.Quote + Mask + secret.Quote);
            pos = secret.End;
        }
        output.Append(line, pos, line.Length - pos);
        if (complete)
        {
            // 有 BEGIN 而到行尾都没见到 END:下一行起按私钥块整行遮
            _inPrivateKey = markers.Count > 0 ? markers[^1].Begin : _inKeyBody;
            _inKeyBody = false;
            _context = string.Empty;
            _scanFrom = 0;
            _keyTail = string.Empty;
        }
        else
        {
            Advance(line, from, line.Length, markers, matches);
        }
    }

    /// <summary>
    /// 当前行处理到 <paramref name="end" /> 为止:之前的原文留作上下文,原文也接进认私钥块用的尾巴,私钥块的开头与结尾记到这里为止。
    /// </summary>
    /// <remarks>
    /// 上下文只留最后 <see cref="MaxContextBytes" /> 个字节左右,但截断点不能落在任何一个匹配(名字 … 值)的中间:
    /// 从一个长值的中间截起,值里像 <c>Password:"</c> 的字样会被当成新的名字与引号,把后面真正的名字吞进去当值,
    /// 真正的值就漏了。落在中间就往前挪到那个匹配的开头 —— 从那里重新扫,与整行一起扫的结果一致。
    /// 截断点前再多留一个字符,见 <see cref="_scanFrom" />。
    /// </remarks>
    private void Advance(string line, int from, int end, List<(int End, bool Begin)> markers, List<(int Start, int End)> matches)
    {
        foreach ((int markerEnd, bool begin) in markers)
        {
            if (markerEnd <= end)
            {
                _inKeyBody = begin;
            }
        }
        int keep = Math.Max(_scanFrom, end - MaxContextBytes);
        for (bool moved = true; moved;)
        {
            moved = false;
            foreach ((int start, int stop) in matches)
            {
                if (start < keep && keep < stop)
                {
                    keep = start;
                    moved = true;
                }
            }
        }
        int contextStart = 0;
        if (keep > _scanFrom)
        {
            contextStart = keep - 1;
            _scanFrom = 1;
        }
        _context = line[contextStart..end];
        _keyTail = KeyTail(_keyTail + line[from..end]);
    }

    /// <summary>多行私钥块里的一行:整行换成 <c>***</c>(行尾留着);出现 END 的那一行原样放出,之后回到平常。</summary>
    private void KeyLine(string segment, bool complete, bool final, StringBuilder output)
    {
        if (segment.Length == 0)
        {
            return;
        }
        if (FirstKeyEnd(segment, segment.Length) is { } end)
        {
            int stop = end.Index + end.Length;
            output.Append(segment, 0, stop);
            _inPrivateKey = false;
            _inKeyBody = false;
            _context = string.Empty;
            _scanFrom = 0;
            _keyTail = KeyTail(segment[..stop]);
            Line(segment[stop..], complete, final, output);
            return;
        }
        if (complete)
        {
            int body = segment.Length - (segment.EndsWith("\r\n", StringComparison.Ordinal) ? 2 : 1);
            output.Append(body == 0 ? segment : Mask + segment[body..]);
            _context = string.Empty;
            _scanFrom = 0;
            _keyTail = string.Empty;
            return;
        }
        // 还不知道是不是 END 那一行:整行扣着;收尾或者扣到上限就先遮,这一行剩下的吞到 END 或行尾为止
        if (!final && segment.Length < MaxHeldBytes)
        {
            _held = segment;
            return;
        }
        output.Append(Mask);
        _swallowing = ValueKind.KeyLine;
        Segment(segment, complete: false, final, output);
    }

    /// <summary>
    /// 一行里要遮的片段,按起点排好、重叠的并成一段。只要结尾落在 <paramref name="from" /> 之后的(之前的已经处理过了);
    /// 行还没完时伸到末尾的值记为未完(后面可能还有)。顺带记下 <paramref name="from" /> 之后才完整的私钥块开头与结尾,
    /// 以及每个匹配从名字到值的整个范围(截上下文时用)。
    /// </summary>
    private List<Secret> FindSecrets(string line, int from, bool complete, List<(int End, bool Begin)> markers, List<(int Start, int End)> matches)
    {
        var found = new List<Secret>();
        for (Match match = AuthorizationScheme().Match(line, _scanFrom); match.Success; match = match.NextMatch())
        {
            matches.Add((match.Index, match.Index + match.Length));
            Group value = match.Groups["value"];
            Add(found, line, from, complete, value.Index, value.Index + value.Length, ValueKind.Credential, string.Empty, closed: false);
        }
        // 名字=值从上下文的第一个字符扫起:截断点落在一串标识符中间时,后半截照样当名字认(值是同一个);
        // 认 Bearer 与令牌时截断点前那个字符只给开头的「前面不是字母数字」看,不当成能起头的位置
        for (Match match = KeyValue().Match(line); match.Success; match = match.NextMatch())
        {
            matches.Add((match.Index, match.Index + match.Length));
            Group value = match.Groups["value"];
            char first = line[value.Index];
            string quote = first == '\\' && value.Length > 1 && line[value.Index + 1] == '"' ? "\\\""
                : first == '"' ? "\""
                : first == '\'' ? "'"
                : string.Empty;
            ValueKind kind = quote.Length == 0 ? ValueKind.Plain : quote == "'" ? ValueKind.SingleQuoted : ValueKind.DoubleQuoted;
            // 收了尾的引号串不会再长;没收尾的(open 那一支)与不加引号的,伸到末尾就是还没完
            bool closed = quote.Length > 0 && !match.Groups["open"].Success;
            Add(found, line, from, complete, value.Index, value.Index + value.Length, kind, quote, closed);
        }
        for (Match match = PrefixedToken().Match(line, _scanFrom); match.Success; match = match.NextMatch())
        {
            matches.Add((match.Index, match.Index + match.Length));
            Add(found, line, from, complete, match.Index, match.Index + match.Length, ValueKind.Token, string.Empty, closed: false);
        }

        // 私钥正文:BEGIN 之后到同一行的 END 为止,没有 END 就到行尾。开头与结尾按原文认(原文的尾巴接上新到的一段,
        // 位置换算回 line 上);已经处理过的那部分里的 BEGIN 记在 _inKeyBody 上
        string keyText = _keyTail + line[from..];
        int shift = from - _keyTail.Length;
        bool inBody = _inKeyBody;
        int bodyStart = from;
        for (Match match = PrivateKeyMarker().Match(keyText); match.Success; match = match.NextMatch())
        {
            int end = match.Index + match.Length + shift;
            if (end <= from)
            {
                continue;
            }
            bool begin = match.Groups["begin"].Success;
            markers.Add((end, begin));
            if (begin && !inBody)
            {
                inBody = true;
                bodyStart = end;
            }
            else if (!begin && inBody)
            {
                AddKeyBody(found, line, from, complete, bodyStart, match.Index + shift);
                inBody = false;
            }
        }
        if (inBody)
        {
            int contentEnd = complete ? line.Length - (line.EndsWith("\r\n", StringComparison.Ordinal) ? 2 : 1) : line.Length;
            AddKeyBody(found, line, from, complete, bodyStart, contentEnd);
        }
        return Merge(found);
    }

    private static void Add(List<Secret> found, string line, int from, bool complete, int start, int end, ValueKind kind, string quote, bool closed)
    {
        if (end > from && end > start)
        {
            found.Add(new Secret(start, end, kind, quote, Open: !complete && !closed && end == line.Length, ContentStart: start + quote.Length));
        }
    }

    /// <summary>
    /// 私钥正文,从 BEGIN 之后第一个非空白字符算起;只有空白的不算(BEGIN 那一行通常就是这样)。
    /// 开头的空白不遮:切开写入时它们先到、已经放出去了,一次写入也得是同一个结果。
    /// </summary>
    private static void AddKeyBody(List<Secret> found, string line, int from, bool complete, int start, int end)
    {
        int text = end > start ? line.AsSpan(start, end - start).IndexOfAnyExcept(' ', '\t', '\r') : -1;
        if (text >= 0)
        {
            Add(found, line, from, complete, start + text, end, ValueKind.KeyBody, string.Empty, closed: false);
        }
    }

    /// <summary>
    /// 按起点排好;重叠的并成一段:套在里面的归外面那段遮,伸出去的按并集遮成 <c>***</c>。
    /// 结束条件(种类与正文起点)跟着决定结尾的那个值走:伸得最远的,或者套在一个已收尾的值里、自己却还没完的那个
    /// (<c>"…-----BEGIN … KEY-----…"</c> 停在收尾引号上时,引号收了尾,私钥正文却可能还长)。
    /// 扣到上限时从它的正文吞起,吞的过程里就不会碰到它自己的结束条件。
    /// </summary>
    private static List<Secret> Merge(List<Secret> found)
    {
        if (found.Count < 2)
        {
            return found;
        }
        found.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
        var merged = new List<Secret>(found.Count) { found[0] };
        for (int i = 1; i < found.Count; i++)
        {
            Secret next = found[i];
            Secret last = merged[^1];
            if (next.Start >= last.End)
            {
                merged.Add(next);
            }
            else
            {
                merged[^1] = next.End > last.End || (next.Open && !last.Open)
                    ? next with { Start = last.Start, End = Math.Max(last.End, next.End), Quote = string.Empty }
                    : last;
            }
        }
        return merged;
    }

    /// <summary>
    /// 吞掉的那部分也算当前行处理过了:它不进上下文(值吞完时上下文从头起),只把原文接进认私钥块用的尾巴,
    /// 里面的私钥块开头与结尾照记。
    /// </summary>
    private void Consume(string text)
    {
        if (text.Length == 0)
        {
            return;
        }
        string keyText = _keyTail + text;
        if (!_inPrivateKey)
        {
            for (Match match = PrivateKeyMarker().Match(keyText); match.Success; match = match.NextMatch())
            {
                if (match.Index + match.Length > _keyTail.Length)
                {
                    _inKeyBody = match.Groups["begin"].Success;
                }
            }
        }
        _keyTail = KeyTail(keyText);
    }

    /// <summary>
    /// 正在吞的值在 <paramref name="text" /> 里到哪为止(结束它的那个字符不吞);-1 表示还没完,
    /// 这时末尾 <paramref name="keep" /> 个字节先留着,下一段来了拼起来再看(切在半截的转义、被切开的 END)。
    /// </summary>
    private int SwallowEnd(string text, out int keep)
    {
        keep = 0;
        switch (_swallowing)
        {
            case ValueKind.Plain:
                return text.AsSpan().IndexOfAny(PlainValueEnds);
            case ValueKind.Credential:
                return text.AsSpan().IndexOfAnyExcept(CredentialChars);
            case ValueKind.Token:
                return text.AsSpan().IndexOfAnyExcept(TokenChars);
            case ValueKind.SingleQuoted:
                return text.AsSpan().IndexOfAny('\'', '\r', '\n');
            case ValueKind.DoubleQuoted:
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (c is '"' or '\r' or '\n')
                    {
                        return i;
                    }
                    if (c == '\\')
                    {
                        if (i + 1 == text.Length)
                        {
                            keep = 1;
                            return -1;
                        }
                        if (text[i + 1] is not ('\r' or '\n'))
                        {
                            i++;
                        }
                    }
                }
                return -1;
            default:
                // 私钥正文:到 END 或行尾(\r\n / \n)为止;行中间的 \r 照吞
                int newline = text.IndexOf('\n');
                int lineEnd = newline > 0 && text[newline - 1] == '\r' ? newline - 1 : newline;
                if (FirstKeyEnd(text, lineEnd < 0 ? text.Length : lineEnd) is { } end)
                {
                    return end.Index;
                }
                if (lineEnd >= 0)
                {
                    return lineEnd;
                }
                keep = Math.Min(MarkerTailBytes, text.Length);
                return -1;
        }
    }

    /// <summary><paramref name="text" /> 前 <paramref name="limit" /> 个字符里的第一个 <c>-----END … PRIVATE KEY-----</c>。</summary>
    private static Match? FirstKeyEnd(string text, int limit)
    {
        for (Match match = PrivateKeyMarker().Match(text, 0, limit); match.Success; match = match.NextMatch())
        {
            if (!match.Groups["begin"].Success)
            {
                return match;
            }
        }
        return null;
    }

    /// <summary>认私钥块用的原文只留最后 <see cref="MarkerTailBytes" /> 个字节。</summary>
    private static string KeyTail(string text) => text.Length <= MarkerTailBytes ? text : text[^MarkerTailBytes..];

    /// <summary>
    /// <c>名字=值</c> / <c>名字: 值</c>:名字里带口令一类的词,值是引号串(双引号认反斜杠转义,含 JSON 里转义过的 <c>\"…\"</c>)
    /// 或到空白、引号、逗号、分号、控制字符为止的一段。名字与分隔符之间允许颜色序列(<c>grep --color</c> 只给匹配到的名字上色),
    /// 分隔符与值之间也允许(jq、bat 给值上色)。值为空的(<c>Password: </c> 提示)不动。
    /// 已收到内容的末尾停在一个没收尾的引号串里时走 <c>open</c> 那一支,调用方据此把它当成没完的值扣下。
    /// </summary>
    /// <remarks>
    /// 名字从一串标识符字符的开头算起,先找到第一个关键词、再把整串吃完,两步都是原子的,值的各支也互不重叠 —— 整条是线性的。
    /// 旧写法在关键词前后各挂一个可回溯的 <c>[A-Za-z0-9_.\-]*</c>,4 KB 的 <c>tokentoken…</c> 就能把它拖上半分钟。
    /// 转义过的 JSON 串里,<c>\\\"</c> 与 <c>\\\\</c> 是里层的转义,不算收尾。
    /// </remarks>
    [GeneratedRegex("""(?<![A-Za-z0-9_.\-])(?>[A-Za-z0-9_.\-]*?(?:password|passwd|passphrase|secret|token|api[_\-]?key|access[_\-]?key|private[_\-]?key|credential))(?>[A-Za-z0-9_.\-]*)(?:\\?["'])?(?>(?:\x1b\[[0-9;]*[A-Za-z])*[ \t]*)[:=](?>(?:[ \t]|\x1b\[[0-9;]*m)*)(?<value>"(?:[^"\\\r\n]|\\[^\r\n])+"|'[^'\r\n]+'|\\"(?:[^"\\\r\n]|\\[^"\\\r\n]|\\\\(?:[^"\\\r\n]|\\[^\r\n]))+\\"|(?<open>"(?:[^"\\\r\n]|\\[^\r\n])*\\?|'[^'\r\n]*|\\"(?:[^"\\\r\n]|\\[^"\\\r\n]|\\\\(?:[^"\\\r\n]|\\[^\r\n]))*\\{0,3})\z|[^ "',;\x00-\x1f\x7f]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyValue();

    /// <summary><c>Bearer</c> / <c>Basic</c> 后面的凭据;已收到内容的末尾停在凭据里(哪怕还不够 8 个字符)也算,调用方据此扣下。</summary>
    /// <remarks>
    /// 本类的正则里空白一律只认 ASCII 的空格与制表符:按 Latin-1 映射后,UTF-8 的续字节 0x85、0xA0 会被 <c>\s</c> 当成空白,
    /// 值就会在一个汉字中间断开。
    /// </remarks>
    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?:bearer|basic)(?>[ \t]+)(?<value>[A-Za-z0-9._~+/\-]{8,}=*|[A-Za-z0-9._~+/\-]+=*\z)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationScheme();

    /// <summary>自带前缀、不必看上下文就认得出的令牌;已收到内容的末尾停在一个前缀已齐的令牌里也算,调用方据此扣下。</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?:(?:(?:AKIA|ASIA)[0-9A-Z]{16}|gh[pousr]_(?>[A-Za-z0-9]{30,})|xox[abprs]-[A-Za-z0-9\-]{10,}|sk-[A-Za-z0-9_\-]{20,})(?![A-Za-z0-9_])|(?:(?:AKIA|ASIA)[0-9A-Z]*|gh[pousr]_[A-Za-z0-9]*|xox[abprs]-[A-Za-z0-9\-]*|sk-[A-Za-z0-9_\-]*)\z)",
        RegexOptions.CultureInvariant)]
    private static partial Regex PrefixedToken();

    /// <summary>PEM 私钥块的开头(<c>begin</c> 组)或结尾。</summary>
    [GeneratedRegex(@"-----(?:(?<begin>BEGIN)|END) [A-Z0-9 ]*PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyMarker();

    /// <summary>
    /// 一处要遮的片段 <c>[Start, End)</c>;<see cref="Open" /> 表示它伸到了已收到内容的末尾,后面可能还有。
    /// <see cref="Quote" /> 是遮完要留着的引号,<see cref="ContentStart" /> 是决定结尾的那个值的正文从哪开始(开头的引号之后)。
    /// </summary>
    private readonly record struct Secret(int Start, int End, ValueKind Kind, string Quote, bool Open, int ContentStart);
}

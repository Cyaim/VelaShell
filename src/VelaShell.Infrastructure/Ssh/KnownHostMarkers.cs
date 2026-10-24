using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Ssh.HostKeys;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 信任库里的受信主机 CA 与吊销记录(<see cref="KnownHostMarker" />)与 OpenSSH <c>known_hosts</c> 那两种标记行之间的换算,
/// 以及「手动添加一个 CA」时的校验。
/// </summary>
/// <remarks>
/// 匹配与验证都不在这里:记录换成 SSH 库的 <see cref="KnownHostEntry" /> 之后交给 <see cref="KnownHostsFile.Lookup(IReadOnlyList{KnownHostEntry}, string, int, SshPublicKey)" />,
/// 取反模式、散列主机名、证书的类型 / 签名 / 有效期 / 主体 / 关键选项都按库的那一套(velashell-docs/zh/ssh/spec/03 §5.4、§5.5),
/// 宿主不另写一份。
/// </remarks>
public static partial class KnownHostMarkers
{
    /// <summary><c>known_hosts</c> 的受信 CA 标记(sshd(8) 的 SSH_KNOWN_HOSTS 一节)。</summary>
    public const string CertificateAuthorityTag = "@cert-authority";

    /// <summary><c>known_hosts</c> 的吊销标记。</summary>
    public const string RevokedTag = "@revoked";

    /// <summary>RSA 的 CA 至少要多少位(与库验主机证书时的下限一致,低于它签出的证书反正验不过)。</summary>
    private const int MinimumRsaCaBits = 2048;

    /// <summary>非 22 端口的模式:<c>[主机]:端口</c>(主机里允许通配)。</summary>
    [GeneratedRegex(@"^\[[^\[\]]+\]:\d{1,5}$", RegexOptions.CultureInvariant)]
    private static partial Regex BracketedPattern { get; }

    /// <summary>把信任库里的记录换成库的条目;公钥读不出来(被手改坏了)的跳过。</summary>
    /// <remarks>行号按顺序编 1 起,库只拿它进诊断文本。</remarks>
    public static IReadOnlyList<KnownHostEntry> ToEntries(IEnumerable<KnownHostMarker> markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        List<KnownHostEntry> entries = [];
        foreach (KnownHostMarker marker in markers)
        {
            byte[] blob;
            try
            {
                blob = Convert.FromBase64String(marker.PublicKey);
            }
            catch (FormatException)
            {
                continue;
            }
            bool hashed = marker.HostPatterns.StartsWith("|1|", StringComparison.Ordinal);
            entries.Add(new KnownHostEntry(
                hashed ? [marker.HostPatterns] : [.. marker.HostPatterns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
                hashed,
                marker.Kind == KnownHostMarkerKind.CertificateAuthority ? CertificateAuthorityTag : RevokedTag,
                marker.KeyType,
                blob,
                entries.Count + 1));
        }
        return entries;
    }

    /// <summary>从 <c>known_hosts</c> 的一条标记行建一条记录(导入用);不是标记行时返回 <see langword="null" />。</summary>
    /// <param name="entry">库解析出的条目。</param>
    /// <param name="comment">那一行行尾的注释;没有为空。</param>
    public static KnownHostMarker? FromEntry(KnownHostEntry entry, string? comment = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.IsCertificateAuthority && !entry.IsRevoked)
        {
            return null;
        }
        string fingerprint;
        try
        {
            fingerprint = SshPublicKey.Decode(entry.KeyBlob).Sha256Fingerprint;
        }
        catch (SshPublicKeyException)
        {
            // 本库认不得的类型照样记下:吊销一把认不得的钥没有坏处,认不得的 CA 也只是永远对不上。
            fingerprint = string.Empty;
        }
        return new KnownHostMarker
        {
            Kind = entry.IsCertificateAuthority ? KnownHostMarkerKind.CertificateAuthority : KnownHostMarkerKind.Revoked,
            HostPatterns = string.Join(',', entry.Patterns),
            KeyType = entry.KeyType,
            PublicKey = Convert.ToBase64String(entry.KeyBlob.Span),
            Fingerprint = fingerprint,
            Comment = SanitizeComment(comment),
        };
    }

    /// <summary>写成一行 <c>known_hosts</c>(导出用):<c>@cert-authority 模式 类型 base64 [注释]</c>。</summary>
    public static string FormatLine(KnownHostMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        string tag = marker.Kind == KnownHostMarkerKind.CertificateAuthority ? CertificateAuthorityTag : RevokedTag;
        string line = string.Create(CultureInfo.InvariantCulture, $"{tag} {marker.HostPatterns} {marker.KeyType} {marker.PublicKey}");
        string comment = SanitizeComment(marker.Comment);
        return comment.Length == 0 ? line : $"{line} {comment}";
    }

    /// <summary>
    /// 手动添加:由主机名模式与一行公钥建一条记录;不成立时给出界面语言的原因。
    /// </summary>
    /// <param name="patterns">主机名模式(逗号分隔,可带 <c>*</c> <c>?</c> 通配、<c>!</c> 取反、<c>[host]:port</c>)。</param>
    /// <param name="keyText">
    /// CA 的公钥行(<c>类型 base64 [注释]</c>,即 CA 的 <c>.pub</c> 文件),或者从 <c>known_hosts</c> 里整行复制来的
    /// <c>@cert-authority 模式 类型 base64</c> / <c>@revoked …</c>。整行时模式框空着就用那一行的模式。
    /// </param>
    /// <param name="comment">说明;空着时用公钥行自带的注释。</param>
    /// <param name="marker">建出的记录。</param>
    /// <param name="error">不成立时的原因。</param>
    public static bool TryCreate(
        string? patterns, string? keyText, string? comment,
        [NotNullWhen(true)] out KnownHostMarker? marker, [NotNullWhen(false)] out string? error)
    {
        marker = null;
        string text = (keyText ?? string.Empty).Trim();
        string chosenPatterns = (patterns ?? string.Empty).Trim();
        string chosenComment = (comment ?? string.Empty).Trim();
        KnownHostMarkerKind kind = KnownHostMarkerKind.CertificateAuthority;
        bool hashedFromLine = false;
        SshPublicKey? key;

        if (text.StartsWith('@'))
        {
            // 整行复制来的标记行:库解析(认不出的标记整行跳过,与读 known_hosts 时一样)。
            IReadOnlyList<KnownHostEntry> parsed = KnownHostsFile.Parse(text);
            if (parsed is not [{ } entry] || (!entry.IsCertificateAuthority && !entry.IsRevoked)
                || !TryDecode(entry.KeyBlob, out key))
            {
                error = Strings.Get("HostCa_ErrKeyInvalid");
                return false;
            }
            kind = entry.IsCertificateAuthority ? KnownHostMarkerKind.CertificateAuthority : KnownHostMarkerKind.Revoked;
            if (chosenPatterns.Length == 0)
            {
                chosenPatterns = string.Join(',', entry.Patterns);
                hashedFromLine = entry.IsHashed;
            }
            if (chosenComment.Length == 0)
            {
                chosenComment = CommentOf(text, fieldsBefore: 4);
            }
        }
        else
        {
            if (!SshPublicKey.TryParse(text, out key))
            {
                error = Strings.Get("HostCa_ErrKeyInvalid");
                return false;
            }
            if (chosenComment.Length == 0)
            {
                chosenComment = CommentOf(text, fieldsBefore: 2);
            }
        }

        // 散列过的模式只可能来自整行复制(库认出了 |1|salt|hash),原样收下;模式框里手填的照常校验。
        if (!hashedFromLine && ValidatePatterns(chosenPatterns) is { } patternError)
        {
            error = patternError;
            return false;
        }
        if (!hashedFromLine)
        {
            // 模式之间的空白去掉:那一栏写回 known_hosts 时空白会把它切成两栏。
            chosenPatterns = string.Join(',', chosenPatterns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        }

        if (kind == KnownHostMarkerKind.CertificateAuthority)
        {
            // CA 公钥本身不能是证书(规范里没有证书链),RSA 的 CA 低于 2048 位签出的证书库验不过 —— 加进来只会让人以为配好了。
            if (key.IsCertificate)
            {
                error = Strings.Get("HostCa_ErrKeyIsCertificate");
                return false;
            }
            if (key.PlainKeyType == "ssh-rsa" && key.KeyBits < MinimumRsaCaBits)
            {
                error = Strings.Format("HostCa_ErrRsaTooShort", key.KeyBits);
                return false;
            }
        }

        marker = new KnownHostMarker
        {
            Kind = kind,
            HostPatterns = chosenPatterns,
            KeyType = key.KeyType,
            PublicKey = Convert.ToBase64String(key.Blob.Span),
            Fingerprint = key.Sha256Fingerprint,
            Comment = SanitizeComment(chosenComment),
        };
        error = null;
        return true;
    }

    /// <summary>校验一栏主机名模式;成立返回 <see langword="null" />,否则返回界面语言的原因。</summary>
    /// <remarks>
    /// 只拦会让这一行的意思变掉的写法:模式里的空白(<c>known_hosts</c> 按空白分栏)、<c>#</c>(注释)、开头的 <c>@</c> / <c>|</c>
    /// (标记与散列行)、方括号写错的端口;全是取反模式的一栏一台主机也对不上,同样拦下。通配本身合法 ——
    /// <c>*.corp.example</c> 正是 CA 最常见的写法。
    /// </remarks>
    public static string? ValidatePatterns(string? patterns)
    {
        string[] parts = (patterns ?? string.Empty).Split(',', StringSplitOptions.TrimEntries);
        if (parts.All(static part => part.Length == 0))
        {
            return Strings.Get("HostCa_ErrPatternsEmpty");
        }

        bool anyPositive = false;
        foreach (string part in parts.Where(static p => p.Length > 0))
        {
            string body = part.StartsWith('!') ? part[1..] : part;
            if (body.Length == 0
                || body.Any(static c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '#' or ',')
                || body[0] is '@' or '|' or '!'
                || (body.Contains('[', StringComparison.Ordinal) || body.Contains(']', StringComparison.Ordinal)) && !BracketedPattern.IsMatch(body))
            {
                return Strings.Format("HostCa_ErrPatternInvalid", part);
            }
            anyPositive |= !part.StartsWith('!');
        }

        return anyPositive ? null : Strings.Get("HostCa_ErrPatternsOnlyNegated");
    }

    /// <summary>一行里第 <paramref name="fieldsBefore" /> 栏之后的部分(注释);没有时为空。</summary>
    internal static string CommentOf(string line, int fieldsBefore)
    {
        string[] parts = line.Trim().Split([' ', '\t'], fieldsBefore + 1, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > fieldsBefore ? parts[fieldsBefore].Trim() : string.Empty;
    }

    /// <summary>注释写回 <c>known_hosts</c> 时只能占一行。</summary>
    private static string SanitizeComment(string? comment) =>
        string.Join(' ', (comment ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool TryDecode(ReadOnlyMemory<byte> blob, [NotNullWhen(true)] out SshPublicKey? key)
    {
        try
        {
            key = SshPublicKey.Decode(blob);
            return true;
        }
        catch (SshPublicKeyException)
        {
            key = null;
            return false;
        }
    }
}

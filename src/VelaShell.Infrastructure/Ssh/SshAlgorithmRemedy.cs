using System.Runtime.CompilerServices;
using VelaShell.Core.Models;
using VelaShell.Ssh.Diagnostics;

namespace VelaShell.Infrastructure.Ssh;

/// <summary>
/// 算法协商失败时的一键补救:对端提供、本版实现了、只是没在我们清单里的那几个算法,
/// 写进那一跳连接配置的自定义清单(<see cref="SshSessionOptions" />)。
/// </summary>
/// <remarks>
/// <para>
/// 诊断(<c>SshInterop</c> 的协商失败说明)早就点名了这几个、指到连接配置;这里把「去配置页翻开关」省掉。
/// ⚠️ <b>只放开诊断点名的那几个</b>,不开「允许老算法」那个总开关:那个开关一次放开 SHA-1 的密钥交换、主机密钥、MAC
/// 再加 SHA-1 的 RSA 用户签名,而谈不成的往往只是其中一类。两边用同一份计算(<see cref="Enableable" />),
/// 按钮写进去的与诊断说的一个不多、一个不少。
/// </para>
/// <para>
/// 写法用 OpenSSH 的 <c>+</c> 追加(§134 的自定义清单):默认清单之后才轮到它们,对端一有更新的就不会选中。
/// </para>
/// </remarks>
/// <param name="Kind">哪一类清单。</param>
/// <param name="Algorithms">要放开的算法(对端给出的顺序)。</param>
/// <param name="HopDepth">谈不成的是哪一跳:0 是终点,1 是终点的跳板(配置里的「跳板主机」),2 是跳板的跳板……</param>
/// <param name="HopTarget">那一跳(<c>用户@主机:端口</c>);终点时为 <see langword="null" />。</param>
public sealed record SshAlgorithmRemedy(SshAlgorithmKind Kind, IReadOnlyList<string> Algorithms, int HopDepth = 0, string? HopTarget = null)
{
    /// <summary>跳板上谈不成时记下是哪一跳:库会把它包进「连不上跳板」的异常里往外传,对象本身不变。</summary>
    private static readonly ConditionalWeakTable<SshNegotiationException, HopTag> HopTags = [];

    private sealed record HopTag(int Depth, string Target);

    /// <summary>从一次连接失败里找出能一键补救的那一份;不是协商失败、或者对端的算法本版一个都没实现时为 <see langword="null" />。</summary>
    /// <param name="error">连接失败(宿主翻译过的也行:原始异常在 <see cref="Exception.InnerException" /> 链上)。</param>
    public static SshAlgorithmRemedy? Find(Exception? error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is not SshNegotiationException negotiation)
            {
                continue;
            }
            if (Enableable(negotiation) is not ({ } kind, { Count: > 0 } algorithms))
            {
                return null;
            }
            return HopTags.TryGetValue(negotiation, out HopTag? tag)
                ? new(kind, algorithms, tag.Depth, tag.Target)
                : new(kind, algorithms);
        }
        return null;
    }

    /// <summary>
    /// 对端提供、本版实现了、只是不在我们这次报出去的清单里的算法(对端的顺序,去重);
    /// 压缩一类没有补救(总有 <c>none</c> 可谈),类别为 <see langword="null" />。
    /// </summary>
    /// <remarks>协商失败的诊断(<c>SshInterop</c>)与一键补救共用这一份,两边点名的永远是同一组。</remarks>
    internal static (SshAlgorithmKind? Kind, IReadOnlyList<string> Algorithms) Enableable(SshNegotiationException ex)
    {
        SshAlgorithmKind? kind = ex.Category switch
        {
            SshNegotiationCategory.KeyExchange => SshAlgorithmKind.KeyExchange,
            SshNegotiationCategory.HostKey => SshAlgorithmKind.HostKey,
            SshNegotiationCategory.EncryptionClientToServer or SshNegotiationCategory.EncryptionServerToClient =>
                SshAlgorithmKind.Cipher,
            SshNegotiationCategory.MacClientToServer or SshNegotiationCategory.MacServerToClient => SshAlgorithmKind.Mac,
            _ => null,
        };
        if (kind is not { } k)
        {
            return (null, []);
        }
        IReadOnlyList<string> available = SshAlgorithmPreferences.Available(k);
        return (k,
        [
            .. ex.OfferedByPeer
                .Where(a => available.Contains(a, StringComparer.Ordinal) && !ex.OfferedByUs.Contains(a, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal)
        ]);
    }

    /// <summary>跳板上的协商失败记下是哪一跳(只记,不拦:在异常过滤器里调,永远返回 <see langword="false" />)。</summary>
    internal static bool TagHop(SshNegotiationException ex, int depth, string target)
    {
        if (depth > 0)
        {
            HopTags.AddOrUpdate(ex, new HopTag(depth, target));
        }
        return false;
    }

    /// <summary>把这几个算法写进一份连接配置的 SSH 选项;返回新的一份,传入的不改。</summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item>那一类还没写过清单 → <c>+a,b</c>(追加在默认之后)。</item>
    ///   <item>已经是 <c>+…</c> 或整份替换的清单 → 接在后面,已经有的不重复写。</item>
    ///   <item>是 <c>-…</c> 或 <c>^…</c> → 一行里接不上第二种前缀,写成此刻生效的完整清单再接上它们。
    ///     ⚠️ 若删掉的正是对端只剩的那个(为缓解 Terrapin 删了 chacha20),这里会把它加回来 —— 确认框会点名,由用户决定。</item>
    /// </list>
    /// 「允许老算法」开关不动,其余三类清单不动。
    /// </remarks>
    public SshSessionOptions ApplyTo(SshSessionOptions? options)
    {
        SshSessionOptions result = options?.Clone() ?? new SshSessionOptions();
        string merged = Merge(Kind, ListOf(result, Kind), result.LegacyAlgorithms, Algorithms);
        switch (Kind)
        {
            case SshAlgorithmKind.KeyExchange:
                result.KexAlgorithms = merged;
                break;
            case SshAlgorithmKind.HostKey:
                result.HostKeyAlgorithms = merged;
                break;
            case SshAlgorithmKind.Cipher:
                result.Ciphers = merged;
                break;
            default:
                result.Macs = merged;
                break;
        }
        return result;
    }

    private static string? ListOf(SshSessionOptions options, SshAlgorithmKind kind) => kind switch
    {
        SshAlgorithmKind.KeyExchange => options.KexAlgorithms,
        SshAlgorithmKind.HostKey => options.HostKeyAlgorithms,
        SshAlgorithmKind.Cipher => options.Ciphers,
        _ => options.Macs,
    };

    private static string Merge(SshAlgorithmKind kind, string? spec, bool legacy, IReadOnlyList<string> add)
    {
        string trimmed = spec?.Trim() ?? string.Empty;
        // 写错了的清单本来就连不上(装配时按配置错误报),谈不到协商失败;万一走到这里,换成只追加这几个。
        if (trimmed.Length == 0
            || !SshAlgorithmPreferences.TryApply(kind, trimmed, legacy, out IReadOnlyList<string> current, out _))
        {
            return "+" + string.Join(',', add);
        }
        string[] missing = [.. add.Where(a => !current.Contains(a, StringComparer.Ordinal))];
        if (missing.Length == 0)
        {
            return trimmed;
        }
        return trimmed[0] is '-' or '^'
            ? string.Join(',', [.. current, .. missing])
            : $"{trimmed},{string.Join(',', missing)}";
    }
}

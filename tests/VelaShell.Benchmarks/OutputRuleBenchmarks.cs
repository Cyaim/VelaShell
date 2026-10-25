using BenchmarkDotNet.Attributes;
using VelaShell.Core.Models;
using VelaShell.Core.OutputRules;
using VelaShell.Terminal.Semantics;

namespace VelaShell.Benchmarks;

/// <summary>
/// 用户输出规则在渲染热路径上的代价:一整屏<b>全新</b>的行(高亮缓存全 miss,刷屏输出时每帧就是这样)
/// 先跑内置的 7 条语义正则,再跑 N 条用户高亮规则;以及触发器每批输出判一次光标行的代价。
/// </summary>
/// <remarks>
/// <para>
/// 一屏取 50 行 × 约 120 字符的日志行,里面有 IP、URL、错误词、数字,也有用户规则要命中的词 ——
/// 命中越多,span 收集越多,比全不命中更接近真实负载。规则都是线性时间引擎编译的(与设置页同一条路)。
/// </para>
/// <para>
/// 读法:<c>BuiltInOnly</c> 是改动之前每一帧 miss 时就要付的钱;<c>WithUserRules</c> 减去它就是 N 条规则的增量。
/// 帧间不变的行走缓存,不在这里 —— 那条路上用户规则一条都不跑(见 <c>OutputHighlightTests.UnchangedRows_DoNotRerunTheRules</c>)。
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class OutputRuleBenchmarks
{
    private string[] _screen = [];
    private OutputRuleSet _rules = OutputRuleSet.Empty;
    private readonly List<OutputHighlightSpan> _spans = [];

    /// <summary>用户高亮规则条数(0 = 只有内置;64 = 规则表上限)。</summary>
    [Params(8, 32, 64)]
    public int RuleCount { get; set; } = 8;

    [GlobalSetup]
    public void Setup()
    {
        _screen =
        [
            .. Enumerable.Range(0, 50).Select(i =>
                $"2026-10-25 08:{i % 60:00}:{(i * 7) % 60:00}.{i * 13 % 1000:000} host-{i} kernel: [{i * 31}] eth0: link up 10.0.{i}.{i * 3 % 255} " +
                $"svc{i % 9} state=running warn={(i % 4 == 0 ? "WARN" : "ok")} see https://example.com/{i} rc={i % 3}"),
        ];
        // 规则模样照常见用法:单词、单词表、带字符类的模式,一部分会命中、一部分不会。
        string[] shapes =
        [
            "ERROR|FATAL|panic", @"\bWARN\b", @"svc[0-9]+", @"rc=[1-9]", "timeout", @"host-\d{2}\b",
            @"state=(failed|stopped)", @"\[\d{3,}\]",
        ];
        List<OutputRule> rules = [];
        for (int i = 0; i < RuleCount; i++)
        {
            rules.Add(new OutputRule
            {
                Name = $"r{i}",
                Pattern = shapes[i % shapes.Length] + (i < shapes.Length ? string.Empty : $"|zz{i}"),
                Foreground = i % 16,
            });
        }
        _rules = OutputRuleCompiler.Build(rules, null, null);
    }

    [Benchmark(Baseline = true, Description = "一屏新行:只有内置语义高亮")]
    public int BuiltInOnly()
    {
        int total = 0;
        foreach (string line in _screen)
        {
            total += SemanticMatcher.Match(line).Count;
        }
        return total;
    }

    [Benchmark(Description = "一屏新行:内置 + N 条用户高亮")]
    public int WithUserRules()
    {
        int total = 0;
        foreach (string line in _screen)
        {
            total += SemanticMatcher.Match(line).Count;
            _spans.Clear();
            _rules.CollectHighlights(line, _spans);
            total += _spans.Count;
        }
        return total;
    }

    [Benchmark(Description = "触发器:一批输出判一次光标行(N 条规则)")]
    public int TriggerCursorLine()
    {
        // 触发器每批输出只看光标所在那一行,与输出量无关;这里拿 N 条规则当应答规则过一遍同一行。
        int hits = 0;
        ReadOnlySpan<char> line = _screen[^1];
        foreach (CompiledOutputRule rule in _rules.Highlights)
        {
            hits += rule.FirstMatch(line).Length > 0 ? 1 : 0;
        }
        return hits;
    }
}

using System.Text.RegularExpressions;
using VelaShell.Core.Models;

namespace VelaShell.Core.OutputRules;

/// <summary>测试结果里一行中的一段:命中了某条高亮规则,或者没命中(<see cref="Rule" /> 为 null)。</summary>
/// <param name="Text">这一段的文字。</param>
/// <param name="Rule">命中的高亮规则;没命中为 null。</param>
public sealed record OutputRuleTestSegment(string Text, OutputRule? Rule);

/// <summary>测试结果里命中过的一行。</summary>
/// <param name="LineNumber">行号(从 1 起)。</param>
/// <param name="Segments">按高亮切开的各段(拼起来就是整行)。</param>
/// <param name="Responders">命中这一行的自动应答规则(按表里的顺序;终端里只有第一条会应答)。</param>
public sealed record OutputRuleTestLine(
    int LineNumber,
    IReadOnlyList<OutputRuleTestSegment> Segments,
    IReadOnlyList<OutputRule> Responders);

/// <summary>
/// 设置页「测试」:把一段粘贴进来的输出按规则逐行过一遍,给出哪一行哪一段命中了哪条规则。
/// </summary>
/// <remarks>
/// 与终端用同一套判据 —— 线性时间引擎、空命中不算、高亮逐字先到先得(表里排前面的赢)。
/// 不同的只有一处:终端里自动应答只对光标所在、等着输入的那一行生效,这里每一行都判,
/// 好让用户看清这条正则会对哪些字起反应(界面上写明了)。
/// </remarks>
public static class OutputRuleTester
{
    /// <summary>最多测这么多行(粘进来一整个日志文件时,设置页不能卡住)。</summary>
    public const int MaxLines = 500;

    /// <summary>逐行测试。</summary>
    /// <param name="rules">参与测试的规则(停用的、正则编不过的跳过;作用范围不看)。</param>
    /// <param name="text">粘贴进来的文本。</param>
    /// <param name="testedLines">实际测了多少行(超过 <see cref="MaxLines" /> 的部分不测)。</param>
    /// <returns>命中过的行。</returns>
    public static IReadOnlyList<OutputRuleTestLine> Run(IReadOnlyList<OutputRule> rules, string? text, out int testedLines)
    {
        testedLines = 0;
        if (string.IsNullOrEmpty(text) || rules.Count == 0)
        {
            return [];
        }
        List<(OutputRule Rule, Regex Regex)> highlights = [];
        List<(OutputRule Rule, Regex Regex)> responders = [];
        foreach (OutputRule rule in rules)
        {
            if (!rule.Enabled || !OutputRuleCompiler.TryCompile(rule.Pattern, rule.CaseSensitive, out Regex? regex).IsValid)
            {
                continue;
            }
            (rule.Action == OutputRuleActions.Respond ? responders : highlights).Add((rule, regex!));
        }

        List<OutputRuleTestLine> results = [];
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        testedLines = Math.Min(lines.Length, MaxLines);
        for (int i = 0; i < testedLines; i++)
        {
            string line = lines[i].TrimEnd('\r');
            var owner = new OutputRule?[line.Length];
            bool any = false;
            foreach ((OutputRule rule, Regex regex) in highlights)
            {
                foreach ((int Index, int Length) match in SafeMatches(regex, line))
                {
                    for (int c = match.Index; c < match.Index + match.Length; c++)
                    {
                        owner[c] ??= rule;
                    }
                    any = true;
                }
            }
            List<OutputRule> hitResponders = [];
            foreach ((OutputRule rule, Regex regex) in responders)
            {
                if (SafeMatches(regex, line).Count > 0)
                {
                    hitResponders.Add(rule);
                }
            }
            if (!any && hitResponders.Count == 0)
            {
                continue;
            }
            results.Add(new(i + 1, Split(line, owner), hitResponders));
        }
        return results;
    }

    private static List<(int Index, int Length)> SafeMatches(Regex regex, string line)
    {
        List<(int Index, int Length)> matches = [];
        try
        {
            foreach (ValueMatch match in regex.EnumerateMatches(line))
            {
                if (match.Length > 0)
                {
                    matches.Add((match.Index, match.Length));
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // 与终端同口径:超时按没命中算。
        }
        return matches;
    }

    private static List<OutputRuleTestSegment> Split(string line, OutputRule?[] owner)
    {
        List<OutputRuleTestSegment> segments = [];
        int start = 0;
        for (int c = 1; c <= line.Length; c++)
        {
            if (c == line.Length || !ReferenceEquals(owner[c], owner[start]))
            {
                segments.Add(new(line[start..c], owner[start]));
                start = c;
            }
        }
        return segments;
    }
}

using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.Services;

/// <summary>一条补全建议:插入文本 + 补充说明 + 来源标签,以及(正文与某条快捷命令相同时)那条快捷命令。</summary>
/// <param name="Text">插入的文本。</param>
/// <param name="Detail">补充说明。</param>
/// <param name="Source">来源标签(快捷命令 / 历史 / 常用)。</param>
/// <param name="Command">
/// 正文与之相同的快捷命令;没有为 null。带 <c>{{变量}}</c> 时选中它要先问变量(§132),
/// 而不是把占位原文插进命令行 —— 同一段正文从历史里冒出来(早先按原文接受过)也一样。
/// </param>
public sealed record CommandSuggestion(string Text, string? Detail, string Source, QuickCommand? Command = null)
{
    /// <summary>选中它要先弹变量询问框:来自一条快捷命令,且正文里有 <c>{{变量}}</c> 占位。</summary>
    public bool HasVariables => Command is not null && QuickCommandTemplate.Parse(Text).HasVariables;
}

/// <summary>合并本地命令历史与快捷命令的补全建议提供器。</summary>
public sealed class CommandSuggestionProvider
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
    private readonly CommandHistoryService _history;
    private readonly IQuickCommandRepository? _repository;
    private List<QuickCommand> _customCommands = [];
    private DateTime _customLoadedAt = DateTime.MinValue;

    /// <summary>创建由历史记录和快捷命令仓储共同提供建议的服务。</summary>
    public CommandSuggestionProvider(
        CommandHistoryService history,
        IQuickCommandRepository? repository
    )
    {
        _history = history;
        _repository = repository;
        _repository?.Changed += (_, _) => _customLoadedAt = DateTime.MinValue;
    }

    /// <summary>
    /// 返回按相关度排序的建议。多来源加权合并:历史前缀命中(带最近加成)>
    /// 常用子命令(git/docker 等上下文)> 快捷命令前缀命中 > 快捷命令松散命中 >
    /// 历史包含命中;同文本去重取最高分。
    /// </summary>
    public async Task<IReadOnlyList<CommandSuggestion>> GetSuggestionsAsync(string prefix, int max)
    {
        List<QuickCommand> quick = await GetQuickCommandsAsync();
        string quickCommandSource = Strings.Get("QuickCommands");
        string historySource = Strings.Get("Svc_History");

        if (string.IsNullOrEmpty(prefix))
        {
            // 空输入(Alt+Enter 全量召出):快捷命令在前,最近历史随后。
            var all = new List<CommandSuggestion>(max);
            var seenAll = new HashSet<string>(StringComparer.Ordinal);
            foreach (QuickCommand command in quick)
            {
                if (all.Count < max && seenAll.Add(command.CommandText))
                {
                    all.Add(new(command.CommandText, DescribeQuickCommand(command), quickCommandSource, command));
                }
            }
            foreach (string entry in _history.Entries)
            {
                if (all.Count < max && seenAll.Add(entry))
                {
                    all.Add(new(entry, null, historySource));
                }
            }
            return all;
        }

        // 文本 → (分数, 建议),同文本保留最高分。
        var scored = new Dictionary<string, (int Score, CommandSuggestion Item)>(
            StringComparer.Ordinal
        );

        void Add(int score, string text, string? detail, string source)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length <= prefix.Length)
            {
                return;
            }
            if (!scored.TryGetValue(text, out (int Score, CommandSuggestion Item) existing)
                || existing.Score < score)
            {
                scored[text] = (score, new(text, detail, source));
            }
        }

        for (int i = 0; i < _history.Entries.Count; i++)
        {
            string entry = _history.Entries[i];
            int recency = Math.Max(0, 30 - i); // MRU 序:越近的历史越靠前。
            if (entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                Add(100 + recency, entry, null, historySource);
            }
            else if (entry.Contains(prefix, StringComparison.OrdinalIgnoreCase))
            {
                Add(30 + recency / 2, entry, null, historySource);
            }
        }

        string usageSource = Strings.Get("Svc_CommonUsage");
        foreach (string candidate in CommonUsageCatalog.Complete(prefix))
        {
            Add(90, candidate, null, usageSource);
        }

        foreach (QuickCommand command in quick)
        {
            if (command.CommandText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                Add(80, command.CommandText, DescribeQuickCommand(command), quickCommandSource);
            }
            else if (MatchesLoosely(command, prefix))
            {
                Add(40, command.CommandText, DescribeQuickCommand(command), quickCommandSource);
            }
        }

        // 同文本去重时历史常常赢(分高),但它若与一条快捷命令一字不差,选中时仍按那条快捷命令问变量。
        var quickByText = new Dictionary<string, QuickCommand>(StringComparer.Ordinal);
        foreach (QuickCommand command in quick)
        {
            _ = quickByText.TryAdd(command.CommandText, command);
        }
        return
        [
            .. scored
                .Values.OrderByDescending(pair => pair.Score)
                .ThenBy(pair => pair.Item.Text.Length)
                .Take(max)
                .Select(pair => quickByText.TryGetValue(pair.Item.Text, out QuickCommand? linked)
                    ? pair.Item with { Command = linked }
                    : pair.Item),
        ];
    }

    private static bool MatchesLoosely(QuickCommand command, string prefix) =>
        command.CommandText.Contains(prefix, StringComparison.OrdinalIgnoreCase)
        || command.Name.Contains(prefix, StringComparison.OrdinalIgnoreCase)
        || command.Description.Contains(prefix, StringComparison.OrdinalIgnoreCase);

    private static string? DescribeQuickCommand(QuickCommand command)
    {
        if (!string.IsNullOrWhiteSpace(command.Description))
        {
            return command.Description;
        }
        return command.Name == command.CommandText ? null : command.Name;
    }

    private async Task<List<QuickCommand>> GetQuickCommandsAsync()
    {
        if (DateTime.UtcNow - _customLoadedAt < CacheTtl)
        {
            return _customCommands;
        }
        var merged = new List<QuickCommand>();
        IEnumerable<QuickCommand> builtIns = QuickCommandCatalog.BuiltIns;
        if (_repository is not null)
        {
            try
            {
                QuickCommandLoadResult result = await _repository.LoadAsync();
                merged.AddRange(result.Data.Commands);
                // 用户删掉的内置命令不再补全,改过的以改后的那条(已在自定义命令里)为准。
                builtIns = QuickCommandCatalog.VisibleBuiltIns(result.Data);
            }
            catch
            {
                // 读不出自定义命令就只用内置目录。
            }
        }
        merged.AddRange(builtIns);
        // 多行命令不进补全:补全是按已键入的前缀续写一行,接受一条多行候选等于替用户敲回车。
        merged.RemoveAll(command => QuickCommandText.IsMultiline(command.CommandText));
        _customCommands = merged;
        _customLoadedAt = DateTime.UtcNow;
        return merged;
    }
}

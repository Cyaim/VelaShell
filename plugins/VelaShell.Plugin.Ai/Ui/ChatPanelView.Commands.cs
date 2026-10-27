using Avalonia.Controls;

namespace VelaShell.Plugin.Ai.Ui;

public partial class ChatPanelView
{
    private readonly List<string> _commandCandidates = [];
    private static readonly string[] LocalCommands = ["/clear", "/resume", "/help"];

    private bool TryShowCommandPicker(string text, int caret)
    {
        if (!text.StartsWith('/') || caret < 1 || text[..caret].Any(char.IsWhiteSpace)) return false;
        _commandCandidates.Clear();
        _commandCandidates.AddRange(LocalCommands.Where(command => command.StartsWith(text[..caret], StringComparison.OrdinalIgnoreCase)));
        _imageMentionCandidates.Clear();
        _fileCandidates.Clear();
        _pickerGeneration++;
        _fileTokenStart = 0;
        _fileIndex = 0;
        FileList.Children.Clear();
        foreach (string command in _commandCandidates)
        {
            int index = FileList.Children.Count;
            var row = new Border
            {
                Classes = { "fileRow" },
                Child = new TextBlock
                {
                    Classes = { "fileName" },
                    Text = command + " · " + _loc[CommandDescription(command)]
                }
            };
            row.PointerPressed += (_, e) => { e.Handled = true; AcceptCommand(index); };
            FileList.Children.Add(row);
        }
        FilePopupHeader.Text = _loc["CommandPickerHeader"];
        FilePopup.IsOpen = _commandCandidates.Count > 0;
        HighlightCandidate();
        return true;
    }

    private static string CommandDescription(string command) => command switch
    {
        "/clear" => "CommandClear",
        "/resume" => "CommandResume",
        _ => "CommandHelp"
    };

    private void AcceptCommand(int index)
    {
        if (index < 0 || index >= _commandCandidates.Count) return;
        string replacement = _commandCandidates[index] + " ";
        int caret = Math.Clamp(InputBox.CaretOffset, 0, InputBox.Text.Length);
        _pickerSuspended = true;
        try
        {
            InputBox.Document.Replace(0, caret, replacement);
            InputBox.CaretOffset = replacement.Length;
        }
        finally { _pickerSuspended = false; }
        CloseFilePicker();
        InputBox.TextArea.Focus();
    }

    private async Task<bool> TryRunLocalCommandAsync(string text)
    {
        if (!text.StartsWith('/')) return false;
        string command = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (!LocalCommands.Contains(command, StringComparer.OrdinalIgnoreCase))
        {
            SetStatus(_loc["CommandUnknown"]);
            return true;
        }
        if (Busy)
        {
            SetStatus(_loc["CommandBusy"]);
            return true;
        }
        if (command.Equals("/clear", StringComparison.OrdinalIgnoreCase))
        {
            if (!text.Equals(command, StringComparison.OrdinalIgnoreCase))
            {
                SetStatus(_loc["CommandClear"]);
                return true;
            }
            ClearAttachments();
            InputBox.Text = "";
            CloseFilePicker();
            StartNewChat();
            return true;
        }
        if (command.Equals("/resume", StringComparison.OrdinalIgnoreCase))
        {
            string query = text[command.Length..].Trim();
            InputBox.Text = "";
            CloseFilePicker();
            if (query.Length > 0)
            {
                var sessions = await _historyStore.ListSessionsAsync();
                var selected = sessions.FirstOrDefault(session => session.Id.Equals(query, StringComparison.Ordinal));
                if (selected is not null) { ClearAttachments(); await LoadConversationAsync(selected); return true; }
            }
            ClearAttachments();
            HistorySearchBox.Text = query;
            SetActiveView(PanelView.History);
            await RefreshHistoryListAsync();
            return true;
        }
        InputBox.Text = "";
        CloseFilePicker();
        SetStatus(string.Join(" · ", LocalCommands.Select(item => item + ": " + _loc[CommandDescription(item)])));
        return true;
    }
}

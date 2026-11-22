using Groundwork.Core;

namespace WorldsYetUnseen.Client;

internal sealed record ConsoleCommand(string Name, string Usage, string Description,
    Func<string, string> Execute, params string[] Values)
{
    internal string? Example { get; init; }
    internal string? UsageDetails { get; init; }
    internal Func<string>? CurrentValue { get; init; }
    internal Func<string, IEnumerable<string>>? CompleteValue { get; init; }
}

internal sealed record ConsoleView(string Prompt, string? Suggestions, string[] Lines, bool ShowingHelp,
    bool SearchingHistory = false, int RequiredRows = 0);

internal sealed class WorldConsole(ConsoleCommand[] commands)
{
    private const int LineLimit = 80;
    private const int HistoryLimit = 32;
    private static readonly string[] BuiltIns = ["help", "clear"];
    private readonly List<string> _history = [];
    private readonly Queue<(string Command, string Result)> _output = new();
    private string _line = "";
    private string _draft = "";
    private int _cursor;
    private int _historyOffset;
    private string[]? _completions;
    private int _completionIndex;
    private bool _showHelp = true;
    private string? _searchQuery;
    private int _searchIndex = -1;

    private bool IsOpen { get; set; }
    internal ConsoleView? View { get; private set; }

    internal static bool Fits(int width, int height) => width >= 260 && height >= 250;

    // Consume the entire snapshot on open and close so menu and gameplay shortcuts cannot fire.
    internal bool Handle(InputSnapshot input, bool focused, bool fits)
    {
        if (!fits && IsOpen)
        {
            Close();
            return true;
        }
        if (!fits || !focused || input.TransitionLoss)
            return IsOpen;

        if (PrototypeMenu.Pressed(input, InputControl.F3))
        {
            if (IsOpen)
                Close();
            else
            {
                IsOpen = true;
                View = CreateView();
            }
            return true;
        }

        if (!IsOpen)
            return false;

        var leftControl = HeldAtStart(input, InputControl.LeftControl);
        var rightControl = HeldAtStart(input, InputControl.RightControl);
        foreach (var entry in input.Events)
        {
            if (entry.Kind == InputEventKind.FocusCleared)
            {
                leftControl = rightControl = false;
                continue;
            }
            if (entry.Device != InputDevice.Keyboard)
                continue;
            switch (entry.Control)
            {
                case InputControl.LeftControl:
                    leftControl = entry.Kind == InputEventKind.Pressed;
                    continue;
                case InputControl.RightControl:
                    rightControl = entry.Kind == InputEventKind.Pressed;
                    continue;
            }
            if (entry.Kind != InputEventKind.Pressed)
                continue;

            if (entry.Control == InputControl.Escape)
            {
                if (_searchQuery is null)
                    Close();
                else
                {
                    _searchQuery = null;
                    View = CreateView();
                }
                return true;
            }

            if (entry.Control == InputControl.R && (leftControl || rightControl))
            {
                SearchHistory();
                View = CreateView();
            }
            else if (_searchQuery is not null)
            {
                if (entry.Control is InputControl.Enter or InputControl.NumpadEnter)
                {
                    AcceptSearchMatch();
                    View = CreateView();
                    return true;
                }
                if (EditSearch(entry.Control))
                    View = CreateView();
            }
            else if (Edit(entry.Control))
                View = CreateView();
        }
        return true;
    }

    private static bool HeldAtStart(InputSnapshot input, InputControl control)
    {
        // HeldControls reflects the end of the capture. The first transition also records
        // whether the modifier was down before a short chord or release in this batch.
        foreach (var entry in input.Events)
        {
            if (entry.Kind == InputEventKind.FocusCleared)
                return false;
            if (entry.Device == InputDevice.Keyboard && entry.Control == control)
                return entry.Kind == InputEventKind.Released;
        }
        return input.HeldControls.Contains(control);
    }

    private void Close()
    {
        _searchQuery = null;
        IsOpen = false;
        View = null;
    }

    private void SearchHistory()
    {
        _completions = null;
        if (_searchQuery is null)
        {
            _searchQuery = "";
            _searchIndex = FindSearchMatch(_history.Count - 1);
            return;
        }

        var older = FindSearchMatch(_searchIndex - 1);
        _searchIndex = older >= 0 ? older : FindSearchMatch(_history.Count - 1);
    }

    private bool EditSearch(InputControl control)
    {
        var query = _searchQuery!;
        if (control == InputControl.Backspace && query.Length > 0)
            _searchQuery = query[..^1];
        else if (query.Length < LineLimit && Character(control) is { } character)
            _searchQuery = query + character;
        else
            return false;
        _searchIndex = FindSearchMatch(_history.Count - 1);
        return true;
    }

    private int FindSearchMatch(int start)
    {
        for (var i = start; i >= 0; i--)
        {
            if (_history[i].Contains(_searchQuery!, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    private void AcceptSearchMatch()
    {
        if (_searchIndex < 0)
            return;
        SetLine(_history[_searchIndex]);
        _historyOffset = 0;
        _draft = "";
        _searchQuery = null;
    }

    private bool Edit(InputControl control)
    {
        if (control == InputControl.Tab)
        {
            Complete();
            return true;
        }

        _completions = null;
        switch (control)
        {
            case InputControl.Enter or InputControl.NumpadEnter:
                Submit();
                return true;
            case InputControl.Up:
                Recall(1);
                return true;
            case InputControl.Down:
                Recall(-1);
                return true;
            case InputControl.Left:
                _cursor = Math.Max(0, _cursor - 1);
                return true;
            case InputControl.Right:
                _cursor = Math.Min(_line.Length, _cursor + 1);
                return true;
            case InputControl.Home:
                _cursor = 0;
                return true;
            case InputControl.End:
                _cursor = _line.Length;
                return true;
            case InputControl.Backspace when _cursor > 0:
                _line = _line.Remove(--_cursor, 1);
                return true;
            case InputControl.Delete when _cursor < _line.Length:
                _line = _line.Remove(_cursor, 1);
                return true;
        }

        if (_line.Length >= LineLimit || Character(control) is not { } character)
            return false;
        _line = _line.Insert(_cursor++, character.ToString());
        return true;
    }

    private static char? Character(InputControl control) => control switch
    {
        >= InputControl.A and <= InputControl.Z => (char)('a' + (control - InputControl.A)),
        >= InputControl.Digit0 and <= InputControl.Digit9 => (char)('0' + (control - InputControl.Digit0)),
        >= InputControl.Numpad0 and <= InputControl.Numpad9 => (char)('0' + (control - InputControl.Numpad0)),
        InputControl.Space => ' ',
        InputControl.Period or InputControl.NumpadDecimal => '.',
        InputControl.Comma => ',',
        InputControl.Minus or InputControl.NumpadSubtract => '-',
        _ => null,
    };

    private void Submit()
    {
        var line = _line.Trim();
        if (line.Length == 0)
            return;

        if (_history.Count == 0 || _history[^1] != line)
            _history.Add(line);
        if (_history.Count > HistoryLimit)
            _history.RemoveAt(0);
        _historyOffset = 0;
        _draft = "";
        SetLine("");

        if (line == "help")
        {
            _showHelp = true;
            return;
        }

        _showHelp = false;
        if (line == "clear")
        {
            _output.Clear();
            return;
        }

        var (name, value) = Split(line);
        var command = Find(name);
        var result = name switch
        {
            "help" => Describe(value),
            "clear" => "Usage: clear",
            _ => command?.Execute(value) ?? $"Unknown command: {name}. Type help.",
        };
        AppendOutput(line, result);
    }

    internal void Report(string command, string result)
    {
        _showHelp = false;
        AppendOutput(command, result);
        if (IsOpen)
            View = CreateView();
    }

    private void AppendOutput(string command, string result)
    {
        if (_output.Count == 8)
            _output.Dequeue();
        _output.Enqueue((command, result));
    }

    private string Describe(string name)
    {
        if (name == "help")
            return "Usage: help [command]\nList commands or show details.\nExample: help fov";
        if (name == "clear")
            return "Usage: clear\nClear console output.";
        if (Find(name) is not { } command)
            return $"Unknown command: {name}. Type help.";

        var lines = new List<string> { $"Usage: {command.Usage}", command.Description };
        if (command.CurrentValue is { } read)
            lines.Add(read());
        if (command.UsageDetails is { } details)
            lines.Add(details);
        if (command.Example is { } example)
            lines.Add($"Example: {example}");
        return string.Join('\n', lines);
    }

    private IEnumerable<string> CommandNames() => BuiltIns.Concat(commands.Select(command => command.Name));

    private void Recall(int direction)
    {
        if (_historyOffset == 0)
            _draft = _line;
        _historyOffset = Math.Clamp(_historyOffset + direction, 0, _history.Count);
        SetLine(_historyOffset == 0 ? _draft : _history[^_historyOffset]);
    }

    private void Complete()
    {
        if (_completions is null)
        {
            _completions = Matches();
            _completionIndex = -1;
        }
        if (_completions.Length == 0)
            return;
        _completionIndex = (_completionIndex + 1) % _completions.Length;
        SetLine(_completions[_completionIndex]);
        if (_completions.Length == 1)
            _completions = null;
    }

    private string[] Matches()
    {
        var line = _line.TrimStart();
        var (name, value) = Split(line);
        if (!line.Contains(' '))
            return CommandNames().Where(candidate => candidate.StartsWith(name, StringComparison.Ordinal))
                .Select(candidate => candidate + (candidate == "help" || Find(candidate)?.Values.Length > 0 ? " " : "")).ToArray();
        var command = Find(name);
        var values = name == "help" ? CommandNames()
            : command?.CompleteValue?.Invoke(line[(name.Length + 1)..].TrimStart()) ?? command?.Values ?? [];
        return values.Where(candidate => candidate.StartsWith(value, StringComparison.Ordinal))
            .Select(candidate => $"{name} {candidate}")
            .Where(candidate => candidate.Length <= LineLimit).ToArray();
    }

    private ConsoleView CreateView()
    {
        var lines = _showHelp
            ? commands.Select(command => $"{command.Usage,-20} {command.Description}")
                .ToArray()
            : _output.SelectMany(entry => entry.Result.Split('\n').Prepend($"> {entry.Command}")).ToArray();
        var requiredRows = _showHelp ? lines.Length
            : _output.LastOrDefault().Result is { } result ? 2 + result.Count(character => character == '\n') : 0;

        if (_searchQuery is { } query)
        {
            var match = _searchIndex < 0 ? "No history match." : $"Match: {_history[_searchIndex]}";
            return new(query + "▏", match, lines, _showHelp, SearchingHistory: true, RequiredRows: requiredRows);
        }

        var matches = _completions ?? Matches();
        var suggestions = _line.Length == 0 || matches.Length == 0 ? null
            : string.Join("  ·  ", matches.Select(match => match.TrimEnd()));
        return new(_line.Insert(_cursor, "▏"), suggestions, lines, _showHelp, RequiredRows: requiredRows);
    }

    private ConsoleCommand? Find(string name) => Array.Find(commands, command => command.Name == name);

    private static (string Name, string Value) Split(string line)
    {
        var separator = line.IndexOf(' ');
        return separator < 0 ? (line, "") : (line[..separator], line[(separator + 1)..].Trim());
    }

    private void SetLine(string value)
    {
        _line = value;
        _cursor = value.Length;
    }
}

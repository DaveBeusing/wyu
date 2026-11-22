using System.Globalization;
using Groundwork.Voxels;

namespace WorldsYetUnseen.Client;

internal sealed class WorldConsoleMarks(Func<string> world, Func<ulong> seed, Func<PlanetVector> position)
{
    internal const string Usage = "mark save <name>\nmark list [page]\nmark show <name>";
    private const int MarkLimit = 16;
    private const int MarksPerPage = 2;
    private readonly Dictionary<string, Mark> _marks = new(StringComparer.Ordinal);

    private readonly record struct Mark(string World, ulong Seed, PlanetVector Position);

    internal string Handle(string value)
    {
        var separator = value.IndexOf(' ');
        var verb = separator < 0 ? value : value[..separator];
        var argument = separator < 0 ? "" : value[(separator + 1)..].TrimStart();
        if (verb == "list")
            return List(argument);
        if (verb is not ("save" or "show"))
            return Usage;
        if (argument.Length is < 1 or > 24 ||
            argument.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
            return "Name: 1–24 a-z, 0-9, hyphen";

        return verb == "save" ? Save(argument) : Show(argument);
    }

    internal IEnumerable<string> CompleteValue(string value)
    {
        if (!value.StartsWith("show ", StringComparison.Ordinal))
            return ["save", "list", "show"];

        var nameStart = 5;
        while (nameStart < value.Length && value[nameStart] == ' ')
            nameStart++;
        var prefix = value[..nameStart];
        return _marks.Keys.Order(StringComparer.Ordinal).Select(name => prefix + name);
    }

    private string Save(string name)
    {
        var exists = _marks.ContainsKey(name);
        if (!exists && _marks.Count >= MarkLimit)
            return $"Mark limit: {MarkLimit}.";

        _marks[name] = new(world(), seed(), position());
        return exists ? $"Mark replaced.\n{name}" : $"Mark saved.\n{name}";
    }

    private string Show(string name)
    {
        if (!_marks.TryGetValue(name, out var mark))
            return $"Mark not found.\n{name}";

        var location = mark.Position;
        return FormattableString.Invariant(
            $"Mark\n{name}\nWorld {mark.World}\nSeed {mark.Seed}\nX {location.X:R}\nY {location.Y:R}\nZ {location.Z:R}");
    }

    private string List(string argument)
    {
        var pages = Math.Max(1, (_marks.Count + MarksPerPage - 1) / MarksPerPage);
        var page = 1;
        if (argument.Length > 0 &&
            (!int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out page) ||
             page < 1 || page > pages))
            return $"Mark page: 1–{pages}.";
        if (_marks.Count == 0)
            return "No marks saved.";

        var entries = _marks.OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Skip((page - 1) * MarksPerPage).Take(MarksPerPage);
        var lines = new List<string> { $"Marks {page}/{pages}" };
        foreach (var (name, mark) in entries)
        {
            lines.Add(name);
            lines.Add(FormattableString.Invariant($"X {mark.Position.X:0.#} · Y {mark.Position.Y:0.#}"));
            lines.Add(FormattableString.Invariant($"Z {mark.Position.Z:0.#}"));
        }
        return string.Join('\n', lines);
    }
}

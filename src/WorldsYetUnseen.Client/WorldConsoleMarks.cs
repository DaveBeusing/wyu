using System.Globalization;
using Groundwork.Voxels;
using static WorldsYetUnseen.Client.FlightVector;

namespace WorldsYetUnseen.Client;

internal sealed class WorldConsoleMarks(Func<string> world, Func<ulong> seed, Func<PlanetVector> position,
    Func<PlanetVector>? forward = null, Func<string, bool>? copyText = null)
{
    internal const string Usage = "mark save <name>\nmark list [page]\nmark show / locate <name>\nmark copy <name>\nmark rename <old> <new>\nmark delete <name>\nmark track <name|off>\nmark track name <name>";
    private const int MarkLimit = 16;
    private const int MarksPerPage = 2;
    private const double LocationTolerance = 0.05;
    private const double DirectionTolerance = 1e-6;
    private readonly Dictionary<string, Mark> _marks = new(StringComparer.Ordinal);
    private string? _trackedName;

    private readonly record struct Mark(string World, ulong Seed, PlanetVector Position);
    private readonly record struct Location(double Distance, double Altitude, string Bearing);

    internal string Handle(string value)
    {
        var separator = value.IndexOf(' ');
        var verb = separator < 0 ? value : value[..separator];
        var argument = separator < 0 ? "" : value[(separator + 1)..].TrimStart();
        if (verb == "list")
            return List(argument);
        if (verb == "rename")
            return Rename(argument);
        if (verb == "track")
            return Track(argument);
        if (verb is not ("save" or "show" or "locate" or "copy" or "delete"))
            return Usage;
        if (!ValidName(argument))
            return "Name: 1–24 a-z, 0-9, hyphen";

        return verb switch
        {
            "save" => Save(argument),
            "locate" => Locate(argument),
            "delete" => Delete(argument),
            _ => Show(argument, verb == "copy"),
        };
    }

    internal IEnumerable<string> CompleteValue(string value)
    {
        var separator = value.IndexOf(' ');
        var verb = separator < 0 ? value : value[..separator];
        if (separator < 0 || verb is not ("show" or "locate" or "copy" or "rename" or "delete" or "track"))
            return ["save", "list", "show", "locate", "copy", "rename", "delete", "track"];

        var nameStart = separator + 1;
        while (nameStart < value.Length && value[nameStart] == ' ')
            nameStart++;
        if (verb == "rename" && value.AsSpan(nameStart).Contains(' '))
            return [];

        if (verb == "track" && value.AsSpan(nameStart).StartsWith("name ", StringComparison.Ordinal))
        {
            var prefixEnd = nameStart + 5;
            while (prefixEnd < value.Length && value[prefixEnd] == ' ')
                prefixEnd++;
            var namedPrefix = value[..prefixEnd];
            return _marks.Keys.Order(StringComparer.Ordinal).Select(name => namedPrefix + name);
        }
        var prefix = value[..nameStart];
        var names = verb == "track" ? _marks.Keys.Append("off").Append("name").Distinct(StringComparer.Ordinal) : _marks.Keys;
        return names.Order(StringComparer.Ordinal).Select(name => prefix + name);
    }

    private bool TryGetPosition(string name, out PlanetVector target)
    {
        target = default;
        if (!ValidName(name) || !_marks.TryGetValue(name, out var mark) || !IsCurrentWorld(mark) ||
            !double.IsFinite(mark.Position.X) || !double.IsFinite(mark.Position.Y) || !double.IsFinite(mark.Position.Z))
            return false;

        target = mark.Position;
        return true;
    }

    internal string? ReadTrackedHint()
    {
        if (_trackedName is not { } name || !TryGetPosition(name, out var target) || Measure(target) is not { } location)
            return null;

        return FormattableString.Invariant($"Mark {name}\n{location.Distance:0.0} m · {location.Bearing}");
    }

    private string Save(string name)
    {
        var exists = _marks.ContainsKey(name);
        if (!exists && _marks.Count >= MarkLimit)
            return $"Mark limit: {MarkLimit}.";

        _marks[name] = new(world(), seed(), position());
        return exists ? $"Mark replaced.\n{name}" : $"Mark saved.\n{name}";
    }

    private string Rename(string argument)
    {
        var names = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (names.Length != 2)
            return "Usage: mark rename <old> <new>";
        var oldName = names[0];
        var newName = names[1];
        if (!ValidName(oldName) || !ValidName(newName))
            return "Name: 1–24 a-z, 0-9, hyphen";
        if (!_marks.TryGetValue(oldName, out var mark))
            return $"Mark not found.\n{oldName}";
        if (oldName == newName)
            return $"Mark already has that name.\n{oldName}";
        if (!_marks.TryAdd(newName, mark))
            return $"Mark already exists.\n{newName}";

        _marks.Remove(oldName);
        if (_trackedName == oldName)
            _trackedName = newName;
        return $"Mark renamed.\n{oldName}\n{newName}";
    }

    private string Delete(string name)
    {
        if (!_marks.Remove(name))
            return $"Mark not found.\n{name}";
        if (_trackedName == name)
            _trackedName = null;
        return $"Mark deleted.\n{name}";
    }

    private string Track(string argument)
    {
        if (argument == "off")
        {
            _trackedName = null;
            return "Mark tracking off.";
        }
        var name = argument.StartsWith("name ", StringComparison.Ordinal) ? argument[5..].TrimStart() : argument;
        if (!ValidName(name))
            return "Name: 1–24 a-z, 0-9, hyphen";
        if (!_marks.TryGetValue(name, out var mark))
            return $"Mark not found.\n{name}";
        if (!IsCurrentWorld(mark))
            return "Mark is in another world.";

        _trackedName = name;
        return $"Tracking mark.\n{name}";
    }

    private string Show(string name, bool copy)
    {
        if (!_marks.TryGetValue(name, out var mark))
            return $"Mark not found.\n{name}";

        var location = mark.Position;
        var text = FormattableString.Invariant(
            $"Mark\n{name}\nWorld {mark.World}\nSeed {mark.Seed}\nX {location.X:R}\nY {location.Y:R}\nZ {location.Z:R}");
        if (!copy)
            return text;
        return copyText?.Invoke(text) == true ? "Mark copied." : "Clipboard unavailable.";
    }

    private string Locate(string name)
    {
        if (!_marks.TryGetValue(name, out var mark))
            return $"Mark not found.\n{name}";
        if (!IsCurrentWorld(mark))
            return "Mark is in another world.";
        if (Measure(mark.Position) is not { } location)
            return "Mark location unavailable.";

        return FormattableString.Invariant(
            $"Locate\n{name}\nDirect {location.Distance:0.0} m\nAltitude Δ {location.Altitude:+0.0;-0.0;0.0} m\n{location.Bearing}");
    }

    private Location? Measure(PlanetVector target)
    {
        var current = position();
        var offset = Subtract(target, current);
        var distance = Length(offset);
        var currentRadius = Length(current);
        var markRadius = Length(target);
        if (!double.IsFinite(distance) || !double.IsFinite(currentRadius) || !double.IsFinite(markRadius))
            return null;

        var altitude = markRadius - currentRadius;
        var bearing = distance < LocationTolerance ? "At mark."
            : Bearing(current, currentRadius, markRadius, offset, altitude);
        return new(distance, altitude, bearing);
    }

    private bool IsCurrentWorld(Mark mark) => string.Equals(mark.World, world(), StringComparison.Ordinal) && mark.Seed == seed();

    private static bool ValidName(string name) => name.Length is >= 1 and <= 24 &&
        name.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private string Bearing(PlanetVector current, double currentRadius, double markRadius, PlanetVector offset, double altitude)
    {
        if (currentRadius <= DirectionTolerance || markRadius <= DirectionTolerance)
            return "Bearing unavailable.";

        var up = Scale(current, 1 / currentRadius);
        var target = Tangent(offset, up);
        if (Length(target) < LocationTolerance)
        {
            // The opposite radial line has no unique tangent bearing.
            if (Dot(offset, up) < -currentRadius)
                return "Bearing n/a (opposite).";
            return altitude >= 0 ? "Directly above." : "Directly below.";
        }

        if (forward is null)
            return "Bearing unavailable.";
        var view = Tangent(forward(), up);
        var viewLength = Length(view);
        if (!double.IsFinite(viewLength) || viewLength <= DirectionTolerance)
            return "Bearing n/a (view angle).";

        var right = new PlanetVector(view.Y * up.Z - view.Z * up.Y, view.Z * up.X - view.X * up.Z,
            view.X * up.Y - view.Y * up.X);
        var angle = Math.Atan2(Dot(target, right), Dot(target, view)) * (180 / Math.PI);
        var direction = Math.Abs(angle) <= 45 ? "ahead" : Math.Abs(angle) >= 135 ? "behind"
            : angle > 0 ? "right" : "left";
        return FormattableString.Invariant($"Bearing {angle:+0;-0;0}° {direction}");
    }

    private static PlanetVector Tangent(PlanetVector vector, PlanetVector up) =>
        Subtract(vector, Scale(up, Dot(vector, up)));

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

using System.Globalization;
using Groundwork.Voxels;
using static WorldsYetUnseen.Client.FlightVector;

namespace WorldsYetUnseen.Client;

internal sealed class WorldConsoleMarks(Func<string> world, Func<ulong> seed, Func<PlanetVector> position,
    Func<PlanetVector>? forward = null)
{
    internal const string Usage = "mark save <name>\nmark list [page]\nmark show <name>\nmark locate <name>";
    private const int MarkLimit = 16;
    private const int MarksPerPage = 2;
    private const double LocationTolerance = 0.05;
    private const double DirectionTolerance = 1e-6;
    private readonly Dictionary<string, Mark> _marks = new(StringComparer.Ordinal);

    private readonly record struct Mark(string World, ulong Seed, PlanetVector Position);

    internal string Handle(string value)
    {
        var separator = value.IndexOf(' ');
        var verb = separator < 0 ? value : value[..separator];
        var argument = separator < 0 ? "" : value[(separator + 1)..].TrimStart();
        if (verb == "list")
            return List(argument);
        if (verb is not ("save" or "show" or "locate"))
            return Usage;
        if (argument.Length is < 1 or > 24 ||
            argument.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
            return "Name: 1–24 a-z, 0-9, hyphen";

        return verb switch
        {
            "save" => Save(argument),
            "show" => Show(argument),
            _ => Locate(argument),
        };
    }

    internal IEnumerable<string> CompleteValue(string value)
    {
        var separator = value.IndexOf(' ');
        if (separator < 0 || value[..separator] is not ("show" or "locate"))
            return ["save", "list", "show", "locate"];

        var nameStart = separator + 1;
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

    private string Locate(string name)
    {
        if (!_marks.TryGetValue(name, out var mark))
            return $"Mark not found.\n{name}";
        if (!string.Equals(mark.World, world(), StringComparison.Ordinal) || mark.Seed != seed())
            return "Mark is in another world.";

        var current = position();
        var offset = Subtract(mark.Position, current);
        var distance = Length(offset);
        var currentRadius = Length(current);
        var markRadius = Length(mark.Position);
        if (!double.IsFinite(distance) || !double.IsFinite(currentRadius) || !double.IsFinite(markRadius))
            return "Mark location unavailable.";

        var altitude = markRadius - currentRadius;
        var bearing = distance < LocationTolerance ? "At mark."
            : Bearing(current, currentRadius, markRadius, offset, altitude);
        return FormattableString.Invariant(
            $"Locate\n{name}\nDirect {distance:0.0} m\nAltitude Δ {altitude:+0.0;-0.0;0.0} m\n{bearing}");
    }

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

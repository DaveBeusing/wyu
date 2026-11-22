using System.Globalization;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    private const float DefaultFieldOfView = 1f;
    private const int PresetLimit = 16;
    private const int PresetsPerPage = 7; // Leave room for the command and page heading in the smallest console.
    private const string PresetUsage = "preset save <name>\npreset load <name>\npreset list [page]\npreset delete <name>\npreset show <name>\npreset diff <a> <b>";
    private const string ProfileRunUsage = "profile run <a> <b> [sec]";
    private const string ProfileRunTiming = "1–30 s per preset\nDefault: 5 s";
    private const string PresetNameUsage = "Name: 1–24 a-z, 0-9, hyphen";
    private float _fieldOfView = DefaultFieldOfView;
    private ulong? _previewTime;
    private string? _comparisonPresetName;

    private readonly record struct PresentationPreset(float FieldOfView, bool? Fog, ulong? Time);

    private WorldConsole CreateConsole(Func<PlanetVector> position, ConsoleFrameProfile profile, Func<string, bool> copyText)
    {
        var presets = new Dictionary<string, PresentationPreset>(StringComparer.Ordinal);
        string[] presetVerbs = ["save", "load", "list", "delete", "show", "diff"];
        string[] profileValues = ["5", "10", "30", "cancel", "keep a", "keep b", "compare", "run"];
        var commands = new List<ConsoleCommand>
        {
            new("fov", "fov [50..100|reset]", "Vertical camera angle (deg)", value => OutsideComparison(value, SetFieldOfView), "reset")
                { CurrentValue = ReadFieldOfView, Example = "fov 90" },
            new("stats", "stats [on|off]", "F1 diagnostics", value => OutsideComparison(value, SetDiagnostics), "on", "off")
                { CurrentValue = ReadDiagnostics, Example = "stats on" },
        };
        if (_weather is not null)
        {
            commands.Add(new("time", "time [day|night|live]", "Preview the sky and lighting", value => OutsideComparison(value, SetTimePreview), "day", "night", "live")
                { CurrentValue = () => $"Time: {TimeMode}", Example = "time night" });
            commands.Add(new("fog", "fog [auto|on|off]", "Terrain fog", value => OutsideComparison(value, SetFog), "auto", "on", "off")
                { CurrentValue = () => $"Fog: {FogMode}", Example = "fog off" });
        }
        commands.Add(new("status", "status", "World and settings",
            value => value.Length == 0 ? ReadConsoleStatus(position()) : "Usage: status") { Example = "status" });
        commands.Add(new("reset", "reset", "Reset FOV, time and fog", value => OutsideComparison(value, ResetPresentation)) { Example = "reset" });
        commands.Add(new("profile", ConsoleFrameProfile.Usage, "Measure performance", RunProfile, profileValues)
        {
            CurrentValue = profile.State,
            UsageDetails = $"{ConsoleFrameProfile.ComparisonUsage}\n{ProfileRunUsage}\n{ProfileRunTiming}",
            CompleteValue = value => value.StartsWith("run ", StringComparison.Ordinal)
                ? CompletePresetNames(value, 2)
                : profileValues,
        });
        commands.Add(new("report", "report copy", "Copy A/B report", value => CopyComparison(value, profile, copyText), "copy"));
        commands.Add(new("preset", "preset <command>", "Session presets", ApplyPreset, presetVerbs)
        {
            UsageDetails = $"{PresetUsage}\n{PresetNameUsage}",
            CompleteValue = CompletePresetValue,
        });
        commands.Add(new("inspect", "inspect", "Local biome and weather", value => OutsideComparison(value, InspectPosition)));
        commands.Add(new("pos", "pos copy", "Copy world, seed and XYZ",
            value => value != "copy" ? "Usage: pos copy"
                : copyText(ReadPosition(position())) ? "Position copied." : "Clipboard unavailable.\nTry pos copy again.", "copy"));
        return new(commands.ToArray());

        string OutsideComparison(string value, Func<string, string> execute) =>
            profile.IsComparing ? "A/B profile running." : execute(value);

        string InspectPosition(string value) => value.Length == 0 ? Inspect(position()) : "Usage: inspect";

        IOrderedEnumerable<string> PresetNames() => presets.Keys.Order(StringComparer.Ordinal);

        IEnumerable<string> CompletePresetValue(string value)
        {
            var separator = value.IndexOf(' ');
            return separator < 0 ? presetVerbs : value[..separator] switch
            {
                "load" or "delete" or "show" => CompletePresetNames(value, 1),
                "diff" => CompletePresetNames(value, 2),
                _ => presetVerbs,
            };
        }

        IEnumerable<string> CompletePresetNames(string value, int nameCount)
        {
            var prefix = value[..(value.LastIndexOf(' ') + 1)];
            var tokens = prefix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > nameCount || tokens.Length == 2 && !presets.ContainsKey(tokens[1]))
                return [];
            return PresetNames().Select(name => prefix + name);
        }

        string RunProfile(string value)
        {
            if (value != "run" && !value.StartsWith("run ", StringComparison.Ordinal))
                return profile.Handle(value);

            var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var seconds = 5;
            if (parts.Length is < 3 or > 4 || parts.Length == 4 &&
                (!int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out seconds) ||
                 seconds is not (>= 1 and <= 30)))
                return $"{ProfileRunUsage}\n{ProfileRunTiming}";
            if (!ValidPresetName(parts[1]) || !ValidPresetName(parts[2]))
                return PresetNameUsage;
            if (!presets.TryGetValue(parts[1], out var a))
                return MissingPreset(parts[1]);
            if (!presets.TryGetValue(parts[2], out var b))
                return MissingPreset(parts[2]);
            if (_caveExitWaypoint >= 0)
                return "Finish climbing first.";

            var original = ReadPresentation();
            return profile.StartComparison(seconds,
                () => ApplyProfilePreset(parts[1], a),
                () => ApplyProfilePreset(parts[2], b),
                () =>
                {
                    ApplyPresentation(original);
                    _comparisonPresetName = null;
                });
        }

        ulong ApplyProfilePreset(string name, PresentationPreset preset)
        {
            var firstSequence = checked(_sequence + 1);
            ApplyPresentation(preset);
            _comparisonPresetName = name;
            return firstSequence;
        }

        string ApplyPreset(string value)
        {
            var separator = value.IndexOf(' ');
            var verb = separator < 0 ? value : value[..separator];
            var name = separator < 0 ? "" : value[(separator + 1)..].TrimStart();
            if (verb == "list")
                return ListPresets(name);
            if (verb == "diff")
                return ComparePresets(name);
            if (separator < 0 || verb is not ("save" or "load" or "delete" or "show"))
                return PresetUsage;

            if (!ValidPresetName(name))
                return PresetNameUsage;

            if (verb == "show")
                return presets.TryGetValue(name, out var stored) ? DescribePreset(name, stored) : MissingPreset(name);
            if (profile.IsComparing)
                return "A/B profile running.";

            if (verb == "delete")
                return presets.Remove(name) ? $"Preset deleted.\n{name}" : MissingPreset(name);

            if (verb == "load")
            {
                if (!presets.TryGetValue(name, out var preset))
                    return MissingPreset(name);
                ApplyPresentation(preset);
                return $"Preset loaded.\n{name}";
            }

            var exists = presets.ContainsKey(name);
            if (!exists && presets.Count >= PresetLimit)
                return $"Preset limit: {PresetLimit}.";
            presets[name] = ReadPresentation();
            return exists ? $"Preset replaced.\n{name}" : $"Preset saved.\n{name}";
        }

        string ComparePresets(string argument)
        {
            var names = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (names.Length != 2)
                return "Usage: preset diff <a> <b>";
            if (!ValidPresetName(names[0]) || !ValidPresetName(names[1]))
                return PresetNameUsage;
            if (!presets.TryGetValue(names[0], out var a))
                return MissingPreset(names[0]);
            if (!presets.TryGetValue(names[1], out var b))
                return MissingPreset(names[1]);

            var differences = new List<string>();
            if (a.FieldOfView != b.FieldOfView)
            {
                var aDegrees = Degrees(a.FieldOfView).ToString("0.###", CultureInfo.InvariantCulture);
                var bDegrees = Degrees(b.FieldOfView).ToString("0.###", CultureInfo.InvariantCulture);
                differences.Add(aDegrees == bDegrees ? "FOV differs by <0.001°" : $"FOV {aDegrees}° | {bDegrees}°");
            }
            if (a.Fog != b.Fog)
                differences.Add($"Fog {FormatFogMode(a.Fog)} | {FormatFogMode(b.Fog)}");
            if (a.Time != b.Time)
                differences.Add($"Time {FormatTimeMode(a.Time)} | {FormatTimeMode(b.Time)}");
            return differences.Count == 0 ? "Presets match."
                : $"A {names[0]}\nB {names[1]}\n{string.Join('\n', differences)}";
        }

        string ListPresets(string argument)
        {
            var pages = Math.Max(1, (presets.Count + PresetsPerPage - 1) / PresetsPerPage);
            var page = 1;
            if (argument.Length > 0 &&
                (!int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out page) ||
                 page < 1 || page > pages))
                return pages == 1 ? "Usage: preset list" : $"Preset page: 1–{pages}.";
            if (presets.Count == 0)
                return "No presets saved.";

            var heading = $"Presets {page}/{pages}";
            if (page < pages)
                heading += $" · preset list {page + 1}";
            var names = PresetNames().Skip((page - 1) * PresetsPerPage).Take(PresetsPerPage);
            return string.Join('\n', names.Prepend(heading));
        }
    }

    private static bool ValidPresetName(string name) => name.Length is >= 1 and <= 24 &&
        name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static string MissingPreset(string name) => $"Preset not found.\n{name}";

    private PresentationPreset ReadPresentation() => new(_fieldOfView, _testFog, _previewTime);

    private void ApplyPresentation(PresentationPreset preset) =>
        (_fieldOfView, _testFog, _previewTime) = (preset.FieldOfView, preset.Fog, preset.Time);

    private static string DescribePreset(string name, PresentationPreset preset) => FormattableString.Invariant(
        $"Preset\n{name}\nFOV {Degrees(preset.FieldOfView):0.###}°\nFog {FormatFogMode(preset.Fog)}\nTime {FormatTimeMode(preset.Time)}");

    private string ReadPosition(PlanetVector position) => FormattableString.Invariant(
        $"World {PlanetName}\nSeed {Descriptor.Seed}\nX {position.X:R}\nY {position.Y:R}\nZ {position.Z:R}");

    private string Inspect(PlanetVector position)
    {
        var biome = cave is null ? new ReferenceBiomes(planet).Evaluate(position).Dominant.ToString() : "n/a";
        var altitude = FlightVector.Length(position) - Descriptor.RadiusMeters;
        var eye = WalkingEye(position);
        var regionalWeather = _weather?.Evaluate(eye, _environmentTick,
            sunDirection: orbitDemo ? OrbitDemoPreset.SunDirection(_environmentTick, 0) : null);
        var fog = regionalWeather is { } weather
            ? FormattableString.Invariant($"{weather.FogOpacity:P0}")
            : "n/a";
        var mode = _weather is null ? "n/a" : FogMode;
        return FormattableString.Invariant($"Biome {biome}\nAltitude {altitude:0.#} m\nRegional fog {fog}\nFog mode {mode}");
    }

    private static string CopyComparison(string value, ConsoleFrameProfile profile, Func<string, bool> copyReport)
    {
        if (value != "copy")
            return "Usage: report copy";
        if (profile.ReadComparisonReport(ClientBuild) is not { } report)
            return "Keep profiles in A and B.";

        return copyReport(report) ? "A/B report copied." : "Clipboard unavailable.\nTry report copy again.";
    }

    private string ReadProfileContext(PlanetVector position, ulong? fixedLiveTick)
    {
        var context = FormattableString.Invariant($"{ReadConsoleStatus(position)}\nTime tick {_environmentTick}\n{ReadDiagnostics()}");
        return _comparisonPresetName is { } name ? $"Preset {name}\nLive view tick {fixedLiveTick}\n{context}" : context;
    }

    private string FogMode => FormatFogMode(_testFog);
    private string TimeMode => FormatTimeMode(_previewTime);

    private static string FormatFogMode(bool? fog) => fog switch { true => "on", false => "off", null => "auto" };

    private static string FormatTimeMode(ulong? time) => time switch
    {
        null => "live",
        ReferenceWeather.InitialTick => "day",
        _ => "night",
    };

    private string ReadConsoleStatus(PlanetVector position)
    {
        var fog = _weather is null ? "n/a" : FogMode;
        var time = _weather is null ? "n/a" : TimeMode;
        return FormattableString.Invariant(
            $"World {PlanetName}\nSeed {Descriptor.Seed}\nGen {Descriptor.GeneratorVersion} · FOV {FieldOfViewDegrees:0.#}°\nX {position.X:0.#} Y {position.Y:0.#}\nZ {position.Z:0.#}\nFog {fog} · Time {time}");
    }

    private string ResetPresentation(string value)
    {
        if (value.Length != 0)
            return "Usage: reset";

        ApplyPresentation(new(DefaultFieldOfView, null, null));
        return "Presentation reset.";
    }

    private static float Degrees(float radians) => radians * (180 / MathF.PI);
    private float FieldOfViewDegrees => Degrees(_fieldOfView);

    private string ReadFieldOfView() => FormattableString.Invariant($"Vertical FOV: {FieldOfViewDegrees:0.#}°");

    private string ReadDiagnostics() => _debug ? "F1 diagnostics on." : "F1 diagnostics off.";

    private string ReadTimePreview() => _previewTime switch
    {
        null => "Lighting follows the world clock.",
        ReferenceWeather.InitialTick => "Day lighting preview.",
        _ => "Night lighting preview.",
    };

    private string ReadFog() => _testFog switch
    {
        true => "Fog override on.",
        false => "Fog override off.",
        null => "Fog follows local weather.",
    };

    private string SetFieldOfView(string value)
    {
        if (value == "reset")
            _fieldOfView = DefaultFieldOfView;
        else if (value.Length > 0)
        {
            if (!float.TryParse(value.Replace(',', '.'), NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var degrees) || degrees is not (>= 50 and <= 100))
                return "Usage: fov [50..100|reset]";
            _fieldOfView = degrees * (MathF.PI / 180);
        }
        return ReadFieldOfView();
    }

    private string SetDiagnostics(string value)
    {
        switch (value)
        {
            case "on": _debug = true; break;
            case "off": _debug = false; break;
            case "": break;
            default: return "Usage: stats [on|off]";
        }
        return ReadDiagnostics();
    }

    private string SetTimePreview(string value)
    {
        switch (value)
        {
            case "day": _previewTime = ReferenceWeather.InitialTick; break;
            case "night": _previewTime = ReferenceWeather.InitialTick + 18_000; break;
            case "live": _previewTime = null; break;
            case "": break;
            default: return "Usage: time [day|night|live]";
        }
        return ReadTimePreview();
    }

    private string SetFog(string value)
    {
        switch (value)
        {
            case "auto": _testFog = null; break;
            case "on": _testFog = true; break;
            case "off": _testFog = false; break;
            case "": break;
            default: return "Usage: fog [auto|on|off]";
        }
        return ReadFog();
    }
}

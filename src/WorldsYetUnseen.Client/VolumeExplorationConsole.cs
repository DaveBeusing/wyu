using System.Globalization;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    private const float DefaultFieldOfView = 1f;
    private const int PresetLimit = 16;
    private const int PresetsPerPage = 7; // Leave room for the command and page heading in the smallest console.
    private const string PresetUsage = "save / load <name>\nlist [page] / delete <name>\nshow / copy <name>\ndiff <a|live> <b>\ndiff saved <a> <b>\nimport [data|confirm|cancel]\npin / unpin <name>";
    private const string ProfileRunUsage = "profile run <a> <b> [sec]";
    private const string ProfileRunTiming = "1–30 s per preset\nDefault: 5 s";
    private const string ProfileRepeatUsage = "profile repeat <a> <b> [n]";
    private const string ProfileRepeatTiming = "2–6 pairs, default 3";
    private const string PresetNameUsage = "Name: 1–24 a-z, 0-9, hyphen";
    private float _fieldOfView = DefaultFieldOfView;
    private ulong? _previewTime;
    private string? _comparisonPresetName;
    private WorldConsoleMarks? _consoleMarks;

    private readonly record struct PresentationPreset(float FieldOfView, bool? Fog, ulong? Time);

    private WorldConsole CreateConsole(Func<PlanetVector> position, ConsoleFrameProfile profile, Func<string, bool> copyText,
        Func<string?> readPresetText, Func<string> inspectAim, Func<string> streamStatus, Func<string> streamWhy, ConsoleTelemetry telemetry,
        ConsolePinnedPresets? pinnedStore, Func<PlanetVector> forward, Func<PlanetVector, string> inspectAt,
        Func<string, bool, string> setSpectate)
    {
        var presets = new Dictionary<string, PresentationPreset>(StringComparer.Ordinal);
        var pinnedNames = new HashSet<string>(StringComparer.Ordinal);
        string[] storedEntries = [];
        var pinsReadable = pinnedStore is not null && pinnedStore.TryRead(out storedEntries);
        if (pinsReadable)
            foreach (var entry in storedEntries)
                if (TryDecodePreset(entry, out var restored))
                {
                    presets.Add(restored.Name, restored.Preset);
                    pinnedNames.Add(restored.Name);
                }
        var marks = _consoleMarks = new WorldConsoleMarks(() => PlanetName, () => Descriptor.Seed, position, forward, copyText);
        PresentationPreset? undo = null;
        (string Name, PresentationPreset Preset, PresentationPreset? Prior)? pendingImport = null;
        string[] presetVerbs = ["save", "load", "list", "delete", "show", "diff", "copy", "import", "import confirm", "import cancel", "pin", "unpin"];
        string[] profileValues = ["5", "10", "30", "cancel", "keep a", "keep b", "compare", "run", "repeat", "trace", "trace cancel"];
        var commands = new List<ConsoleCommand>
        {
            new("fov", "fov [50..100|reset]", "Vertical camera angle (deg)", value => TrackPresentation(value, SetFieldOfView), "reset")
                { CurrentValue = ReadFieldOfView, Example = "fov 90" },
            new("stats", "stats [on|off]", "F1 diagnostics", value => OutsideComparison(value, SetDiagnostics), "on", "off")
                { CurrentValue = ReadDiagnostics, Example = "stats on" },
        };
        if (_weather is not null)
        {
            commands.Add(new("time", "time [day|night|live]", "Preview the sky and lighting", value => TrackPresentation(value, SetTimePreview), "day", "night", "live")
                { CurrentValue = () => $"Time: {TimeMode}", Example = "time night" });
            commands.Add(new("fog", "fog [auto|on|off]", "Terrain fog", value => TrackPresentation(value, SetFog), "auto", "on", "off")
                { CurrentValue = () => $"Fog: {FogMode}", Example = "fog off" });
        }
        commands.Add(new("status", "status", "World and settings",
            value => value.Length == 0 ? ReadConsoleStatus(position()) : "Usage: status") { Example = "status" });
        commands.Add(new("reset", "reset", "Reset FOV, time and fog", value => TrackPresentation(value, ResetPresentation)) { Example = "reset" });
        commands.Add(new("profile", ConsoleFrameProfile.Usage, "Measure performance", RunProfile, profileValues)
        {
            CurrentValue = profile.State,
            UsageDetails = $"{ConsoleFrameProfile.ComparisonUsage}\n{ProfileRunUsage}\n{ProfileRepeatUsage}\n{ProfileRepeatTiming}\nprofile trace [sec]\nprofile trace cancel",
            CompleteValue = value => value.StartsWith("run ", StringComparison.Ordinal) ||
                value.StartsWith("repeat ", StringComparison.Ordinal)
                ? CompletePresetNames(value, 2)
                : profileValues,
        });
        commands.Add(new("report", "report <command>", "Copy or export diagnostics", Report, "copy", "here", "trace", "export")
        {
            UsageDetails = "report copy · A/B results\nreport here · this location\nreport trace · last trace\nreport export · Reports folder",
        });
        commands.Add(new("preset", "preset <command>", "Session presets", ApplyPreset, presetVerbs)
        {
            UsageDetails = $"{PresetUsage}\n{PresetNameUsage}",
            CompleteValue = CompletePresetValue,
        });
        commands.Add(new("inspect", "inspect <command>", "Inspect local terrain", value => OutsideComparison(value, InspectPosition), "aim", "at")
        {
            UsageDetails = "inspect · current location\ninspect aim · terrain hit\ninspect at <x> <y> <z>",
        });
        commands.Add(new("pos", "pos copy", "Copy world, seed and XYZ",
            value => value != "copy" ? "Usage: pos copy"
                : copyText(ReadPosition(position())) ? "Position copied." : "Clipboard unavailable.\nTry pos copy again.", "copy"));
        commands.Add(new("undo", "undo", "Restore last view edit", value => OutsideComparison(value, UndoPresentation)));
        commands.Add(new("stream", "stream <command>", "Terrain streaming", Stream, "status", "watch", "watch cancel", "why")
        {
            UsageDetails = "stream status\nstream why\nstream watch [sec]\nstream watch cancel",
        });
        commands.Add(new("mark", "mark <command>", "Session locations", marks.Handle, "save", "list", "show", "locate", "copy", "rename", "delete", "track", "track off")
        {
            UsageDetails = WorldConsoleMarks.Usage,
            CompleteValue = marks.CompleteValue,
        });
        commands.Add(new("save", "save status", "Checkpoint status", value =>
            value == "status" ? ReadSaveStatus() : "Usage: save status", "status"));
        commands.Add(new("trace", "trace mark <label>", "Tag the running trace", value =>
            value.StartsWith("mark ", StringComparison.Ordinal) ? telemetry.AddTraceMark(value[5..].TrimStart()) : "Usage: trace mark <label>", "mark"));
        if (freeTestWorld)
        {
            commands.Add(new("spectate", "spectate [on|off]", "Free camera",
                value => setSpectate(value, Measuring() || _queuedConsoleOre is not null), "on", "off"));
            commands.Add(new("give", "give ore <1–64>", "Grant construction ore", value => QueueGive(value, Measuring()), "ore"));
        }
        return new(commands.ToArray());

        bool Measuring() => profile.IsActive || telemetry.IsActive;

        string OutsideComparison(string value, Func<string, string> execute) =>
            profile.IsComparing ? "A/B profile running." : execute(value);

        string Report(string value)
        {
            if (value == "copy")
                return CopyComparison(value, profile, copyText);
            if (value == "trace")
                return telemetry.LastTraceReport is { } trace
                    ? copyText(trace) ? "Trace report copied." : "Clipboard unavailable."
                    : "No completed trace.";
            if (value is not ("here" or "export"))
                return "report copy / here / trace\nreport export";
            if (IsSpectating)
                return "Exit spectate first.";
            if (Measuring())
                return "Finish measuring first.";
            var report = FormattableString.Invariant(
                $"WYU location report\nBuild {ClientBuild}\n{ReadPosition(position())}\nCaptured UTC {DateTimeOffset.UtcNow:O}\nGenerator {Descriptor.GeneratorVersion}\nFOV {FieldOfViewDegrees:0.###}°\nFog {(_weather is null ? "n/a" : FogMode)} · Time {(_weather is null ? "n/a" : TimeMode)}\n\nTerrain aim\n{inspectAim()}\n\nStreaming\n{streamStatus()}");
            if (value == "export")
                return ExportConsoleReport(report, telemetry.LastTraceReport);
            return copyText(report) ? "Location report copied." : "Clipboard unavailable.";
        }

        string Stream(string value)
        {
            if (value == "status")
                return profile.IsComparing ? "A/B profile running." : streamStatus();
            if (value == "why")
                return streamWhy();
            if (value != "watch" && !value.StartsWith("watch ", StringComparison.Ordinal))
                return "Usage: stream status / why\nstream watch [sec]";
            return profile.IsActive || IsSpectating || _queuedConsoleOre is not null
                ? "Finish the active action first." : telemetry.HandleWatch(value[5..].TrimStart());
        }

        string InspectPosition(string value)
        {
            if (value.Length == 0)
                return Inspect(position());
            if (value == "aim")
                return IsSpectating ? "Exit spectate for avatar aim." : inspectAim();
            if (value.StartsWith("at ", StringComparison.Ordinal) && TryReadConsolePoint(value[3..], out var point))
                return inspectAt(point);
            return "inspect / inspect aim\ninspect at <x> <y> <z>";
        }

        string TrackPresentation(string value, Func<string, string> execute)
        {
            if (profile.IsComparing)
                return "A/B profile running.";
            var previous = ReadPresentation();
            var result = execute(value);
            if (ReadPresentation() != previous)
                undo = previous;
            return result;
        }

        string UndoPresentation(string value)
        {
            if (value.Length != 0)
                return "Usage: undo";
            if (undo is not { } previous)
                return "No view edit to undo.";
            ApplyPresentation(previous);
            undo = null;
            return "View settings restored.";
        }

        IOrderedEnumerable<string> PresetNames() => presets.Keys.Order(StringComparer.Ordinal);

        IEnumerable<string> CompletePresetValue(string value)
        {
            var separator = value.IndexOf(' ');
            return separator < 0 ? presetVerbs : value[..separator] switch
            {
                "load" or "delete" or "show" or "copy" or "pin" => CompletePresetNames(value, 1),
                "unpin" => CompletePresetNames(value, 1, pinnedNames.Order(StringComparer.Ordinal)),
                "diff" => CompletePresetDiff(value),
                _ => presetVerbs,
            };
        }

        IEnumerable<string> CompletePresetDiff(string value)
        {
            var prefix = value[..(value.LastIndexOf(' ') + 1)];
            var tokens = prefix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            IEnumerable<string> names = tokens.Length switch
            {
                1 => PresetNames().Prepend("saved").Prepend("live").Distinct(StringComparer.Ordinal),
                2 when tokens[1] is "live" or "saved" || presets.ContainsKey(tokens[1]) => PresetNames(),
                3 when tokens[1] == "saved" && presets.ContainsKey(tokens[2]) => PresetNames(),
                _ => [],
            };
            return names.Select(name => prefix + name);
        }

        IEnumerable<string> CompletePresetNames(string value, int nameCount, IEnumerable<string>? names = null)
        {
            var prefix = value[..(value.LastIndexOf(' ') + 1)];
            var tokens = prefix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > nameCount || tokens.Length == 2 && !presets.ContainsKey(tokens[1]))
                return [];
            return (names ?? PresetNames()).Select(name => prefix + name);
        }

        string RunProfile(string value)
        {
            if (IsSpectating || _queuedConsoleOre is not null)
                return "Finish the active action first.";
            if (value == "trace" || value.StartsWith("trace ", StringComparison.Ordinal))
                return profile.IsActive ? "Profile already running." : telemetry.HandleTrace(value[5..].TrimStart());
            if (telemetry.IsActive)
                return "Diagnostic capture running.";
            var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts[0] is not ("run" or "repeat"))
                return profile.Handle(value);
            var repeat = parts[0] == "repeat";
            var amount = repeat ? 3 : 5;
            var minimum = repeat ? 2 : 1;
            var maximum = repeat ? 6 : 30;
            if (parts.Length is < 3 or > 4 || parts.Length == 4 &&
                (!int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out amount) ||
                 amount < minimum || amount > maximum))
                return repeat ? $"{ProfileRepeatUsage}\n{ProfileRepeatTiming}" : $"{ProfileRunUsage}\n{ProfileRunTiming}";
            if (!ValidPresetName(parts[1]) || !ValidPresetName(parts[2]))
                return PresetNameUsage;
            if (!presets.TryGetValue(parts[1], out var a))
                return MissingPreset(parts[1]);
            if (!presets.TryGetValue(parts[2], out var b))
                return MissingPreset(parts[2]);
            if (!CanApplyPreset(a) || !CanApplyPreset(b))
                return "Preset needs time or fog.\nUnavailable in this world.";
            if (_caveExitWaypoint >= 0)
                return "Finish climbing first.";

            var original = ReadPresentation();
            ulong ApplyA() => ApplyProfilePreset(parts[1], a);
            ulong ApplyB() => ApplyProfilePreset(parts[2], b);
            void Restore()
            {
                ApplyPresentation(original);
                _comparisonPresetName = null;
            }
            return repeat ? profile.StartRepeatedComparison(amount, ApplyA, ApplyB, Restore)
                : profile.StartComparison(amount, ApplyA, ApplyB, Restore);
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
            if (verb == "import")
                return OutsideComparison(name, ImportPreset);
            if (separator < 0 || verb is not ("save" or "load" or "delete" or "show" or "copy" or "pin" or "unpin"))
                return PresetUsage;

            if (!ValidPresetName(name))
                return PresetNameUsage;

            if (verb == "show")
                return presets.TryGetValue(name, out var stored) ? DescribePreset(name, stored) : MissingPreset(name);
            if (profile.IsComparing)
                return "A/B profile running.";

            if (verb is "pin" or "unpin")
                return ChangePin(verb, name);

            if (verb == "copy")
                return presets.TryGetValue(name, out var copied)
                    ? copyText(EncodePreset(name, copied)) ? "Preset copied." : "Clipboard unavailable."
                    : MissingPreset(name);

            if (verb == "delete")
                return presets.Remove(name) ? $"Preset deleted.\n{name}" : MissingPreset(name);

            if (verb == "load")
            {
                if (!presets.TryGetValue(name, out var preset))
                    return MissingPreset(name);
                if (!CanApplyPreset(preset))
                    return "Preset needs time or fog.\nUnavailable in this world.";
                var previous = ReadPresentation();
                if (preset != previous)
                    undo = previous;
                ApplyPresentation(preset);
                return $"Preset loaded.\n{name}";
            }

            var exists = presets.ContainsKey(name);
            if (!exists && presets.Count >= PresetLimit)
                return $"Preset limit: {PresetLimit}.";
            presets[name] = ReadPresentation();
            return exists ? $"Preset replaced.\n{name}" : $"Preset saved.\n{name}";
        }

        string ChangePin(string verb, string name)
        {
            if (profile.IsActive || telemetry.IsActive)
                return "Finish measuring first.";
            if (pinnedStore is null)
                return "Pinned presets unavailable.";
            var result = ConsolePresetPinResult.NotFound;
            if (verb == "unpin")
                result = pinnedStore.Unpin(name);
            else if (presets.TryGetValue(name, out var preset))
                result = pinnedStore.Pin(EncodePreset(name, preset));
            else
                return MissingPreset(name);

            if (result is ConsolePresetPinResult.Pinned or ConsolePresetPinResult.Unpinned or ConsolePresetPinResult.NotFound)
            {
                pinsReadable = pinnedStore.TryRead(out var entries);
                if (pinsReadable)
                {
                    pinnedNames.Clear();
                    foreach (var entry in entries)
                        if (ReadPinnedPresetName(entry) is { } pinnedName)
                            pinnedNames.Add(pinnedName);
                }
                else
                {
                    pinnedNames.Remove(name);
                    if (result == ConsolePresetPinResult.Pinned && pinnedNames.Count < PresetLimit)
                        pinnedNames.Add(name);
                }
            }
            return result switch
            {
                ConsolePresetPinResult.Pinned => $"Preset pinned.\n{name}",
                ConsolePresetPinResult.Unpinned => $"Preset unpinned.\n{name}",
                ConsolePresetPinResult.NotFound => $"Preset is not pinned.\n{name}",
                ConsolePresetPinResult.LimitReached => $"Pinned limit: {PresetLimit}.",
                _ => "Pinned presets unavailable.",
            };
        }

        string ImportPreset(string argument)
        {
            if (argument == "cancel")
            {
                pendingImport = null;
                return "Import cancelled.";
            }
            if (argument == "confirm")
            {
                if (pendingImport is not { } pending)
                    return "Preview an import first.";
                var prior = FindPreset(pending.Name);
                if (prior != pending.Prior)
                {
                    pendingImport = null;
                    return "Preset changed.\nPreview the import again.";
                }
                if (prior is null && presets.Count >= PresetLimit)
                    return $"Preset limit: {PresetLimit}.";
                presets[pending.Name] = pending.Preset;
                pendingImport = null;
                return prior is not null ? $"Preset replaced.\n{pending.Name}" : $"Preset imported.\n{pending.Name}";
            }

            pendingImport = null;
            var data = argument.Length == 0 ? readPresetText() : argument;
            if (!TryDecodePreset(data, out var imported))
                return "Invalid preset data.\nUse text from preset copy.";
            var priorPreset = FindPreset(imported.Name);
            pendingImport = (imported.Name, imported.Preset, priorPreset);
            var action = priorPreset is not null ? "Replace stored preset?" : "Save imported preset?";
            return $"{DescribePreset(imported.Name, imported.Preset)}\n{action}\npreset import confirm\npreset import cancel";
        }

        PresentationPreset? FindPreset(string name) => presets.TryGetValue(name, out var preset) ? preset : null;

        string ComparePresets(string argument)
        {
            var names = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var namedOnly = names.Length == 3 && names[0] == "saved";
            if (names.Length != 2 && !namedOnly)
                return "preset diff <a|live> <b>\npreset diff saved <a> <b>";
            var left = names[^2];
            var right = names[^1];
            var live = !namedOnly && left == "live";
            if (!ValidPresetName(left) || !ValidPresetName(right))
                return PresetNameUsage;
            var first = live ? ReadPresentation() : FindPreset(left);
            if (first is not { } a)
                return MissingPreset(left);
            if (!presets.TryGetValue(right, out var b))
                return MissingPreset(right);

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
            var hint = live && presets.ContainsKey("live") ? "\nStored live: diff saved" : "";
            return differences.Count == 0 ? live ? $"Current view matches.{hint}" : "Presets match."
                : $"{(live ? "A current view" : $"A {left}")}\nB {right}{hint}\n{string.Join('\n', differences)}";
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
                return pinsReadable || pinnedStore is null ? "No presets saved." : "Pinned presets unreadable.";

            var heading = $"Presets {page}/{pages}";
            if (page < pages)
                heading += $" · preset list {page + 1}";
            var names = PresetNames().Skip((page - 1) * PresetsPerPage).Take(PresetsPerPage)
                .Select(name => pinnedNames.Contains(name) ? $"{name} *" : name);
            return string.Join('\n', names.Prepend(heading));
        }
    }

    private static string? ReadPinnedPresetName(string encoded) =>
        TryDecodePreset(encoded, out var preset) &&
        string.Equals(encoded, EncodePreset(preset.Name, preset.Preset), StringComparison.Ordinal) ? preset.Name : null;

    private static string EncodePreset(string name, PresentationPreset preset) => FormattableString.Invariant(
        $"wyu1 {name} {preset.FieldOfView:R} {FormatFogMode(preset.Fog)} {FormatTimeMode(preset.Time)}");

    private static bool TryDecodePreset(string? data, out (string Name, PresentationPreset Preset) imported)
    {
        imported = default;
        if (data is null || data.Length > 80 || data.Contains('\r') || data.Contains('\n'))
            return false;

        var fields = data.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5 || fields[0] != "wyu1" || !ValidPresetName(fields[1]) ||
            !float.TryParse(fields[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var fov) ||
            fov is not (>= 50 * (MathF.PI / 180) and <= 100 * (MathF.PI / 180)) ||
            fields[3] is not ("auto" or "on" or "off") || fields[4] is not ("live" or "day" or "night"))
            return false;

        bool? fog = fields[3] == "auto" ? null : fields[3] == "on";
        ulong? time = fields[4] switch
        {
            "day" => ReferenceWeather.InitialTick,
            "night" => ReferenceWeather.InitialTick + 18_000,
            _ => null,
        };
        imported = (fields[1], new(fov, fog, time));
        return true;
    }

    private static bool ValidPresetName(string name) => name.Length is >= 1 and <= 24 &&
        name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static string MissingPreset(string name) => $"Preset not found.\n{name}";

    private PresentationPreset ReadPresentation() => new(_fieldOfView, _testFog, _previewTime);

    private bool CanApplyPreset(PresentationPreset preset) => _weather is not null || preset is { Fog: null, Time: null };

    private void ApplyPresentation(PresentationPreset preset) =>
        (_fieldOfView, _testFog, _previewTime) = (preset.FieldOfView, preset.Fog, preset.Time);

    private static string DescribePreset(string name, PresentationPreset preset) => FormattableString.Invariant(
        $"Preset\n{name}\nFOV {Degrees(preset.FieldOfView):0.###}°\nFog {FormatFogMode(preset.Fog)}\nTime {FormatTimeMode(preset.Time)}");

    private string ReadPosition(PlanetVector position) => FormattableString.Invariant(
        $"World {PlanetName}\nSeed {Descriptor.Seed}\nX {position.X:R}\nY {position.Y:R}\nZ {position.Z:R}");

    private string ReadTraceContext(PlanetVector position) => FormattableString.Invariant(
        $"Build {ClientBuild}\n{ReadPosition(position)}\nGenerator {Descriptor.GeneratorVersion}\nCaptured UTC {DateTimeOffset.UtcNow:O}");

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

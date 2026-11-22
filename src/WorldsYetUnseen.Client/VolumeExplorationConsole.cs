using System.Globalization;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    private const float DefaultFieldOfView = 1f;
    private float _fieldOfView = DefaultFieldOfView;
    private ulong? _previewTime;

    private WorldConsole CreateConsole(Func<PlanetVector> position, ConsoleFrameProfile profile, Func<string, bool> copyReport)
    {
        var commands = new List<ConsoleCommand>
        {
            new("fov", "fov [50..100|reset]", "Vertical camera angle (deg)", SetFieldOfView, "reset")
                { CurrentValue = ReadFieldOfView, Example = "fov 90" },
            new("stats", "stats [on|off]", "F1 diagnostics", SetDiagnostics, "on", "off")
                { CurrentValue = ReadDiagnostics, Example = "stats on" },
        };
        if (_weather is not null)
        {
            commands.Add(new("time", "time [day|night|live]", "Preview the sky and lighting", SetTimePreview, "day", "night", "live")
                { CurrentValue = () => $"Time: {TimeMode}", Example = "time night" });
            commands.Add(new("fog", "fog [auto|on|off]", "Terrain fog", SetFog, "auto", "on", "off")
                { CurrentValue = () => $"Fog: {FogMode}", Example = "fog off" });
        }
        commands.Add(new("status", "status", "World and settings",
            value => value.Length == 0 ? ReadConsoleStatus(position()) : "Usage: status") { Example = "status" });
        commands.Add(new("reset", "reset", "Reset FOV, time and fog", ResetPresentation) { Example = "reset" });
        commands.Add(new("profile", ConsoleFrameProfile.Usage, "Sample 1–30 seconds", profile.Handle,
            "5", "10", "30", "cancel", "keep a", "keep b", "compare")
        {
            CurrentValue = profile.State, Example = "profile 10", UsageDetails = ConsoleFrameProfile.ComparisonUsage,
        });
        commands.Add(new("report", "report copy", "Copy A/B report", value => CopyComparison(value, profile, copyReport), "copy"));
        return new(commands.ToArray());
    }

    private static string CopyComparison(string value, ConsoleFrameProfile profile, Func<string, bool> copyReport)
    {
        if (value != "copy")
            return "Usage: report copy";
        if (profile.ReadComparisonReport(ClientBuild) is not { } report)
            return "Keep profiles in A and B.";

        return copyReport(report) ? "A/B report copied." : "Clipboard unavailable.\nTry report copy again.";
    }

    private string ReadProfileContext(PlanetVector position) => FormattableString.Invariant(
        $"{ReadConsoleStatus(position)}\nTime tick {_environmentTick}\n{ReadDiagnostics()}");

    private string FogMode => _testFog switch { true => "on", false => "off", null => "auto" };

    private string TimeMode => _previewTime switch
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

        _fieldOfView = DefaultFieldOfView;
        _testFog = null;
        _previewTime = null;
        return "Presentation reset.";
    }

    private float FieldOfViewDegrees => _fieldOfView * (180 / MathF.PI);

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

using System.Globalization;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    private const float DefaultFieldOfView = 1f;
    private float _fieldOfView = DefaultFieldOfView;
    private ulong? _previewTime;

    private WorldConsole CreateConsole()
    {
        var commands = new List<ConsoleCommand>
        {
            new("fov", "fov [50..100|reset]", "Vertical camera angle (deg)", SetFieldOfView, "reset"),
            new("stats", "stats [on|off]", "F1 diagnostics", SetDiagnostics, "on", "off"),
        };
        if (_weather is not null)
        {
            commands.Add(new("time", "time [day|night|live]", "Preview the sky and lighting", SetTimePreview, "day", "night", "live"));
            commands.Add(new("fog", "fog [auto|on|off]", "Terrain fog", SetFog, "auto", "on", "off"));
        }
        return new(commands.ToArray());
    }

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
        return FormattableString.Invariant($"Vertical FOV: {_fieldOfView * (180 / MathF.PI):0.#}°");
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
        return _debug ? "F1 diagnostics on." : "F1 diagnostics off.";
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
        return _previewTime switch
        {
            null => "Lighting follows the world clock.",
            ReferenceWeather.InitialTick => "Day lighting preview.",
            _ => "Night lighting preview.",
        };
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
        return _testFog switch
        {
            true => "Fog override on.",
            false => "Fog override off.",
            null => "Fog follows local weather.",
        };
    }
}

using System.Numerics;

namespace WorldsYetUnseen.Gameplay;

/// <summary>Stateless presentation of solar elevation; it never advances the game clock.</summary>
public static class ReferenceDaylight
{
    public const string ProfileId = "wyu.far-horizon.daylight.v1";

    public static ReferenceDaylightSample Evaluate(float solarElevation)
    {
        if (!float.IsFinite(solarElevation) || solarElevation is < -1.0001f or > 1.0001f)
            throw new ArgumentOutOfRangeException(nameof(solarElevation));
        var elevation = Math.Clamp(solarElevation, -1, 1);
        var daylight = Smooth(-0.15f, 0.20f, elevation);
        var strength = 0.18f + 0.82f * daylight;
        var warm = Smooth(-0.15f, -0.025f, elevation) * (1 - Smooth(0.04f, 0.30f, elevation));
        var direct = 0.65f * strength * Smooth(0, 0.12f, elevation);
        var directColour = Vector3.Lerp(new(1, 0.38f, 0.12f), Vector3.One, Smooth(0.01f, 0.30f, elevation));
        var ambientColour = Vector3.Lerp(new(0.72f, 0.80f, 1), Vector3.One, daylight);
        ambientColour = Vector3.Lerp(ambientColour, new(1, 0.70f, 0.50f), 0.45f * warm);
        var background = Vector3.Lerp(new(0.025f, 0.035f, 0.075f), new(0.18f, 0.27f, 0.41f), daylight)
            + new Vector3(0.20f, 0.05f, -0.015f) * warm * daylight;
        return new(new(ambientColour * strength, 1), new(background, 1),
            directColour * direct, ambientColour * (strength - direct));
    }

    private static float Smooth(float start, float end, float value)
    {
        var t = Math.Clamp((value - start) / (end - start), 0, 1);
        return t * t * (3 - 2 * t);
    }
}

public readonly record struct ReferenceDaylightSample(Vector4 Tint, Vector4 Background, Vector3 Direct, Vector3 Ambient);

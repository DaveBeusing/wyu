namespace WorldsYetUnseen.Client;

internal sealed partial class VolumeExploration
{
    private const string RenderUsage = "Usage: render [sky|textures|clouds]";
    private static readonly string[] RenderFeatures = ["sky", "textures", "clouds"];
    private bool? _renderSkyOverride;

    private bool RenderSkyEnabled => reference is not null && (_renderSkyOverride ?? ReferenceSky.Enabled);

    private string ReadRenderOptions() => $"{ReadRenderSky()}\n{ReadTerrainTextures()}\nClouds: unavailable.";

    private string[] CompleteRenderOption(string value)
    {
        if (value.StartsWith("sky ", StringComparison.Ordinal))
            return reference is null ? [] : ["sky auto", "sky on", "sky off"];
        if (value.StartsWith("textures ", StringComparison.Ordinal))
            return OrbitMaterialHost is null ? [] : ["textures on", "textures off"];
        return RenderFeatures;
    }

    private string ReadRenderSky()
    {
        if (reference is null)
            return "Sky: unavailable in this world.";
        var state = RenderSkyEnabled ? "on" : "off";
        return _renderSkyOverride is null ? $"Sky: {state} (auto)." : $"Sky: {state}.";
    }

    private string ReadTerrainTextures() => OrbitMaterialHost?.TerrainTexturesEnabled switch
    {
        true => "Textures: on.",
        false => "Textures: off.",
        null => "Textures: unavailable.",
    };

    private string SetRenderOption(string input, bool measuring)
    {
        if (input.Length == 0)
            return ReadRenderOptions();

        var separator = input.IndexOf(' ');
        var feature = separator < 0 ? input : input[..separator];
        var value = separator < 0 ? "" : input[(separator + 1)..].TrimStart();
        return feature switch
        {
            "sky" => SetRenderSky(value, measuring),
            "textures" => SetRenderTextures(value, measuring),
            "clouds" => "Clouds: unavailable.",
            _ => RenderUsage,
        };
    }

    private string SetRenderSky(string value, bool measuring)
    {
        if (value.Length == 0 || reference is null)
            return ReadRenderSky();
        if (value is not ("auto" or "on" or "off"))
            return "Usage: render sky [auto|on|off]";
        if (measuring)
            return "Finish the active measurement first.";

        _renderSkyOverride = value switch { "on" => true, "off" => false, _ => null };
        return ReadRenderSky();
    }

    private string SetRenderTextures(string value, bool measuring)
    {
        if (value.Length == 0 || OrbitMaterialHost is not { } host)
            return ReadTerrainTextures();
        if (value is not ("on" or "off"))
            return "Usage: render textures [on|off]";
        if (measuring)
            return "Finish the active measurement first.";

        return host.TrySetTerrainTextures(value == "on")
            ? ReadTerrainTextures() : "Textures could not be changed. Try again.";
    }
}

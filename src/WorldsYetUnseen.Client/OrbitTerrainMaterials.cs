using System.Numerics;
using System.Text.Json;
using Groundwork.Rendering.D3D12;
using Groundwork.Rendering.Materials;
using Groundwork.Rendering.Textures;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Client;

internal sealed class OrbitTerrainMaterials
{
    internal const ushort OrbitalMarkerMaterial = 3;

    private const string ResourcePrefix = "WorldsYetUnseen.Client.Assets.OrbitDemo.";
    private const int MapsPerMaterial = 3;
    private const int MapCount = 2 * MapsPerMaterial;
    private const int MaximumCookedBytes = 2 * 1024 * 1024;
    private const int MaximumManifestBytes = 16 * 1024;
    private const int NativeBytesPerMap = 1536 * 1024;
    private const int CoarseBytesPerMap = 256 * 1024;
    private static readonly D3D12TextureMipPolicy MipPolicy = new(0, 2, 16, 24);
    private const int MaterialConstantsHeadroom = 1024 * 1024;
    private static readonly TextureLimits CookLimits = new(MaximumCookedBytes, MaximumCookedBytes,
        MaximumCookedBytes, 512, 10);

    private readonly MaterialPalette _palette;
    private readonly D3D12TextureTarget[] _targets;
    private readonly CookedTexture[] _maps;

    private OrbitTerrainMaterials(MaterialPalette palette, D3D12TextureTarget[] targets, CookedTexture[] maps)
    {
        _palette = palette;
        _targets = targets;
        _maps = maps;
    }

    internal static OrbitTerrainMaterials Load()
    {
        using var manifestStream = Open("manifest.json");
        if (manifestStream.Length is <= 0 or > MaximumManifestBytes)
            throw new InvalidDataException("Orbit terrain texture manifest exceeds its size limit.");
        using var manifest = JsonDocument.Parse(manifestStream);
        var entries = manifest.RootElement.GetProperty("assets");
        var targets = new D3D12TextureTarget[MapCount];
        var maps = new CookedTexture[MapCount];
        var scratch = new byte[MaximumCookedBytes];
        var rock = LoadMaterial(ReferenceTerrainMaterial.Basalt, "rock_01", new(0.33f, 0.25f, 0.17f), 0);
        var scree = LoadMaterial(ReferenceTerrainMaterial.OchreStone, "rocks_ground_05", new(0.43f, 0.32f, 0.20f),
            MapsPerMaterial);
        var marker = new MaterialPaletteEntry(OrbitalMarkerMaterial,
            new(Vector3.Zero, 0, 1, 1, new(1f, 0.9f, 0.1f), 0));
        return new(new MaterialPalette(1, [rock, scree, marker]), targets, maps);

        MaterialPaletteEntry LoadMaterial(ReferenceTerrainMaterial id, string name, Vector3 fallback, int start)
        {
            var baseColor = LoadMap(id, name + "_diff.gwtx", MaterialTextureSemantic.BaseColor,
                TextureSemantic.Color, TextureColorSpace.Srgb, start);
            var normal = LoadMap(id, name + "_normal.gwtx", MaterialTextureSemantic.Normal,
                TextureSemantic.Normal, TextureColorSpace.Linear, start + 1);
            var roughness = LoadMap(id, name + "_rough.gwtx", MaterialTextureSemantic.Roughness,
                TextureSemantic.Data, TextureColorSpace.Linear, start + 2);
            return new((ushort)id, new(fallback, 0, 0.8f, 1, Vector3.Zero, 0.8f,
                new MaterialTextureSet(baseColor: baseColor, roughness: roughness, normal: normal)));
        }

        MaterialTextureReference LoadMap(ReferenceTerrainMaterial id, string name,
            MaterialTextureSemantic materialSemantic, TextureSemantic textureSemantic, TextureColorSpace colorSpace, int index)
        {
            var digest = FindDigest(entries, name);
            using var source = Open(name);
            if (source.Length is <= 0 or > MaximumCookedBytes)
                throw new InvalidDataException($"Orbit terrain map exceeds its size limit: {name}.");
            var bytes = scratch.AsSpan(0, (int)source.Length);
            source.ReadExactly(bytes);
            var error = TextureCooker.ReadCooked(bytes, "sha256:" + digest, CookLimits, out var texture);
            if (error != TextureCookError.None ||
                texture is not { Format: TextureFormat.Rgba8, Width: 512, Height: 512 } ||
                texture.Semantic != textureSemantic || texture.ColorSpace != colorSpace || texture.Mips.Length != 10)
                throw new InvalidDataException($"Invalid Orbit terrain map: {name} ({error}).");

            var reference = new MaterialTextureReference(materialSemantic, texture.AssetId);
            maps[index] = texture;
            targets[index] = new(new((ushort)id, materialSemantic), 1, reference);
            return reference;
        }
    }

    internal D3D12TextureHandoff CreateStream()
    {
        // The render owner admits one copy at a time. Keep one extra slot for
        // a superseded cut's copy while its fence retires.
        var slots = _targets.Length + 1;
        return new(new(_targets.Length, slots, slots * MaximumCookedBytes, 0, slots * MaximumCookedBytes,
            slots * NativeBytesPerMap, slots * NativeBytesPerMap + MaterialConstantsHeadroom, slots));
    }

    // The decoded maps and target sequences remain fixed for this session.
    // Groundwork carries exact residents on a same-epoch cut and coalesces their requests.
    internal enum Scheduling { Scheduled, Retry, Stopped }

    internal Scheduling Publish(D3D12TextureHandoff stream, D3D12TextureView view, bool orbital)
    {
        if (!stream.PublishTerrainAppearance(view, _palette, _targets))
            return Scheduling.Stopped;
        // Coarse backing comes first on both renderers. The fixed orbit proxy
        // stays coarse, while the walking renderer promotes each resident map.
        var distance = orbital ? MipPolicy.DemoteBeyond : 0;
        var targetMip = orbital ? MipPolicy.CoarseMip : MipPolicy.FineMip;
        var pending = false;
        for (var index = 0; index < _targets.Length; index++)
        {
            var target = _targets[index];
            if (stream.TryReadResidency(view, target, out var residency) && residency.ResidentMip == targetMip)
                continue;

            var map = _maps[index];
            var bytes = map.CanonicalBytes.Length;
            var fine = new D3D12TextureReservation(bytes, 0, bytes, NativeBytesPerMap, NativeBytesPerMap);
            var coarse = fine with { StagingBytes = CoarseBytesPerMap, NativeBytes = CoarseBytesPerMap };
            var result = stream.TryRequestTier(view, target, MipPolicy, distance, fine, coarse, out var ticket);
            if (result == D3D12TextureResult.Unavailable)
                continue;
            pending = true;
            if (result is D3D12TextureResult.Coalesced or D3D12TextureResult.Quota)
                continue;
            if (result != D3D12TextureResult.Accepted || stream.TryComplete(ticket, map) != D3D12TextureResult.Accepted)
                return Scheduling.Stopped;
        }
        return pending ? Scheduling.Retry : Scheduling.Scheduled;
    }

    private static Stream Open(string name) =>
        typeof(OrbitTerrainMaterials).Assembly.GetManifestResourceStream(ResourcePrefix + name)
        ?? throw new InvalidDataException($"Missing Orbit terrain resource: {name}.");

    private static string FindDigest(JsonElement entries, string name)
    {
        string? digest = null;
        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.GetProperty("file").ValueEquals(name))
                continue;
            if (digest is not null)
                throw new InvalidDataException($"Duplicate Orbit texture manifest entry: {name}.");
            digest = entry.GetProperty("cookedSha256").GetString();
            if (digest is not { Length: 64 })
                throw new InvalidDataException($"Invalid Orbit texture manifest digest: {name}.");
        }
        return digest ?? throw new InvalidDataException($"Missing Orbit texture manifest entry: {name}.");
    }
}

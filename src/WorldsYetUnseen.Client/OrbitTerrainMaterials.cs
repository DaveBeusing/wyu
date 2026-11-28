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
    private const string ManifestName = "manifest.json";
    private const int MapsPerMaterial = 3;
    internal const int MapCount = 2 * MapsPerMaterial;
    internal const int RetryFrameInterval = 16;
    internal const int MaximumPreparationFrames = (MapCount * 2 + 1) * RetryFrameInterval;
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
    private MaterialPalette? _pressurePalette;
    private D3D12TextureTarget[]? _pressureTargets;

    private OrbitTerrainMaterials(MaterialPalette palette, D3D12TextureTarget[] targets, CookedTexture[] maps)
    {
        _palette = palette;
        _targets = targets;
        _maps = maps;
    }

    internal static OrbitTerrainMaterials Load()
    {
        using var manifest = ReadManifest();
        var root = manifest.RootElement;
        if (!TryReadManifestProperty(root, "assets", out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Invalid Orbit texture manifest {ManifestName}: expected an assets list.");
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
                throw new InvalidDataException($"Invalid Orbit texture size: {name}.");
            var bytes = scratch.AsSpan(0, (int)source.Length);
            source.ReadExactly(bytes);
            var error = TextureCooker.ReadCooked(bytes, "sha256:" + digest, CookLimits, out var texture);
            if (error != TextureCookError.None)
                throw new InvalidDataException($"Orbit texture failed validation: {name} ({error}).");
            if (texture is not { Format: TextureFormat.Rgba8, Width: 512, Height: 512 } ||
                texture.Semantic != textureSemantic || texture.ColorSpace != colorSpace || texture.Mips.Length != 10)
                throw new InvalidDataException($"Orbit texture has unexpected metadata: {name}.");

            var reference = new MaterialTextureReference(materialSemantic, texture.AssetId);
            maps[index] = texture;
            targets[index] = new(new((ushort)id, materialSemantic), 1, reference);
            return reference;
        }
    }

    internal D3D12TextureHandoff CreateStream(bool pressure = false)
    {
        // The render owner admits one copy at a time. Keep one extra slot for
        // a superseded cut's copy while its fence retires.
        var slots = _targets.Length + 1;
        _pressurePalette = null;
        _pressureTargets = null;
        if (!pressure)
            return new(new(_targets.Length, slots, slots * MaximumCookedBytes, 0, slots * MaximumCookedBytes,
                slots * NativeBytesPerMap, slots * NativeBytesPerMap + MaterialConstantsHeadroom, slots));

        var extra = R09IndependentTexturePressure.Credit;
        return new(new(_targets.Length + 1, slots + 1, slots * MaximumCookedBytes + extra.SourceBytes,
            extra.ScratchBytes, slots * MaximumCookedBytes + extra.PayloadBytes,
            slots * NativeBytesPerMap + extra.StagingBytes,
            slots * NativeBytesPerMap + MaterialConstantsHeadroom + extra.NativeBytes, slots + 1));
    }

    internal bool PublishPressureTarget(D3D12TextureHandoff stream, D3D12TextureView view, D3D12TextureTarget? target)
    {
        // Preserve every production entry and its existing texture target sequence.
        // The pressure ID does not occur in the Orbit terrain geometry.
        var revision = checked((_pressurePalette?.VisualRevision ?? _palette.VisualRevision) + 1);
        var entries = _palette.Entries.ToArray();
        var targets = _targets;
        if (target is { } extra)
        {
            if (extra.Slot.Material != ushort.MaxValue || entries.Any(entry => entry.MaterialId == extra.Slot.Material))
                throw new InvalidOperationException("The Orbit pressure copy must use its reserved material ID.");
            var descriptor = new PbrMaterialDescriptor(Vector3.Zero, 0, 1, 1, Vector3.Zero, 0,
                new MaterialTextureSet(baseColor: extra.Reference));
            entries = [..entries, new(extra.Slot.Material, descriptor)];
            targets = [.._targets, extra];
        }
        var palette = new MaterialPalette(revision, entries);
        if (!stream.PublishTerrainAppearance(view, palette, targets))
            return false;
        _pressurePalette = palette;
        _pressureTargets = targets;
        return true;
    }

    // The decoded maps and target sequences remain fixed for this session.
    // Groundwork carries exact residents on a same-epoch cut and coalesces their requests.
    internal enum Scheduling { Scheduled, Retry, Stopped }

    internal bool IsResident(D3D12TextureHandoff stream, D3D12TextureView view, bool orbital)
    {
        var targetMip = orbital ? MipPolicy.CoarseMip : MipPolicy.FineMip;
        foreach (var target in _targets)
        {
            if (!stream.TryReadResidency(view, target, out var residency) || residency.ResidentMip != targetMip)
                return false;
        }
        return true;
    }

    internal Scheduling Publish(D3D12TextureHandoff stream, D3D12TextureView view, bool orbital)
    {
        if (!stream.PublishTerrainAppearance(view, _pressurePalette ?? _palette, _pressureTargets ?? _targets))
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

    private static JsonDocument ReadManifest()
    {
        using var stream = Open(ManifestName);
        if (stream.Length is <= 0 or > MaximumManifestBytes)
            throw new InvalidDataException($"Invalid Orbit texture manifest size: {ManifestName}.");

        try
        {
            return JsonDocument.Parse(stream);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"Invalid Orbit texture manifest {ManifestName}: malformed JSON.", error);
        }
    }

    private static string FindDigest(JsonElement entries, string name)
    {
        string? digest = null;
        foreach (var entry in entries.EnumerateArray())
        {
            if (!TryReadManifestProperty(entry, "file", out var file) ||
                file.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"Invalid Orbit texture manifest {ManifestName}: asset entry has no file name.");
            if (!string.Equals(ReadManifestText(file, "file"), name, StringComparison.Ordinal))
                continue;
            if (digest is not null)
                throw new InvalidDataException($"Duplicate Orbit texture manifest entry: {name}.");
            digest = TryReadManifestProperty(entry, "cookedSha256", out var hash) && hash.ValueKind == JsonValueKind.String
                ? ReadManifestText(hash, $"digest for {name}") : null;
            if (digest is not { Length: 64 } || !digest.All(char.IsAsciiHexDigit))
                throw new InvalidDataException($"Invalid Orbit texture manifest digest: {name}.");
        }
        return digest ?? throw new InvalidDataException($"Missing Orbit texture manifest entry: {name}.");
    }

    private static bool TryReadManifestProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        try
        {
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value);
        }
        catch (InvalidOperationException error)
        {
            throw new InvalidDataException($"Invalid Orbit texture manifest {ManifestName}: malformed property name.", error);
        }
    }

    private static string? ReadManifestText(JsonElement element, string field)
    {
        try
        {
            return element.GetString();
        }
        catch (InvalidOperationException error)
        {
            throw new InvalidDataException($"Invalid Orbit texture manifest {ManifestName}: malformed {field} text.", error);
        }
    }
}

using System.Numerics;
using Groundwork.Rendering;
using Groundwork.Rendering.D3D12;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Client;

internal static class Wp11WarpReadbackSamples
{
    private const float GlobeDepthTolerance = 0.00002f;
    private const int GlobeColorTolerance = 3;
    private const int EditColorTolerance = 1;
    private const double EditDepthToleranceMeters = 0.08;
    private static readonly VolumeQueryLimits EditQueries = new(8_000_000, 128);

    internal static object Inspect(string checkpoint, int width, int height, byte[] colors, float[] depths,
        RenderScene scene, PlanetLocalFrame? origin, VolumeStreamingCoordinator terrain, VolumePublicationInfo expected,
        RenderMeshPair? globe, PlanetVector pad, VolumeTerrainColorEvaluator? colorEvaluator)
    {
        if (width < 8 || height < 8 || depths.Length != checked(width * height) ||
            colors.Length != checked(depths.Length * 4) || !Matrix4x4.Invert(scene.View, out var inverseView))
            throw Invalid(checkpoint, "invalid attachment dimensions or view matrix");
        var image = new ReadbackImage(width, height, colors, depths);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(scene.VerticalFieldOfView, (float)width / height,
            scene.NearPlane, scene.FarPlane);
        var camera = new ReadbackCamera(scene.View * projection, inverseView, projection, scene.NearPlane, scene.FarPlane);

        return checkpoint switch
        {
            "far-after-ascent" or "orbit-globe" or "far-before-return" when globe is not null =>
                InspectGlobe(checkpoint, image, camera, globe, pad, scene.Tint),
            "landing-marker" when globe is not null => InspectMarker(checkpoint, image, camera, globe, scene.Tint),
            "near-before-ascent" or "near-after-return" when origin.HasValue =>
                InspectNear(checkpoint, image, terrain, expected, origin.Value),
            "edited-final-approach" when origin.HasValue =>
                InspectEdit(checkpoint, image, camera, scene, origin.Value, terrain, expected, pad, colorEvaluator),
            _ => throw Invalid(checkpoint, "missing scene information"),
        };
    }

    private static object InspectMarker(string checkpoint, ReadbackImage image, ReadbackCamera camera,
        RenderMeshPair globe, Vector4 tint)
    {
        var target = ProceduralGlobe.MarkerAimPoint(globe);
        if (camera.ProjectPixel(image, target) is not { } projected)
            throw Invalid(checkpoint, "the aimed marker triangle is outside the viewport");

        var ray = camera.RayAt(image, projected.X, projected.Y);
        if (NearestGlobeHit(globe, ray, camera) is not { Marker: true, Interior: true } markerHit ||
            NearestGlobeHit(globe, ray, camera, includeMarker: false) is not { } terrainBehind)
            throw Invalid(checkpoint, "the marker sample does not cover the globe terrain");

        var marker = CheckGlobeHit(checkpoint, image.At(projected.X, projected.Y), ray, markerHit, camera, tint);
        var expectedTerrainDepth = camera.ProjectDepth(ray.At(terrainBehind.ForwardMeters));
        var expectedTerrainColor = ColorCode(terrainBehind.Color * tint);
        if (expectedTerrainDepth - marker.Pixel.Depth <= GlobeDepthTolerance ||
            MatchesColor(marker.Pixel, expectedTerrainColor, GlobeColorTolerance))
            throw Invalid(checkpoint, "the sample does not distinguish marker color and depth from the terrain behind it");

        return new
        {
            Marker = marker, TargetTriangleCentroid = target, ExpectedTerrainDepth = expectedTerrainDepth,
            ExpectedTerrainColor = expectedTerrainColor, TerrainForwardMeters = terrainBehind.ForwardMeters,
            GlobeDepthTolerance, GlobeColorTolerance,
        };
    }

    private static object InspectGlobe(string checkpoint, ReadbackImage image, ReadbackCamera camera,
        RenderMeshPair globe, PlanetVector pad, Vector4 tint)
    {
        GlobeSample? ground = null;
        Pixel? sky = null;
        foreach (var (x, y) in ProbePixels(image))
        {
            var ray = camera.RayAt(image, x, y);
            var hit = NearestGlobeHit(globe, ray, camera);
            var pixel = image.At(x, y);
            if (hit is null && sky is null)
            {
                if (pixel.Depth != 1)
                    throw Invalid(checkpoint, $"globe sky has non-clear depth at ({x},{y})");
                sky = pixel;
            }
            else if (ground is null && hit is { Marker: false, Interior: true })
            {
                ground = CheckGlobeHit(checkpoint, pixel, ray, hit.Value, camera, tint);
            }
            if (ground.HasValue && sky.HasValue)
                break;
        }

        if (ground is not { } groundSample)
            throw Invalid(checkpoint, "no interior pixel of the generated globe is in view");
        if (checkpoint == "orbit-globe" && sky is null)
            throw Invalid(checkpoint, "the orbital globe silhouette has no visible sky control");
        if (sky is { } skySample && !ColorDiffers(groundSample.Pixel, skySample))
            throw Invalid(checkpoint, "globe surface and clear-depth sky have the same RGB");

        GlobeSample? marker = null;
        var markerPosition = new Vector3((float)pad.X, (float)pad.Y, (float)pad.Z);
        markerPosition += Vector3.Normalize(markerPosition) * 8;
        if (camera.ProjectPixel(image, markerPosition) is { } markerPixel)
        {
            var ray = camera.RayAt(image, markerPixel.X, markerPixel.Y);
            if (NearestGlobeHit(globe, ray, camera) is { Marker: true } markerHit)
                marker = CheckGlobeHit(checkpoint, image.At(markerPixel.X, markerPixel.Y), ray, markerHit, camera, tint);
        }

        return new { Ground = groundSample, Sky = sky, Marker = marker, GlobeDepthTolerance, GlobeColorTolerance };
    }

    private static GlobeSample CheckGlobeHit(string checkpoint, Pixel pixel, Ray ray, GlobeHit hit,
        ReadbackCamera camera, Vector4 tint)
    {
        var expectedDepth = camera.ProjectDepth(ray.At(hit.ForwardMeters));
        if (!pixel.Drawn || MathF.Abs(pixel.Depth - expectedDepth) > GlobeDepthTolerance)
            throw Invalid(checkpoint, $"globe depth at ({pixel.X},{pixel.Y}) is {pixel.Depth:R}, expected {expectedDepth:R}");
        var expectedRgba = ColorCode(hit.Color * tint);
        if (!MatchesColor(pixel, expectedRgba, GlobeColorTolerance))
            throw Invalid(checkpoint, $"globe RGBA at ({pixel.X},{pixel.Y}) differs from the generated mesh color");
        return new(pixel, expectedDepth, hit.ForwardMeters, expectedRgba);
    }

    private static object InspectNear(string checkpoint, ReadbackImage image, VolumeStreamingCoordinator terrain,
        VolumePublicationInfo expected, PlanetLocalFrame origin)
    {
        using var lease = RequireTerrain(checkpoint, terrain, expected, origin);
        var foreground = ProbePixels(image).Select(point => image.At(point.X, point.Y)).Where(pixel => pixel.Drawn).Take(3).ToArray();
        if (foreground.Length != 3)
            throw Invalid(checkpoint, "no visible colored terrain surface in the confirmed Near frame");
        return new { Foreground = foreground, lease.Info.TerrainRevision, lease.Info.Generation };
    }

    private static object InspectEdit(string checkpoint, ReadbackImage image, ReadbackCamera camera, RenderScene scene,
        PlanetLocalFrame origin, VolumeStreamingCoordinator terrain, VolumePublicationInfo expected, PlanetVector pad,
        VolumeTerrainColorEvaluator? colorEvaluator)
    {
        using var lease = RequireTerrain(checkpoint, terrain, expected, origin);
        if (expected.TerrainRevision != 2 || scene.Sunlight.HasValue)
            throw Invalid(checkpoint, "the seed 42 landing-pad edit is missing");
        Span<VolumeRayHit> hits = stackalloc VolumeRayHit[8];
        string? colorFailure = null;
        foreach (var (offsetX, offsetY) in new (int, int)[] { (0, 0), (-5, 0), (5, 0), (0, -5), (0, 5) })
        {
            var x = Math.Clamp(image.Width / 2 + offsetX, 0, image.Width - 1);
            var y = Math.Clamp(image.Height / 2 + offsetY, 0, image.Height - 1);
            var pixel = image.At(x, y);
            if (!pixel.Drawn)
                continue;
            var ray = camera.RayAt(image, x, y);
            var distance = camera.ForwardMeters(pixel.Depth);
            var gpuPoint = origin.ToPlanet(ray.At(distance));
            var result = lease.Raycast(origin.ToPlanet(ray.At(camera.NearPlane)),
                origin.ToPlanet(ray.At(Math.Min(distance + 0.5f, camera.FarPlane))), hits, EditQueries);
            if (result.Terrain != expected || result.Status != VolumeQueryStatus.Contact || result.Count < 1 ||
                hits[0].Material != (ushort)ReferenceTerrainMaterial.OchreStone ||
                DistanceMeters(hits[0].Position, pad) > 0.75)
                continue;
            var errorMeters = DistanceMeters(gpuPoint, hits[0].Position);
            if (errorMeters > EditDepthToleranceMeters)
                throw Invalid(checkpoint, $"GPU surface differs from the edited collision hit by {errorMeters:R} m");
            if (EditedMeshColors(lease, origin, ray, distance, scene, colorEvaluator) is not { } expectedColors)
                continue;
            if (!MatchesColor(pixel, expectedColors.Edit, EditColorTolerance) ||
                MatchesColor(pixel, expectedColors.Basalt, EditColorTolerance))
            {
                colorFailure = $"edit RGBA ({pixel.R},{pixel.G},{pixel.B},{pixel.A}) at ({x},{y}) must distinguish " +
                    $"the published mesh color {expectedColors.Edit} from the same mesh shaded as basalt {expectedColors.Basalt}";
                continue;
            }
            return new
            {
                Edit = pixel, ExpectedColor = expectedColors.Edit, BasaltColor = expectedColors.Basalt,
                ColorTolerance = EditColorTolerance, GpuPosition = Position(gpuPoint),
                CollisionPosition = Position(hits[0].Position), hits[0].Material, ErrorMeters = errorMeters,
                EditDepthToleranceMeters, lease.Info.TerrainRevision, lease.Info.Generation,
            };
        }

        throw Invalid(checkpoint, colorFailure ?? "no centered color/depth sample hits the committed ochre landing-pad edit");
    }

    private static EditColors? EditedMeshColors(VolumeStreamingLease lease, PlanetLocalFrame origin, Ray ray, float distance,
        RenderScene scene, VolumeTerrainColorEvaluator? colorEvaluator)
    {
        EditColors? colors = null;
        var bestError = (float)EditDepthToleranceMeters;
        foreach (var mesh in lease.Meshes)
        foreach (var triangle in mesh.Triangles)
        {
            var a = MeshVertex(mesh, triangle.A);
            var b = MeshVertex(mesh, triangle.B);
            var c = MeshVertex(mesh, triangle.C);
            if (!Intersect(ray, origin.ToLocal(a), origin.ToLocal(b), origin.ToLocal(c), out var forward, out var weights))
                continue;
            var error = MathF.Abs(forward - distance);
            if (error >= bestError)
                continue;
            bestError = error;
            const ushort basalt = (ushort)ReferenceTerrainMaterial.Basalt;
            var editColor = VertexColor(a, triangle.A.Material) * weights.X + VertexColor(b, triangle.B.Material) * weights.Y +
                VertexColor(c, triangle.C.Material) * weights.Z;
            var basaltColor = VertexColor(a, basalt) * weights.X + VertexColor(b, basalt) * weights.Y + VertexColor(c, basalt) * weights.Z;
            colors = new(Shade(editColor, scene, distance), Shade(basaltColor, scene, distance));
        }
        return colors;

        Vector4 VertexColor(PlanetVector position, ushort material) => DecodeRgba(
            colorEvaluator is null ? VolumeColor(material) : colorEvaluator(position, material)
                ?? throw new InvalidOperationException("The diagnostic volume palette rejected a published mesh vertex."));
    }

    private static Rgba Shade(Vector4 color, RenderScene scene, float distance)
    {
        color *= scene.Tint;
        if (scene.Fog is { MaxOpacity: > 0 } fog)
        {
            var amount = fog.MaxOpacity * Math.Clamp((distance - fog.StartDepthMeters) /
                (fog.EndDepthMeters - fog.StartDepthMeters), 0, 1);
            var rgb = Vector3.Lerp(new(color.X, color.Y, color.Z), fog.Color, amount);
            color = new(rgb, color.W);
        }
        return ColorCode(color);
    }

    private static PlanetVector MeshVertex(PlanetVolumeMesh mesh, VolumeVertex vertex)
    {
        var cell = mesh.Descriptor.CellSizeMeters;
        return new((mesh.Origin.X + (double)vertex.Position.X) * cell,
            (mesh.Origin.Y + (double)vertex.Position.Y) * cell, (mesh.Origin.Z + (double)vertex.Position.Z) * cell);
    }

    // Default volume vertex colors when the pinned D3D12 provider has no color evaluator.
    private static uint VolumeColor(ushort material) => (material % 3) switch
    {
        0 => 0xff4688b8u,
        1 => 0xff50905au,
        _ => 0xffa06b63u,
    };

    private readonly record struct EditColors(Rgba Edit, Rgba Basalt);

    private static VolumeStreamingLease RequireTerrain(string checkpoint, VolumeStreamingCoordinator terrain,
        VolumePublicationInfo expected, PlanetLocalFrame origin)
    {
        var lease = terrain.TryRead();
        if (lease is not null && lease.Info == expected && lease.Info.View.Frame == origin)
            return lease;
        lease?.Dispose();
        throw Invalid(checkpoint, "the Present and current collision generation differ");
    }

    private static GlobeHit? NearestGlobeHit(RenderMeshPair globe, Ray ray, ReadbackCamera camera, bool includeMarker = true)
    {
        GlobeHit? closest = null;
        Visit(globe.First, hasMarker: false);
        Visit(globe.Second, hasMarker: true);
        return closest;

        void Visit(RenderMeshInstance instance, bool hasMarker)
        {
            var vertices = instance.Mesh.Positions;
            var indices = instance.Mesh.Indices;
            var colors = instance.Mesh.Colors;
            var localRay = ray with { Origin = ray.Origin - instance.Translation };
            var indexCount = hasMarker && !includeMarker ? indices.Length - 6 : indices.Length;
            for (var i = 0; i < indexCount; i += 3)
            {
                var a = (int)indices[i];
                var b = (int)indices[i + 1];
                var c = (int)indices[i + 2];
                if (!Intersect(localRay, vertices[a], vertices[b], vertices[c], out var distance, out var weights) ||
                    distance < camera.NearPlane || distance >= (closest?.ForwardMeters ?? camera.FarPlane))
                    continue;
                var color = colors.IsEmpty ? Vector4.One :
                    DecodeRgba(colors[a]) * weights.X + DecodeRgba(colors[b]) * weights.Y + DecodeRgba(colors[c]) * weights.Z;
                closest = new(distance, color, hasMarker && i >= indices.Length - 6,
                    MathF.Min(weights.X, MathF.Min(weights.Y, weights.Z)) > 0.001f);
            }
        }
    }

    private static bool Intersect(Ray ray, Vector3 a, Vector3 b, Vector3 c, out float distance, out Vector3 weights)
    {
        distance = 0;
        weights = default;
        var edgeB = b - a;
        var edgeC = c - a;
        var cross = Vector3.Cross(ray.Direction, edgeC);
        var det = Vector3.Dot(edgeB, cross);
        if (MathF.Abs(det) < 1e-7f)
            return false;
        var relative = ray.Origin - a;
        var weightB = Vector3.Dot(relative, cross) / det;
        if (weightB < 0 || weightB > 1)
            return false;
        cross = Vector3.Cross(relative, edgeB);
        var weightC = Vector3.Dot(ray.Direction, cross) / det;
        var weightA = 1 - weightB - weightC;
        if (weightC < 0 || weightA < 0)
            return false;
        distance = Vector3.Dot(edgeC, cross) / det;
        weights = new(weightA, weightB, weightC);
        return true;
    }

    private static IEnumerable<(int X, int Y)> ProbePixels(ReadbackImage image)
    {
        foreach (var y in new[] { 50, 76, 26, 94, 6 })
        foreach (var x in new[] { 50, 23, 77, 9, 91 })
            yield return (image.Width * x / 100, image.Height * y / 100);
    }

    private static bool ColorDiffers(Pixel first, Pixel second) =>
        Math.Abs(first.R - second.R) >= 3 || Math.Abs(first.G - second.G) >= 3 || Math.Abs(first.B - second.B) >= 3;

    private static bool MatchesColor(Pixel pixel, Rgba expected, int tolerance) =>
        Math.Abs(pixel.R - expected.R) <= tolerance && Math.Abs(pixel.G - expected.G) <= tolerance &&
        Math.Abs(pixel.B - expected.B) <= tolerance && Math.Abs(pixel.A - expected.A) <= tolerance;

    private static Vector4 DecodeRgba(uint pack) =>
        new Vector4(pack & 255, (pack >> 8) & 255, (pack >> 16) & 255, pack >> 24) / 255f;

    private static Rgba ColorCode(Vector4 color) => new(Code(color.X), Code(color.Y), Code(color.Z), Code(color.W));
    private static int Code(float value) => (int)MathF.Round(Math.Clamp(value, 0, 1) * 255);

    private static double DistanceMeters(PlanetVector a, PlanetVector b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    private static object Position(PlanetVector planet) => new { planet.X, planet.Y, planet.Z };
    private static InvalidOperationException Invalid(string checkpoint, string detail) => new($"WP11 WARP {checkpoint}: {detail}.");

    private readonly record struct ReadbackImage(int Width, int Height, byte[] Colors, float[] Depths)
    {
        internal Pixel At(int x, int y)
        {
            var index = y * Width + x;
            var color = Colors.AsSpan(index * 4, 4);
            return new(x, y, color[0], color[1], color[2], color[3], Depths[index]);
        }
    }

    private readonly record struct ReadbackCamera(Matrix4x4 ViewProjection, Matrix4x4 InverseView, Matrix4x4 Projection,
        float NearPlane, float FarPlane)
    {
        internal Ray RayAt(ReadbackImage image, int x, int y) => new(InverseView.Translation,
            Vector3.TransformNormal(new Vector3((2 * (x + 0.5f) / image.Width - 1) / Projection.M11,
                (1 - 2 * (y + 0.5f) / image.Height) / Projection.M22, -1), InverseView));

        internal float ProjectDepth(Vector3 position)
        {
            var clip = Vector4.Transform(new Vector4(position, 1), ViewProjection);
            return clip.Z / clip.W;
        }

        internal (int X, int Y)? ProjectPixel(ReadbackImage image, Vector3 position)
        {
            var clip = Vector4.Transform(new Vector4(position, 1), ViewProjection);
            if (clip.W <= 0 || clip.Z < 0 || clip.Z >= clip.W || MathF.Abs(clip.X) >= clip.W || MathF.Abs(clip.Y) >= clip.W)
                return null;
            return ((int)((clip.X / clip.W + 1) * image.Width / 2), (int)((1 - clip.Y / clip.W) * image.Height / 2));
        }

        internal float ForwardMeters(float depth) =>
            (float)((double)NearPlane * FarPlane / (FarPlane - depth * ((double)FarPlane - NearPlane)));
    }

    private readonly record struct Ray(Vector3 Origin, Vector3 Direction)
    {
        internal Vector3 At(float forwardMeters) => Origin + Direction * forwardMeters;
    }

    private readonly record struct Pixel(int X, int Y, byte R, byte G, byte B, byte A, float Depth)
    {
        public bool Drawn => Depth is >= 0 and < 1 && A > 0 && (R | G | B) != 0;
    }

    private readonly record struct Rgba(int R, int G, int B, int A);
    private readonly record struct GlobeHit(float ForwardMeters, Vector4 Color, bool Marker, bool Interior);
    private readonly record struct GlobeSample(Pixel Pixel, float ExpectedDepth, float ForwardMeters, Rgba? ExpectedRgba);
}

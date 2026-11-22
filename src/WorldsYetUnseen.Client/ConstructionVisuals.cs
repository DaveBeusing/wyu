using System.Numerics;
using Groundwork.Rendering;
using Groundwork.Rendering.D3D12;
using Groundwork.Voxels;
using WorldsYetUnseen.Gameplay;

namespace WorldsYetUnseen.Client;

/// <summary>Publishes committed blocks and the placement ghost through the same native depth-tested prop pass.</summary>
internal sealed class ConstructionVisuals
{
    internal const uint ReadyPreviewTint = 0x665ef485;
    internal const uint RejectedPreviewTint = 0x666b74ed;
    private const int SolidModelCount = 4;
    private const int ConstructionCellLayer = 6;
    private static readonly PropCellKey PreviewCell = new(0, 0, ConstructionCellLayer + 1);

    private D3D12PropCells? _stream;
    private ReferenceProps? _props;
    private int _firstModel;
    private PropPublication _publication;
    private ConstructionState? _built;
    private ConstructionBox? _preview;
    private uint _previewColor;
    private ulong _sceneRevision;
    private bool _ready;

    internal void Attach(D3D12Renderer renderer, ReferenceProps? props = null)
    {
        var decorativeAssets = props is null ? ReadOnlySpan<D3D12PropAsset>.Empty : props.RenderAssets;
        var assets = new D3D12PropAsset[decorativeAssets.Length + 2 * SolidModelCount];
        decorativeAssets.CopyTo(assets);
        _firstModel = decorativeAssets.Length;
        for (var shape = 0; shape < SolidModelCount; shape++)
        {
            var extent = shape == 0 ? new Vector3(0.125f) : new Vector3(
                shape == 1 ? 0.25f : 0.125f, shape == 2 ? 0.25f : 0.125f, shape == 3 ? 0.25f : 0.125f);
            var mesh = BuildMesh(extent);
            var radius = extent.Length() * 1.00001f;
            // Model IDs 1–4 are used by decorative props on this stream.
            assets[_firstModel + shape] = new(new((uint)(5 + shape), 5, radius, 1000, 32, 40), mesh, mesh);
            assets[_firstModel + SolidModelCount + shape] = new(new((uint)(9 + shape), 6, radius, 12, 4, 6), mesh, mesh)
                { Transparent = true };
        }

        var decorativeCells = props is null ? 0 : ReferenceProps.RenderCellCapacity;
        var maxCells = decorativeCells + ConstructionState.MaximumBlocks + 1;
        var maxInstances = decorativeCells * ReferenceProps.RenderInstancesPerCell + ConstructionState.MaximumBlocks + 1;
        var perCell = props is null ? 1 : ReferenceProps.RenderInstancesPerCell;
        _stream = new(new(maxCells, maxCells, maxInstances, maxInstances, assets.Length * 2),
            perCell, props?.RenderSeed ?? 0, 1, assets);
        renderer.AttachPropCells(_stream);
        props?.UseAttachedStream(_stream);
        _props = props;
        _publication = default;
        _built = null;
        _preview = null;
        _sceneRevision = 0;
        _ready = false;
    }

    internal bool Update(Guid world, VolumePublicationInfo terrain, ConstructionState construction,
        ConstructionBox? preview = null, uint previewColor = ReadyPreviewTint)
    {
        if (_stream is not { } stream || world == Guid.Empty || terrain.Generation == 0)
            return false;
        var publication = new PropPublication(world, terrain.View.RendererEpoch, terrain.TerrainRevision, terrain.Generation);
        var newCut = publication != _publication;
        var sameBlocks = ReferenceEquals(_built, construction);
        var samePreview = _preview == preview && (preview is null || _previewColor == previewColor);
        if (!newCut && _ready && sameBlocks && samePreview)
            return true;

        if (newCut)
        {
            // Decorative terrain workers own their publication when both features share the stream.
            if (_props is { } props)
            {
                if (!props.HasPublished(terrain))
                    return false;
            }
            else
                stream.Publish(publication, []);
        }

        var previous = _built;
        var restageBlocks = newCut || !_ready || !sameBlocks;
        var restagePreview = newCut || !_ready || !samePreview;
        var required = new PropCellKey[construction.Blocks.Length + (preview is null ? 0 : 1)];
        var invalidated = new List<PropCellKey>();
        var toStage = new List<int>();
        for (var i = 0; i < construction.Blocks.Length; i++)
        {
            var block = construction.Blocks[i];
            var key = new PropCellKey(block.Id, 0, ConstructionCellLayer);
            required[i] = key;
            if (restageBlocks && (newCut || !_ready || previous is null ||
                !previous.TryGetBlock(block.Id, out var old) || old != block))
            {
                invalidated.Add(key);
                toStage.Add(i);
            }
        }
        if (preview is not null)
        {
            required[^1] = PreviewCell;
            if (restagePreview)
                invalidated.Add(PreviewCell);
        }

        var decorative = _props is null ? ReadOnlySpan<PropCellKey>.Empty : _props.RenderCoverage;
        var coverage = new PropCellKey[decorative.Length + required.Length];
        decorative.CopyTo(coverage);
        required.AsSpan().CopyTo(coverage.AsSpan(decorative.Length));

        _publication = publication;
        _built = construction;
        _preview = preview;
        _previewColor = previewColor;
        _ready = false;
        // Every distinct ghost, including a tint change or removal, needs its own real Present.
        var revision = ++_sceneRevision;
        if (!stream.TryReviseCoverage(publication, coverage, invalidated.ToArray(), required, revision,
                preview is null ? null : PreviewCell))
            return false;
        if (toStage.Count > 0)
        {
            var boxes = construction.BuildBoxes();
            foreach (var index in toStage)
                if (!TryStage(stream, required[index], boxes[index], preview: false, uint.MaxValue))
                    return false;
        }
        if (restagePreview && preview is { } box && !TryStage(stream, PreviewCell, box, preview: true, previewColor))
            return false;

        _ready = true;
        return true;
    }

    internal bool IsPresented(VolumePublicationInfo terrain, ulong constructionRevision, ulong minimumSequence) =>
        _preview is null && HasPresentedScene(terrain, constructionRevision, minimumSequence);

    internal bool HasPresentedPreview(VolumePublicationInfo terrain, ulong constructionRevision, ConstructionBox preview,
        uint previewColor = ReadyPreviewTint) =>
        _preview == preview && _previewColor == previewColor && HasPresentedScene(terrain, constructionRevision, 0);

    private bool HasPresentedScene(VolumePublicationInfo terrain, ulong constructionRevision, ulong minimumSequence) =>
        _ready && _stream is { } stream &&
        _built?.Revision == constructionRevision &&
        _publication.RendererEpoch == terrain.View.RendererEpoch && _publication.TerrainRevision == terrain.TerrainRevision &&
        _publication.CoverageGeneration == terrain.Generation &&
        stream.HasPresentedScene(_publication, _sceneRevision, minimumSequence);

    private bool TryStage(D3D12PropCells stream, PropCellKey key, ConstructionBox box, bool preview, uint tint)
    {
        if (stream.TryRequest(key, 1, out var request) != PropCellResult.Accepted)
            return false;
        var center = new PropPoint(box.Center.X, box.Center.Y, box.Center.Z);
        var model = _firstModel + (box.HalfExtent.X > 0.1875 ? 1 : box.HalfExtent.Y > 0.1875 ? 2 : box.HalfExtent.Z > 0.1875 ? 3 : 0);
        if (preview)
            model += SolidModelCount;
        var x = box.XAxis;
        var y = box.YAxis;
        var z = box.ZAxis;
        var basis = new Matrix4x4(
            (float)x.X, (float)x.Y, (float)x.Z, 0,
            (float)y.X, (float)y.Y, (float)y.Z, 0,
            (float)z.X, (float)z.Y, (float)z.Z, 0,
            0, 0, 0, 1);
        var orientation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(basis));
        Span<PropPlacement> placement = [new(0, model, Vector3.Zero, orientation, 1, tint)];
        if (stream.TryStage(request, center, placement) == PropCellResult.Accepted)
            return true;
        stream.CompleteWorker(request);
        return false;
    }

    private static RenderMesh BuildMesh(Vector3 h)
    {
        Vector3[] corners =
        [
            new(-h.X, -h.Y, -h.Z), new(h.X, -h.Y, -h.Z), new(-h.X, h.Y, -h.Z), new(h.X, h.Y, -h.Z),
            new(-h.X, -h.Y, h.Z), new(h.X, -h.Y, h.Z), new(-h.X, h.Y, h.Z), new(h.X, h.Y, h.Z)
        ];
        ReadOnlySpan<int> faceCorners = [0, 2, 3, 1, 4, 5, 7, 6, 0, 4, 6, 2, 1, 3, 7, 5, 0, 1, 5, 4, 2, 6, 7, 3];
        ReadOnlySpan<uint> faceColors = [0xffa89d90, 0xffc0b7af, 0xffb0a697, 0xffc6bcb0, 0xff82796d, 0xffdcd3c8];
        var positions = new Vector3[24];
        var colors = new uint[24];
        var indices = new uint[36];
        for (var face = 0; face < 6; face++)
        {
            var vertex = face * 4;
            for (var corner = 0; corner < 4; corner++)
            {
                positions[vertex + corner] = corners[faceCorners[vertex + corner]];
                colors[vertex + corner] = faceColors[face];
            }
            var offset = face * 6;
            var v = (uint)vertex;
            indices[offset] = v;
            indices[offset + 1] = v + 1;
            indices[offset + 2] = v + 2;
            indices[offset + 3] = v;
            indices[offset + 4] = v + 2;
            indices[offset + 5] = v + 3;
        }
        return RenderMesh.Create(positions, indices, colors);
    }
}

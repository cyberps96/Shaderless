using System.Buffers.Binary;
using SCSKiller.Core.Planning;

namespace SCSKiller.Core.Dagor;

/// <summary>The root signature Dagor Engine's DX12 driver creates for a pipeline, rebuilt from each stage's dxil::ShaderHeader
/// (DagorEngine: drv3d_DX12 pipeline.cpp, shadersMetaData/dxil/utility.h decode_graphics_root_signature and
/// decode_compute_root_signature, root_signature_generator.h). Serialized at version 1.0, no static samplers, constant
/// buffers as root CBVs, or with <c>cbvRanges</c> (dx12/rootSignaturesUsesCBVDescriptorRanges) as a table per stage of one
/// range per register. Parameters in order: the draw-id constant, each stage's root constants, constant buffers, sampler tables, one bindless sampler table, SRV tables, one
/// bindless SRV table, UAV tables, then the vendor extension UAV. Graphics stages go PS, VS, HS, DS, GS; the four non-pixel
/// stages share descriptor offsets, so a register's offset counts the registers below it any of them uses.</summary>
public static class DagorRootSig
{
    /// <summary>sizeof(dxil::ShaderHeader) in the dump.</summary>
    public const int HeaderSize = 96;

    /// <summary>ShaderResourceUsageTable (at 8), inOutSemanticMask (at 32) and the low feature flags (at 84) of a header; zero
    /// for a stage the pipeline doesn't have.</summary>
    readonly record struct Usage(uint T, uint S, uint Bindless, uint B, uint U, uint RootConstants, uint Special, uint InOut, uint Features)
    {
        public bool Any => (T | S | Bindless | B | U | RootConstants | Special) != 0;

        public static Usage Of(ShaderInfo? s)
        {
            if (s?.EngineHeader is not { Length: >= HeaderSize } h) return default;
            uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(at));
            uint U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(at));
            return new(U32(8), U32(12), U32(16), U16(20), U16(22), h[24], h[25], U32(32), U32(84));
        }
    }

    // D3D12_SHADER_VISIBILITY and DENY_*_SHADER_ROOT_ACCESS of each graphics stage, in the driver's order
    static readonly (Stage Stage, uint Vis, uint Deny)[] Graphics =
        [(Stage.Pixel, 5, 0x20), (Stage.Vertex, 1, 0x2), (Stage.Hull, 2, 0x4), (Stage.Domain, 3, 0x8), (Stage.Geometry, 4, 0x10)];

    const uint Srv = 0, Uav = 1, Cbv = 2, Sampler = 3, Unbounded = uint.MaxValue;
    const uint DrawId = 1, NvidiaExtension = 2, AmdExtension = 4;
    const uint ResourceHeapIndexing = 0x02000000, SamplerHeapIndexing = 0x04000000;   // D3D_SHADER_REQUIRES_*_DESCRIPTOR_HEAP_INDEXING

    /// <summary>The pipeline's root signature: compute when it has a compute shader, else graphics (a missing stage has no
    /// resources). Mesh pipelines aren't built.</summary>
    public static RootSig.Desc Build(IReadOnlyDictionary<Stage, ShaderInfo> stages, bool cbvRanges = false)
    {
        if (stages.ContainsKey(Stage.Mesh) || stages.ContainsKey(Stage.Amplification)) throw new RootSig.SerializeException("Dagor mesh pipelines aren't rebuilt");
        var compute = stages.TryGetValue(Stage.Compute, out var cs);
        var used = compute ? [(0u, Usage.Of(cs))] : Graphics.Select(g => (g.Vis, Usage.Of(stages.GetValueOrDefault(g.Stage)))).ToArray();
        var features = used.Aggregate(0u, (f, x) => f | x.Item2.Features);
        var flags = ((features & ResourceHeapIndexing) != 0 ? 0x400u : 0) | ((features & SamplerHeapIndexing) != 0 ? 0x800u : 0);
        if (!compute)
        {
            if (Usage.Of(stages.GetValueOrDefault(Stage.Vertex)).InOut != 0) flags |= 0x1;   // ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT
            for (var i = 0; i < Graphics.Length; i++) if (!used[i].Item2.Any) flags |= Graphics[i].Deny;
        }
        // the pixel shader's own registers; the other graphics stages' offsets count the union of theirs
        uint Combo(int i, Func<Usage, uint> mask) => compute || i == 0 ? mask(used[i].Item2) : used.Skip(1).Aggregate(0u, (m, x) => m | mask(x.Item2));

        var rows = new List<uint[]>();
        var special = used.Aggregate(0u, (m, x) => m | x.Item2.Special);
        if ((special & DrawId) != 0) rows.Add([1, 0, 7, 1, 1]);   // b7 space 1 (SPECIAL_CONSTANTS_REGISTER_INDEX, DRAW_ID_REGISTER_SPACE), one dword
        foreach (var (vis, u) in used)
            if (u.RootConstants > 0) rows.Add([1, vis, 8, 1 + u.RootConstants, u.RootConstants]);   // b8, space 1 + dwords
        for (var i = 0; i < used.Length; i++)
            if (!cbvRanges)
                for (var r = 0u; r < 14; r++) { if ((used[i].Item2.B >> (int)r & 1) != 0) rows.Add([2, used[i].Item1, r, 0, 0]); }
            else if (Singles(Cbv, used[i].Item2.B, Combo(i, x => x.B)) is { Count: > 0 } t) rows.Add([0, used[i].Item1, .. t]);
        for (var i = 0; i < used.Length; i++)
            if (Singles(Sampler, used[i].Item2.S, Combo(i, x => x.S)) is { Count: > 0 } t) rows.Add([0, used[i].Item1, .. t]);
        Bindless(rows, used, Sampler, u => u.Bindless & 0x3, 2);
        for (var i = 0; i < used.Length; i++)
            if (Runs(Srv, used[i].Item2.T, Combo(i, x => x.T)) is { Count: > 0 } t) rows.Add([0, used[i].Item1, .. t]);
        Bindless(rows, used, Srv, u => u.Bindless >> 2 & 0x3FFFFFFF, 30);
        for (var i = 0; i < used.Length; i++)
            if (Runs(Uav, used[i].Item2.U, Combo(i, x => x.U)) is { Count: > 0 } t) rows.Add([0, used[i].Item1, .. t]);
        if ((special & NvidiaExtension) != 0) rows.Add([0, 0, Uav, 1, 0, 99, 0]);
        else if ((special & AmdExtension) != 0) rows.Add([0, 0, Uav, 1, 0, 0x7FFF0ADE, 0]);
        return new RootSig.Desc(flags, rows, Version10: true, RangeOffsets: true);
    }

    /// <summary>One single-register range per register (samplers, constant buffers), at its offset among <paramref name="combo"/>'s registers.</summary>
    static List<uint> Singles(uint type, uint mask, uint combo)
    {
        var ranges = new List<uint>();
        for (int r = 0, offset = 0; r < 32 && mask >> r != 0; offset += (int)(combo >> r & 1), r++)
            if ((mask >> r & 1) != 0) ranges.AddRange([type, 1, (uint)r, 0, (uint)offset]);
        return ranges;
    }

    /// <summary>One range per run of consecutive registers (SRVs, UAVs), at its first register's offset among <paramref name="combo"/>'s.</summary>
    static List<uint> Runs(uint type, uint mask, uint combo)
    {
        var ranges = new List<uint>();
        for (int r = 0, offset = 0; r < 32 && mask >> r != 0; offset += (int)(combo >> r & 1), r++)
            if ((mask >> r & 1) != 0 && (r == 0 || (mask >> (r - 1) & 1) == 0))
            {
                var n = 1;
                while (r + n < 32 && (mask >> (r + n) & 1) != 0) n++;
                ranges.AddRange([type, (uint)n, (uint)r, 0, (uint)offset]);
            }
        return ranges;
    }

    /// <summary>The one table of unbounded ranges (register 0, space 1 + bit) for every stage's bindless bits, deduplicated:
    /// visible to the first stage that uses one, to all once a second does.</summary>
    static void Bindless(List<uint[]> rows, (uint Vis, Usage U)[] used, uint type, Func<Usage, uint> bits, int count)
    {
        var (stages, visibility, ranges) = (0, 0u, new List<uint>());
        foreach (var (vis, u) in used)
        {
            var m = bits(u);
            if (m == 0) continue;
            visibility = stages++ == 0 ? vis : 0;
            for (var i = 0; i < count; i++)
                if ((m >> i & 1) != 0 && !Contains(ranges, 1 + (uint)i)) ranges.AddRange([type, Unbounded, 0, 1 + (uint)i, 0]);
        }
        if (stages > 0) rows.Add([0, visibility, .. ranges]);

        static bool Contains(List<uint> ranges, uint space)
        {
            for (var k = 3; k < ranges.Count; k += 5) if (ranges[k] == space) return true;
            return false;
        }
    }
}

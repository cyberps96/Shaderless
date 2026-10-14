using System.Buffers.Binary;
using System.Text;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;
using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Core.RedEngine;

/// <summary>REDengine 3's ray tracing materials as collections. The game builds a path tracing pipeline from its own (run
/// time) libraries and adds materials to it with AddToStateObject, many at once at startup: each material a hit group of a
/// closest hit and an any hit library, the pair a technique names (<see cref="RedShaderCache"/>). NVIDIA caches an
/// addition whole, so one hits only as an exact repeat; a hit group compiled as a collection makes any later addition with
/// it hit (selftest dxr: "add C" 1.6 ms against 12.3 cold, "add B" with the hit group inside another pipeline 12.4). So:
/// one collection per material hit group ('H' plan record), in the subobjects of the game's additions as its recording has
/// them (global root signature, shader config, pipeline config 1), the local root signature by <see cref="Local"/>.</summary>
public static class RedRayTracing
{
    /// <summary>What the game's additions share: the global root signature (a hash; its blob is the recording's), the
    /// shader config and the pipeline config 1. The Witcher 3: all 66 recorded additions 0265c5f2, (32, 8), (1, 0x200).</summary>
    public sealed record Shape(string Global, uint Payload, uint Attributes, uint Depth, uint PipelineFlags);

    /// <summary>The shape most of the recorded additions ('A' records) have; null without any.</summary>
    public static Shape? Learn(IEnumerable<Rec> stateObjects)
    {
        var shapes = stateObjects.Where(r => r.Tag == 'A').Select(Read).OfType<Shape>().ToList();
        return shapes.Count == 0 ? null : shapes.GroupBy(s => s).MaxBy(g => g.Count())!.Key;
    }

    static Shape? Read(Rec r)
    {
        var p = r.Payload;
        var pos = 20; // the grown object's key
        uint U() { var v = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(pos)); pos += 4; return v; }
        void Str() { var n = U(); if (n != uint.MaxValue) pos += 2 * (int)n; }
        void Strs() { for (var n = U(); n > 0; n--) Str(); }
        string? global = null;
        uint payload = 0, attributes = 0, depth = 0, flags = 0;
        try
        {
            U();
            for (var n = U(); n > 0; n--)
                switch (U())
                {
                    case 1: global = Hex(p.AsSpan(pos, 20)); pos += 20; break;
                    case 2: pos += 20; break;
                    case 9: payload = U(); attributes = U(); break;
                    case 12: depth = U(); flags = U(); break;
                    case 10: depth = U(); break;
                    case 0 or 3: U(); break;
                    case 5 or 6: pos += 20; for (var e = U(); e > 0; e--) { Str(); Str(); U(); } break;
                    case 7: U(); Strs(); break;
                    case 8: Str(); Strs(); break;
                    case 11: Str(); U(); Str(); Str(); Str(); break;
                    default: return null;
                }
            return global == null ? null : new Shape(global, payload, attributes, depth, flags);
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>A material hit group's local root signature: SRVs t1 and t2 and 22 root constants b3 in space 1, then a table
    /// of the space-0 SRV registers the libraries bind (always, empty or not) and one of their space-0 CBVs (when they bind
    /// any), each a run of consecutive registers per range at OFFSET_APPEND, volatile descriptors and data; registers the
    /// global root signature already gives are left out. The Witcher 3's recording: 66 of 66 material hit groups rebuilt
    /// byte for byte. Null when a binding is unbounded or partly in a global range (a local range would overlap it), or when
    /// nothing is bound in space 0: the SRV table is then empty and last, which NVIDIA fails to create (0x8000FFFF; the debug
    /// layer only warns), while an empty SRV table before a CBV table works.</summary>
    public static RootSig.Desc? Local(IEnumerable<ShaderInfo> libs, RootSig.Ranges global)
    {
        var spans = new Dictionary<string, List<(ulong Lo, ulong Hi)>> { ["srv"] = [], ["cbv"] = [] };
        foreach (var b in libs.SelectMany(l => l.Bindings).Where(b => b.Space == 0))
        {
            if (b.Count < 0 || b.Lower < 0) return null;
            var (lo, hi) = ((ulong)b.Lower, (ulong)b.Lower + (ulong)Math.Max(1, b.Count));
            var type = b.Class switch { "srv" => 0u, "uav" => 1u, "cbv" => 2u, _ => 3u };
            var overlaps = global.Slots.Where(g => g.Space == 0 && g.Type == type)
                .Select(g => (Lo: (ulong)g.Base, Hi: g.Count == RootSig.Unbounded ? 1UL << 32 : (ulong)g.Base + g.Count))
                .Where(g => g.Lo < hi && lo < g.Hi).ToList();
            if (overlaps.Any(g => g.Lo <= lo && hi <= g.Hi)) continue;
            if (overlaps.Count > 0) return null;
            spans.GetValueOrDefault(b.Class)?.Add((lo, hi));
        }
        List<uint[]> rows = [[3, 0, 1, 1, 2], [3, 0, 2, 1, 2], [1, 0, 3, 1, 22]];
        foreach (var (type, cls) in new[] { (0u, "srv"), (2u, "cbv") })
        {
            var runs = Runs(spans[cls]);
            if (runs.Count > 0 || type == 0) rows.Add([0, 0, .. runs.SelectMany(r => new[] { type, (uint)(r.Hi - r.Lo), (uint)r.Lo, 0u, 3u })]);
        }
        return rows[^1] is [0, 0] ? null : new(0x80, rows, AppendRanges: true);
    }

    // overlapping or adjacent ranges as one
    static List<(ulong Lo, ulong Hi)> Runs(IEnumerable<(ulong Lo, ulong Hi)> spans)
    {
        var runs = new List<(ulong Lo, ulong Hi)>();
        foreach (var r in spans.OrderBy(r => r.Lo))
            if (runs.Count > 0 && r.Lo <= runs[^1].Hi) runs[^1] = (runs[^1].Lo, Math.Max(runs[^1].Hi, r.Hi));
            else runs.Add(r);
        return runs;
    }

    /// <summary>Plan record 'H': closest hit library sha1[20], any hit library[20] (zero: none), global root signature[20],
    /// local root signature[20], u32 payload, attributes, depth, pipeline flags, NVAPI slot (~0u: none), space, options.
    /// Materialize turns it into the collection's 'R' record (<see cref="Collection"/>) once it has the libraries' bytes.</summary>
    public static byte[] Item(string closestHit, string? anyHit, string local, Shape s, RtCollections.Nv? nv)
    {
        var b = new byte[80 + 28];
        foreach (var (h, i) in new[] { closestHit, anyHit ?? Zero, s.Global, local }.Select((h, i) => (h, i))) Convert.FromHexString(h).CopyTo(b, 20 * i);
        uint[] v = [s.Payload, s.Attributes, s.Depth, s.PipelineFlags, nv?.Slot ?? uint.MaxValue, nv?.Space ?? 0, nv?.Options ?? 0];
        for (var i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(80 + 4 * i), v[i]);
        return b;
    }

    public sealed record ItemFields(string ClosestHit, string? AnyHit, string Local, Shape Shape, RtCollections.Nv? Nv);

    public static ItemFields ParseItem(byte[] p)
    {
        if (p.Length != 108) throw new InvalidDataException($"an 'H' record is 108 bytes, not {p.Length}");
        string H(int i) => Hex(p.AsSpan(20 * i, 20));
        uint U(int i) => BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(80 + 4 * i));
        return new(H(0), H(1) == Zero ? null : H(1), H(3), new Shape(H(2), U(0), U(1), U(2), U(3)), U(4) == uint.MaxValue ? null : new(U(4), U(5), U(6)));
    }

    /// <summary>The 'R' payload of a hit group's collection, in the subobjects of the game's additions: the global root
    /// signature, shader config, pipeline config 1, the closest hit library and the any hit library with their hit shaders
    /// exported under their own names, the hit group (triangles), the local root signature and its association with the hit
    /// group. Null when a library exports no hit shader of its kind.</summary>
    public static byte[]? Collection(ReadOnlySpan<byte> closestHit, ItemFields f, ReadOnlySpan<byte> anyHit)
    {
        var ch = ShaderContainer.Rdat(closestHit).Functions.Where(x => x.Kind == 10).Select(x => x.Name).ToList();
        var ah = f.AnyHit == null ? [] : ShaderContainer.Rdat(anyHit).Functions.Where(x => x.Kind == 9).Select(x => x.Name).ToList();
        if (ch.Count != 1 || f.AnyHit != null && ah.Count != 1) return null;
        var w = new MemoryStream();
        void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); w.Write(b); }
        void Hash(string h) => w.Write(Convert.FromHexString(h));
        void Str(string? s) { if (s == null) { U32(uint.MaxValue); return; } U32((uint)s.Length); w.Write(Encoding.Unicode.GetBytes(s)); }
        var hitGroup = $"HitGroup_{f.ClosestHit[..16]}";
        var subs = 0u;
        U32(0); U32(0); // a collection; the subobject count, filled in below
        void Sub(uint t) { subs++; U32(t); }
        Sub(1); Hash(f.Shape.Global);
        Sub(9); U32(f.Shape.Payload); U32(f.Shape.Attributes);
        Sub(12); U32(f.Shape.Depth); U32(f.Shape.PipelineFlags);
        Sub(5); Hash(f.ClosestHit); U32(1); Str(ch[0]); Str(null); U32(0);
        if (f.AnyHit != null) { Sub(5); Hash(f.AnyHit); U32(1); Str(ah[0]); Str(null); U32(0); }
        Sub(11); Str(hitGroup); U32(0); Str(ah.FirstOrDefault()); Str(ch[0]); Str(null);
        var local = subs;
        Sub(2); Hash(f.Local);
        Sub(7); U32(local); U32(1); Str(hitGroup);
        var bytes = w.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), subs);
        return bytes;
    }
}

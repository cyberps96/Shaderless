using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core.Planning;
using SCSKiller.Core.RedEngine;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Core.FromSoft;

/// <summary>Elden Ring's ray tracing materials as collections. Each material's <c>&lt;name&gt;_[RT].shaderbdle</c> holds a
/// closest hit and an any hit library per ray payload (4: <c>[AO]</c>, 12: <c>[Rad]</c>); the game compiles a material's
/// pair as one collection before linking its pipelines from them: the shader config (12, 8), pipeline config depth 1,
/// its global root signature, both libraries (each entry point exported under a name of its own), a hit group of the
/// closest hit alone and one of both, its local root signature and that one's association with both hit groups. Its
/// recording: 30 of 30 collections, all <c>[AO]</c> pairs, in this shape with these root signatures; <c>[Rad]</c> pairs
/// were never created there and are planned the same way, unverified. Export and hit group names aren't in NVIDIA's key:
/// the planned ones are named after the closest hit's hash, the game's after the material. The plan has one 'H' item per
/// pair (<see cref="RedRayTracing.Item"/>), turned into a collection by <see cref="Collection"/>.</summary>
public static class SoulsRayTracing
{
    /// <summary>The binder a material's ray tracing libraries are in; the reader gives each payload's pair its own map.</summary>
    public const string Bundle = "_[RT].shaderbdle";

    public static bool IsPair(ShaderMap m) => m.Library.Contains(Bundle + "|p", StringComparison.OrdinalIgnoreCase) && m.Shaders.Count == 2;

    /// <summary>The global root signature (version 1.0): tables of CBVs b0 and b8-9, SRVs t64-66, UAVs u0-3 and samplers s0-15.</summary>
    public static readonly (string Hash, byte[] Blob) Global = Version10(new(0, [[0, 0, 2, 1, 0, 0, 0, 2, 2, 8, 0, 0], [0, 0, 0, 3, 64, 0, 0], [0, 0, 1, 4, 0, 0, 0], [0, 0, 3, 16, 0, 0, 0]], AppendRanges: true));

    /// <summary>The hit groups' local root signature (version 1.0): tables of CBVs b16 and b1-3, SRVs t67-70 and SRVs t0-24.</summary>
    public static readonly (string Hash, byte[] Blob) Local = Version10(new(0x80, [[0, 0, 2, 1, 16, 0, 0, 2, 3, 1, 0, 0], [0, 0, 0, 4, 67, 0, 0], [0, 0, 0, 25, 0, 0, 0]], AppendRanges: true));

    /// <summary>Shader config (12, 8) and depth 1; <see cref="RedRayTracing.Shape.PipelineFlags"/> is unused (a plain pipeline config).</summary>
    public static readonly RedRayTracing.Shape Shape = new(Global.Hash, 12, 8, 1, 0);

    static (string, byte[]) Version10(RootSig.Desc d)
    {
        var b = RootSig.AsVersion10(RootSig.Serialize(d, []));
        return (Convert.ToHexStringLower(SHA1.HashData(b)), b);
    }

    /// <summary>The 'R' payload of a pair's collection in the game's shape; null unless one library exports a closest hit
    /// and the other an any hit (either order). <paramref name="material"/>: the game's name for the material (export and hit
    /// group names), else the closest hit's hash.</summary>
    public static byte[]? Collection(ReadOnlySpan<byte> first, RedRayTracing.ItemFields f, ReadOnlySpan<byte> second, string? material = null)
    {
        if (f.AnyHit == null) return null;
        var (a, b) = (ShaderContainer.Rdat(first).Functions, ShaderContainer.Rdat(second).Functions);
        if (a.Count != 1 || b.Count != 1) return null;
        var ((chLib, ch), (ahLib, ah)) = a[0].Kind == 10 && b[0].Kind == 9 ? ((f.ClosestHit, a[0]), (f.AnyHit, b[0]))
            : a[0].Kind == 9 && b[0].Kind == 10 ? ((f.AnyHit, b[0]), (f.ClosestHit, a[0])) : default;
        if (ch == null) return null;
        var x = $"{material ?? chLib[..16]}_RayTracing_";
        var tag = ch.Payload == 4 ? "[AO]" : "[Rad]";
        string chName = $"{x}[ClosestHit]_{tag}", ahName = $"{x}[AnyHit]_{tag}", group = $"HitGroup_{material ?? chLib[..16]}_{tag}";
        var w = new MemoryStream();
        void U32(uint v) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(s, v); w.Write(s); }
        void Hash(string h) => w.Write(Convert.FromHexString(h));
        void Str(string? s) { if (s == null) { U32(uint.MaxValue); return; } U32((uint)s.Length); w.Write(Encoding.Unicode.GetBytes(s)); }
        U32(0); U32(9); // a collection of 9 subobjects
        U32(9); U32(f.Shape.Payload); U32(f.Shape.Attributes);
        U32(10); U32(f.Shape.Depth);
        U32(1); Hash(f.Shape.Global);
        U32(5); Hash(chLib); U32(1); Str(chName); Str(ch.Name); U32(0);
        U32(5); Hash(ahLib); U32(1); Str(ahName); Str(ah.Name); U32(0);
        U32(11); Str(group); U32(0); Str(null); Str(chName); Str(null);
        U32(11); Str(group + "_[A]"); U32(0); Str(ahName); Str(chName); Str(null);
        U32(2); Hash(f.Local);
        U32(7); U32(7); U32(2); Str(group); Str(group + "_[A]");
        return w.ToArray();
    }
}

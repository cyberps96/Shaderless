using System.Buffers.Binary;
using System.IO.Compression;

namespace SCSKiller.Core.RedEngine;

/// <summary>REDengine 3's shader caches (The Witcher 3's DX12 build, content\content0), laid out as the game's own loader
/// reads them. Both end in "RDHS" and version 5.
///   - shaderdx12_0.cache, the material shaders: from offset 0, per shader u64 key, u8 1 (zlib), u32 inflated size, u32
///     stored size, the zlib stream (inflated: u32 container size, u32, the DXBC container, the engine's reflection); then
///     the techniques (<see cref="ReadTechnique"/>), each naming the shaders of one pipeline by key. Footer, 48 bytes: u32
///     shaders, u32 techniques, u64, u64 techniques offset, u64 techniques size, u64, "RDHS", u32 version.
///   - staticshaderDx12_0.cache, the engine's own shaders: per shader u64, u64, u32 n, then n bytes: u32 container size,
///     u32, the container, reflection. Footer, 28 bytes: u32 shaders, 16 bytes, "RDHS", u32 version.
/// The game also ships shaderDx12_0.cachecutted (the same entries with the reflection only) and psodx12.cache, a pipeline
/// list that names material shaders by an id these files don't hold.
/// A damaged file reads as null: every count is bounded by the bytes it needs, every length by the bytes left.</summary>
public static class RedShaderCache
{
    const uint Magic = 0x53484452, Version = 5; // "RDHS"

    /// <summary>About 4x The Witcher 3's: 44,828 material shaders, 438,220 techniques, 940 engine shaders, 75 KB at most
    /// inflated per shader.</summary>
    public const int MaxShaders = 200_000, MaxTechniques = 2_000_000, MaxStatic = 4_000, MaxPayload = 4 << 20;

    /// <summary>About 4x the 60,674 distinct pipelines The Witcher 3's techniques name: what a plan's memory scales with.</summary>
    public const int MaxPipelines = 250_000;

    const int ShaderHeader = 17, MinTechnique = 117; // u64 x3, 1-byte path length, u32, six keys, u64 x3, four counts

    /// <summary>A shader's bytes: <paramref name="Stored"/> bytes at <paramref name="At"/>, a zlib stream inflating to
    /// <paramref name="Inflated"/> bytes, or (static) the payload itself.</summary>
    public readonly record struct Entry(long At, int Stored, bool Zlib, int Inflated);

    public sealed record Materials(Dictionary<ulong, Entry> Shaders, List<ulong[]> Techniques);

    public readonly record struct Footer(uint Shaders, uint Techniques, long At, long Size);

    /// <summary>The material cache's footer, when its counts and offsets fit the file and the caps and the techniques are
    /// followed by exactly the two short lists the game writes before the footer (u32 n1, u32 n2, (n1 + n2) x 12 bytes:
    /// 8 + 4 in The Witcher 3's, 1 + 4 in its .cachecutted); else null.</summary>
    public static Footer? ReadFooter(Stream f)
    {
        var len = f.Length;
        if (len < 56) return null;
        var foot = new byte[48];
        f.Position = len - 48;
        f.ReadExactly(foot);
        if (U32(foot, 40) != Magic || U32(foot, 44) != Version) return null;
        var (shaders, techniques, at, size) = (U32(foot, 0), U32(foot, 4), U64(foot, 16), U64(foot, 24));
        if (shaders is 0 or > MaxShaders || techniques is 0 or > MaxTechniques || at > (ulong)len - 56 || size > (ulong)len - 56 - at
            || (ulong)shaders * ShaderHeader > at || (ulong)techniques * MinTechnique > size) return null;
        var lists = new byte[8];
        f.Position = (long)(at + size);
        f.ReadExactly(lists);
        return (long)(at + size) + 8 + 12L * ((long)U32(lists, 0) + U32(lists, 4)) == len - 48 ? new Footer(shaders, techniques, (long)at, (long)size) : null;
    }

    /// <summary>Null: not such a file, another version, or damaged.</summary>
    public static Materials? ReadMaterials(Stream f, CancellationToken ct = default)
    {
        if (ReadFooter(f) is not { } foot) return null;
        var r = new BinaryReader(f);
        try
        {
            f.Position = 0;
            var map = new Dictionary<ulong, Entry>((int)foot.Shaders);
            for (var i = 0; i < foot.Shaders; i++)
            {
                if (i % 4096 == 0) ct.ThrowIfCancellationRequested();
                var key = r.ReadUInt64();
                var zlib = r.ReadByte() != 0;
                var inflated = r.ReadInt32();
                var stored = r.ReadInt32();
                if (stored < 0 || stored > foot.At - f.Position || inflated is < 0 or > MaxPayload) return null;
                map[key] = new Entry(f.Position, stored, zlib, inflated);
                f.Position += stored;
            }
            if (f.Position != foot.At) return null;
            var end = foot.At + foot.Size;
            var list = new List<ulong[]>((int)foot.Techniques);
            for (var i = 0; i < foot.Techniques; i++)
            {
                if (i % 4096 == 0) ct.ThrowIfCancellationRequested();
                var start = f.Position;
                list.Add(ReadTechnique(r, end, ct));
                if (f.Position <= start) return null;
            }
            return f.Position == end ? new Materials(map, list) : null;
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException) { return null; }
    }

    /// <summary>u64 x3, a path, u32, the six stage keys (VS, PS, GS, HS, DS, CS; 0 = none), the ray tracing keys (closest hit
    /// library, any hit library, a placeholder entry; The Witcher 3: on 13,670 techniques, 2,401 distinct hit groups), two
    /// lists (u32 n) of 32-byte records, two lists of (name, u8). A path or name is a compact length (bit 6: more length
    /// bytes follow, 7 bits each) and its characters: a path's are UTF-16 unless bit 7 is set, a name's are bytes. Returns
    /// the six stage keys and the two library keys.</summary>
    static ulong[] ReadTechnique(BinaryReader r, long end, CancellationToken ct)
    {
        var f = r.BaseStream;
        Advance(f, 24, end);
        Skip(r, name: false, end);
        Advance(f, 4, end);
        var keys = new ulong[8];
        for (var i = 0; i < 8; i++) keys[i] = r.ReadUInt64();
        Advance(f, 8, end);
        for (var i = 0; i < 2; i++)
        {
            var records = r.ReadUInt32();
            Advance(f, 32L * records, end);
        }
        for (var i = 0; i < 2; i++)
        {
            var names = r.ReadUInt32();
            if (2L * names > end - f.Position) throw new InvalidDataException("more names than bytes left");
            for (; names > 0; names--)
            {
                if (names % 4096 == 0) ct.ThrowIfCancellationRequested();
                Skip(r, name: true, end);
                Advance(f, 1, end);
            }
        }
        return keys;
    }

    static void Skip(BinaryReader r, bool name, long end)
    {
        var b = r.ReadByte();
        long n = b & 0x3F;
        for (int shift = 6, x = b; (x & (shift == 6 ? 0x40 : 0x80)) != 0; shift += 7)
        {
            if (shift > 27) throw new InvalidDataException("compact length over 34 bits");
            x = r.ReadByte();
            n |= (long)(x & 0x7F) << shift;
        }
        Advance(r.BaseStream, name || (b & 0x80) != 0 ? n : 2 * n, end);
    }

    static void Advance(Stream f, long n, long end)
    {
        if (n < 0 || n > end - f.Position) throw new InvalidDataException("runs past the techniques");
        f.Position += n;
    }

    /// <summary>Null: not such a file, another version, or damaged.</summary>
    public static List<Entry>? ReadStatic(Stream f)
    {
        var len = f.Length;
        if (len < 28) return null;
        var foot = new byte[28];
        f.Position = len - 28;
        f.ReadExactly(foot);
        if (U32(foot, 20) != Magic || U32(foot, 24) != Version) return null;
        var n = U32(foot, 0);
        if (n > MaxStatic || n * 28L > len) return null;
        var r = new BinaryReader(f);
        f.Position = 0;
        var list = new List<Entry>((int)n);
        for (var i = 0; i < n; i++)
        {
            f.Position += 16;
            var size = r.ReadInt32();
            if (size is < 8 or > MaxPayload || size > len - 28 - f.Position) return null;
            list.Add(new Entry(f.Position, size, false, size));
            f.Position += size;
        }
        return f.Position == len - 28 ? list : null;
    }

    /// <summary>The DXBC container an entry holds; null when it holds none (112 of the material cache's entries are
    /// placeholders), its bytes are past the file's end (it changed since it was indexed) or they don't inflate to exactly
    /// the declared size.</summary>
    public static byte[]? Container(Stream f, Entry e)
    {
        if (e.Inflated is < 0 or > MaxPayload || e.Stored is < 0 or > MaxPayload || e.At < 0 || e.At > f.Length - e.Stored) return null;
        var stored = new byte[e.Stored];
        f.Position = e.At;
        if (f.ReadAtLeast(stored, stored.Length, throwOnEndOfStream: false) != stored.Length) return null;
        byte[] p;
        if (!e.Zlib) p = stored;
        else
            try
            {
                using var z = new ZLibStream(new MemoryStream(stored), CompressionMode.Decompress);
                p = new byte[e.Inflated];
                if (z.ReadAtLeast(p, p.Length, throwOnEndOfStream: false) != p.Length || z.ReadByte() != -1) return null;
            }
            catch (InvalidDataException) { return null; }
        if (p.Length < 40 || !p.AsSpan(8, 4).SequenceEqual("DXBC"u8)) return null;
        var size = BinaryPrimitives.ReadInt32LittleEndian(p);
        return size >= 32 && size <= p.Length - 8 ? p[8..(8 + size)] : null;
    }

    static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    static ulong U64(byte[] b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o));
}

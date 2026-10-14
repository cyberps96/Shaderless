using System.Buffers.Binary;

namespace SCSKiller.Core.Unreal;

/// <summary>Unreal's shipped pipeline cache (Content/PipelineCaches/&lt;Platform&gt;/&lt;Project&gt;_&lt;ShaderPlatform&gt;.stable.upipelinecache),
/// read from its table of contents: each PSO's type and the library hashes (FSHAHash) of its shaders. Layout from Epic's
/// PipelineFileCache.cpp (FPipelineCacheFileFormatHeader, FPipelineCacheFileFormatTOC, FPipelineCacheFileFormatPSOMetaData).</summary>
public static class StablePipelineCache
{
    public enum PsoType : uint { Compute = 0, Graphics = 1, RayTracing = 2 }

    public sealed record Pso(uint Key, PsoType Type, string[] Shaders);

    const ulong Magic = 0x5049504543414348, TocMagic = 0x544F435354415232, TocMagic17 = 0x544F435354415254, EofMagic = 0x454F462D4D41524B; // PIPECACH, TOCSTAR2, TOCSTART, EOF-MARK

    /// <summary>File versions 22 (LastUsedTime) to 30 (VariableRateShading) share this header and TOC layout; between them only
    /// the PSO bodies change, of which this reads the type alone (29 adds the ray tracing shader binding layout, 30 the
    /// graphics VRS fields). 31 (UE 5.8) puts a precache result between the type and the rest, and its TOC lists 8-byte
    /// shader hashes. Version 17 (Subpass, UE 4.25) has the TOCSTART table: no guid shared by every entry, no last used time
    /// per entry. Null: not such a file.</summary>
    public static List<Pso>? Read(ReadOnlySpan<byte> b)
    {
        try
        {
            if (b.Length < 57 || U64(b, 0) != Magic || U32(b, 8) is not (17 or (>= 22 and <= 30)) || U64(b, b.Length - 8) != EofMagic) return null;
            var o = checked((int)U64(b, 33)); // after magic, version, game version, u8 platform, guid
            var v17 = U32(b, 8) == 17;
            if (U64(b, o) != (v17 ? TocMagic17 : TocMagic)) return null;
            o += 8;
            if (!v17) o += b[o] != 0 ? 17 : 1; // every entry's guid, once
            o += 4; // sort order
            var n = BinaryPrimitives.ReadInt32LittleEndian(b[o..]);
            o += 4;
            if (n < 0 || n > b.Length / (v17 ? 86 : 94)) return null; // an entry is 86 or 94 bytes at least
            var psos = new List<Pso>(n);
            for (var i = 0; i < n; i++)
            {
                var key = U32(b, o);
                var body = checked((int)U64(b, o + 4));
                o += 4 + 16 + 16 + 36; // key, file offset + size, guid, stats
                var k = BinaryPrimitives.ReadInt32LittleEndian(b[o..]);
                o += 4;
                if (k is < 0 or > 8) return null; // a PSO has at most five stages
                var shaders = new string[k];
                for (var j = 0; j < k; j++) shaders[j] = Convert.ToHexStringLower(b.Slice(o + 20 * j, 20));
                o += 20 * k + 8 + 2 + (v17 ? 0 : 8); // shaders, usage mask, engine flags, last used time
                psos.Add(new Pso(key, (PsoType)U32(b, body), shaders));
            }
            return o == b.Length - 8 ? psos : null;
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException) { return null; }
    }

    static ulong U64(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b[o..]);
    static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
}

using System.Buffers.Binary;
using System.Text;
using K4os.Compression.LZ4;
using SCSKiller.Core.Carved;

namespace SCSKiller.Core.Unity;

/// <summary>Just enough of Unity's file formats to find compiled shaders (layouts as in AssetStudio / UnityPy):
///   - SerializedFile (loose *.assets / level* / globalgamemanagers, or a node of a bundle): header + object table, no type
///     tree needed (player builds strip it; a bundle's is skipped);
///   - UnityFS bundles: blocks info + node table, blocks (LZ4/LZ4HC/LZMA) decompressed only where a read needs them;
///   - Shader objects (class 48): the per-platform LZ4 <c>compressedBlob</c> located by its shape (<see cref="ShaderBlob"/>),
///     decompressed and carved for DXBC/DXIL containers; ComputeShader objects (72) keep theirs raw.</summary>
public static class UnityFiles
{
    public const int ShaderClass = 48, ComputeShaderClass = 72, BuildSettingsClass = 141;

    /// <summary>Unity's ShaderCompilerPlatform "d3d11": the DXBC (or DXC-built DXIL) both the DX11 and DX12 players run.</summary>
    public const uint D3D11Platform = 4;

    /// <summary>Reads <c>count</c> bytes at an offset of a file or of a bundle's uncompressed data (short at the end).</summary>
    public delegate byte[] ReadAt(long offset, int count);

    public sealed record Obj(long PathId, long Start, long Size, int ClassId);
    public sealed record SerializedFile(int Format, string UnityVersion, IReadOnlyList<Obj> Objects);

    /// <summary>The object table of a SerializedFile (formats 9-22 and up), or null when the bytes aren't one.</summary>
    public static SerializedFile? ReadSerialized(ReadAt read, long length)
    {
        var h = read(0, 48);
        if (h.Length < 48) return null;
        uint Be(int o) => BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(o));
        long metaSize = Be(0), fileSize = Be(4), dataOffset = Be(12);
        int format = (int)Be(8), metaAt = 20;
        if (format is < 9 or > 64) return null;
        if (format >= 22)
        {
            metaSize = Be(20);
            fileSize = BinaryPrimitives.ReadInt64BigEndian(h.AsSpan(24));
            dataOffset = BinaryPrimitives.ReadInt64BigEndian(h.AsSpan(32));
            metaAt = 48;
        }
        if (fileSize != length || dataOffset > length || metaSize is <= 0 or > 256 << 20 || metaAt + metaSize > length) return null;
        try
        {
            var r = new Reader(read(metaAt, (int)metaSize), bigEndian: h[16] != 0);
            var unity = r.CString();
            r.I32(); // target platform
            var typeTree = format < 13 || r.U8() != 0;
            var types = new int[r.Count(1 << 20)];
            for (var i = 0; i < types.Length; i++)
            {
                var cls = types[i] = r.I32();
                if (format >= 16) r.U8();      // stripped
                if (format >= 17) r.I16();     // script type index
                if (format >= 13) r.Skip((format < 16 ? cls < 0 : cls == 114) ? 32 : 16); // (script id +) type hash
                if (!typeTree) continue;
                if (format < 12 && format != 10) return null; // old recursive trees: Unity 4 and older
                int nodes = r.Count(1 << 20), strings = r.Count(64 << 20);
                r.Skip(nodes * (format >= 19 ? 32 : 24) + strings);
                if (format >= 21) r.Skip(4 * r.Count(1 << 20));
            }
            if (format is >= 7 and < 14) r.I32(); // big id enabled
            var objs = new Obj[r.Count(1 << 24)];
            for (var i = 0; i < objs.Length; i++)
            {
                if (format >= 14) r.Align4();
                var id = format >= 14 ? r.I64() : r.I32();
                long start = format >= 22 ? r.I64() : r.U32(), size = r.U32();
                var t = r.I32();
                var cls = format >= 16 ? (uint)t < types.Length ? types[t] : -1 : t;
                if (format < 16) r.I16();
                if (format < 11) r.I16();
                if (format is >= 11 and < 17) r.I16();
                if (format is >= 15 and < 17) r.U8();
                objs[i] = new Obj(id, dataOffset + start, size, cls);
            }
            return new SerializedFile(format, unity, objs);
        }
        catch (ArgumentOutOfRangeException) { return null; } // truncated metadata: not a serialized file
    }

    public sealed record Node(long Offset, long Size, uint Flags, string Path);

    /// <summary>A UnityFS bundle: its node table and random access to the uncompressed data (blocks decompressed on
    /// demand; the 16 last used are kept while the bundle is open).</summary>
    public sealed class Bundle : IDisposable
    {
        const int CachedBlocks = 16;   // a big bundle has thousands of 128 KB blocks: keeping them all holds its whole uncompressed size

        readonly Microsoft.Win32.SafeHandles.SafeFileHandle file;
        readonly (long UOff, int USize, long COff, int CSize, int Comp)[] blocks;
        readonly List<(int Index, byte[] Data)> cache = [];   // least recently used first
        public string UnityVersion { get; }
        public IReadOnlyList<Node> Nodes { get; }
        public long BlocksDecompressed { get; private set; }

        Bundle(Microsoft.Win32.SafeHandles.SafeFileHandle file, string unity, (long, int, long, int, int)[] blocks, Node[] nodes) =>
            (this.file, UnityVersion, this.blocks, Nodes) = (file, unity, blocks, nodes);

        public static bool IsBundle(ReadOnlySpan<byte> head) => head.StartsWith("UnityFS\0"u8);

        public static Bundle? Open(string path)
        {
            var f = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            try { return Open(f, RandomAccess.GetLength(f)) is { } b ? b : Close(f); }
            catch { f.Dispose(); throw; }
            static Bundle? Close(Microsoft.Win32.SafeHandles.SafeFileHandle f) { f.Dispose(); return null; }
        }

        static Bundle? Open(Microsoft.Win32.SafeHandles.SafeFileHandle f, long length)
        {
            var head = new byte[Math.Min(512, length)];
            RandomAccess.Read(f, head, 0);
            if (!IsBundle(head)) return null;
            var r = new Reader(head, bigEndian: true);
            r.Skip(8);
            var ver = r.U32();
            var unity = r.CString();
            r.CString(); // revision
            r.I64();     // total size
            int cbi = (int)r.U32(), ubi = (int)r.U32();
            var flags = r.U32();
            if (cbi is < 0 or > 64 << 20 || ubi is < 0 or > 64 << 20 || cbi > length) throw new InvalidDataException("bad UnityFS blocks info size");
            if (ver >= 7) r.Align(16);
            var at = (long)r.Pos;
            var info = new byte[cbi];
            RandomAccess.Read(f, info, (flags & 0x80) != 0 ? length - cbi : at);
            if ((flags & 0x80) == 0) at += cbi;
            if ((flags & 0x200) != 0) at = (at + 15) & ~15L;
            var bi = new Reader(Decompress(info, ubi, (int)(flags & 0x3F)), bigEndian: true);
            bi.Skip(16);
            var blocks = new (long, int, long, int, int)[bi.Count(1 << 24)];
            long u = 0, c = at;
            for (var i = 0; i < blocks.Length; i++)
            {
                int us = (int)bi.U32(), cs = (int)bi.U32(), bf = bi.U16();
                if (us < 0 || cs < 0) throw new InvalidDataException("UnityFS block over 2 GB");
                if (c + cs > length) throw new InvalidDataException("UnityFS block past the end of the file");
                if ((bf & 0x3F) is 2 or 3 && us > 256L * cs + 64) throw new InvalidDataException("UnityFS block larger than LZ4 can expand to");
                blocks[i] = (u, us, c, cs, bf & 0x3F);
                u += us; c += cs;
            }
            var nodes = new Node[bi.Count(1 << 20)];
            for (var i = 0; i < nodes.Length; i++) nodes[i] = new Node(bi.I64(), bi.I64(), bi.U32(), bi.CString());
            return new Bundle(f, unity, blocks, nodes);
        }

        /// <summary>Uncompressed bytes [offset, offset + count) of the bundle's data.</summary>
        public byte[] Read(long offset, int count)
        {
            var dst = new byte[count];
            var i = -1;   // the last block that starts at or before offset
            for (int lo = 0, hi = blocks.Length - 1; lo <= hi;)
            {
                var mid = (lo + hi) / 2;
                if (blocks[mid].UOff <= offset) (i, lo) = (mid, mid + 1);
                else hi = mid - 1;
            }
            for (var done = 0; done < count && i >= 0 && i < blocks.Length; i++)
            {
                var b = Block(i);
                var from = (int)(offset + done - blocks[i].UOff);
                var n = Math.Min(count - done, b.Length - from);
                if (n <= 0) break;
                b.AsSpan(from, n).CopyTo(dst.AsSpan(done));
                done += n;
            }
            return dst;
        }

        /// <summary>Reads within one node (a serialized file or resource) of the bundle.</summary>
        public ReadAt Reader(Node node) => (off, count) => Read(node.Offset + off, (int)Math.Max(0, Math.Min(count, node.Size - off)));

        byte[] Block(int i)
        {
            var at = cache.FindIndex(c => c.Index == i);
            if (at >= 0)
            {
                var hit = cache[at];
                cache.RemoveAt(at);
                cache.Add(hit);
                return hit.Data;
            }
            var (_, us, co, cs, comp) = blocks[i];
            var src = new byte[cs];
            RandomAccess.Read(file, src, co);
            BlocksDecompressed++;
            var b = Decompress(src, us, comp);
            if (cache.Count == CachedBlocks) cache.RemoveAt(0);
            cache.Add((i, b));
            return b;
        }

        public void Dispose()
        {
            file.Dispose();
            cache.Clear();
        }
    }

    /// <summary>UnityFS compression: 0 none, 1 LZMA (5 property bytes, then the raw stream), 2 LZ4, 3 LZ4HC.</summary>
    public static byte[] Decompress(byte[] src, int size, int comp)
    {
        switch (comp)
        {
            case 0: return src;
            case 2 or 3:
                var dst = new byte[size];
                return LZ4Codec.Decode(src, dst) == size ? dst : throw new InvalidDataException("bad LZ4 block");
            case 1:
                var lzma = new SevenZip.Compression.LZMA.Decoder();
                lzma.SetDecoderProperties(src[..5]);
                using (var i = new MemoryStream(src, 5, src.Length - 5))
                using (var o = new MemoryStream(size))
                {
                    lzma.Code(i, o, src.Length - 5, size, null);
                    return o.Length == size ? o.GetBuffer()[..size] : throw new InvalidDataException("bad LZMA block");
                }
            default: throw new InvalidDataException($"unknown UnityFS compression {comp}");
        }
    }

    public sealed record Segment(uint Platform, int Offset, int CompressedLength, int DecompressedLength);

    /// <summary>The compressed blob of a Shader object, found by its shape: <c>platforms</c> (u32 array), then
    /// <c>offsets</c>, <c>compressedLengths</c>, <c>decompressedLengths</c> (u32 arrays per platform before 2019.3, arrays of
    /// arrays after), then <c>compressedBlob</c> (u32 length + bytes). The first candidate whose d3d11 segments all
    /// LZ4-decode to their stated size wins. Returns each d3d11 segment decompressed; null = no such blob (no d3d11 code).
    /// ponytail: a shape scan over the object instead of the per-version SerializedShader layout (m_ParsedForm is ~100 fields
    /// that change every few versions); a false match must also decode as LZ4 to exact sizes. Parse m_ParsedForm per version
    /// if a game ever breaks it.</summary>
    public static List<byte[]>? ShaderBlob(ReadOnlySpan<byte> o)
    {
        for (var p = 0; p + 16 <= o.Length; p += 4)
        {
            var n = U(o, p);
            if (n is 0 or > 32) continue;
            foreach (var nested in (bool[])[true, false])
                if (Shape(o, p, (int)n, nested) is var (segs, blobAt) && Decode(o, segs, blobAt) is { } d) return d;
        }
        return null;
    }

    static (List<Segment>, int)? Shape(ReadOnlySpan<byte> o, int p, int n, bool nested)
    {
        var q = p + 4;
        if (q + 4 * n > o.Length) return null;
        var plats = new uint[n];
        for (var i = 0; i < n; i++) if ((plats[i] = U(o, q + 4 * i)) > 64) return null;
        q += 4 * n;
        var arrays = new List<uint[]>[3];
        for (var a = 0; a < 3; a++)
        {
            arrays[a] = [];
            if (nested)
            {
                if (q + 4 > o.Length || U(o, q) != n) return null;
                q += 4;
                for (var i = 0; i < n; i++)
                    if (UArray(o, ref q, 16) is { Length: > 0 } x && (a == 0 || x.Length == arrays[0][i].Length)) arrays[a].Add(x);
                    else return null;
            }
            else if (UArray(o, ref q, n) is { } x && x.Length == n) arrays[a].AddRange(x.Select(v => new[] { v }));
            else return null;
        }
        if (q + 4 > o.Length) return null;
        var len = U(o, q);
        q += 4;
        if (len == 0 || q + len > o.Length) return null;
        var segs = new List<Segment>();
        for (var i = 0; i < n; i++)
            for (var j = 0; j < arrays[0][i].Length; j++)
            {
                uint off = arrays[0][i][j], cl = arrays[1][i][j], dl = arrays[2][i][j];
                if ((long)off + cl > len || dl > 256u << 20) return null;
                if (plats[i] == D3D11Platform && cl > 0) segs.Add(new Segment(plats[i], (int)off, (int)cl, (int)dl));
            }
        return (segs, q);
    }

    static uint[]? UArray(ReadOnlySpan<byte> o, ref int q, int max)
    {
        if (q + 4 > o.Length) return null;
        var n = U(o, q);
        if (n > max || q + 4 + 4 * n > o.Length) return null;
        var a = new uint[n];
        for (var i = 0; i < n; i++) a[i] = U(o, q + 4 + 4 * i);
        q += 4 + 4 * (int)n;
        return a;
    }

    static List<byte[]>? Decode(ReadOnlySpan<byte> o, List<Segment> segs, int blobAt)
    {
        if (segs.Count == 0) return null;
        var outs = new List<byte[]>();
        foreach (var s in segs)
        {
            var src = o.Slice(blobAt + s.Offset, s.CompressedLength);
            if (s.CompressedLength == s.DecompressedLength) { outs.Add(src.ToArray()); continue; }
            var dst = new byte[s.DecompressedLength];
            if (LZ4Codec.Decode(src, dst) != dst.Length) return null;
            outs.Add(dst);
        }
        return outs;
    }

    /// <summary>BuildSettings' last field, <c>m_GraphicsAPIs</c> (GfxDeviceRenderer: 2 D3D11, 17 OpenGL core, 18 D3D12,
    /// 21 Vulkan), the list the player tries in order. Measured in 2019.2-6000.4 builds: the object ends with it.</summary>
    public static int[]? GraphicsApis(ReadOnlySpan<byte> buildSettings)
    {
        for (var n = 1; n <= 8 && 4 * (n + 1) <= buildSettings.Length; n++)
        {
            var at = buildSettings.Length - 4 * (n + 1);
            if (U(buildSettings, at) != n) continue;
            var apis = new int[n];
            for (var i = 0; i < n; i++) apis[i] = (int)U(buildSettings, at + 4 + 4 * i);
            if (apis.All(a => a is 2 or 17 or 18 or 21)) return apis;
        }
        return null;
    }

    static uint U(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt32LittleEndian(s[o..]);

    /// <summary>Endian-aware cursor; reading past the end throws ArgumentOutOfRangeException.</summary>
    sealed class Reader(byte[] b, bool bigEndian)
    {
        public int Pos;
        ReadOnlySpan<byte> Take(int n) { if (n < 0 || Pos + n > b.Length) throw new ArgumentOutOfRangeException(nameof(n)); Pos += n; return b.AsSpan(Pos - n, n); }
        public byte U8() => Take(1)[0];
        public short I16() => (short)U16();
        public ushort U16() => bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(Take(2)) : BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public uint U32() => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(Take(4)) : BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public int I32() => (int)U32();
        public long I64() => bigEndian ? BinaryPrimitives.ReadInt64BigEndian(Take(8)) : BinaryPrimitives.ReadInt64LittleEndian(Take(8));
        public int Count(int max) => I32() is var n && n >= 0 && n <= max ? n : throw new ArgumentOutOfRangeException(nameof(max));
        public void Skip(int n) => Take(n);
        public void Align4() => Align(4);
        public void Align(int a) => Pos = Math.Min(b.Length, (Pos + a - 1) / a * a);
        public string CString()
        {
            var n = b.AsSpan(Pos).IndexOf((byte)0);
            if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
            var s = Encoding.UTF8.GetString(Take(n));
            Pos++;
            return s;
        }
    }
}

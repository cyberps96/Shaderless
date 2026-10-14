using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core.Carved;
using static SCSKiller.Core.Planning.PsoDb;

namespace SCSKiller.Core.Planning;

/// <summary>Hash-only recordings, the form the community database stores, serves and accepts:
/// every record the recorder writes, 'G' / 'C' / 'S' PSOs, 'R' / 'A' ray tracing state
/// objects and the 'N' NVAPI state of either, plus the root signatures they name as 'B' records, never shader bytes (a state object's DXIL libraries are
/// hashes, rehydrated from the install like shaders), and the uploader's 'L' flags (<see cref="LocalOnly"/>). Shared by the
/// client's export and the server's import and upload validation (the server links this file).</summary>
public static class HashOnly
{
    /// <summary>Records an input may hold (an upload, an import; same value in records.rs). <see cref="MaxEntryRecords"/>: what
    /// a client reads from a download.</summary>
    public const int MaxRecords = 200_000, MaxEntryRecords = 250_000, MaxRootSignature = 64 << 10;

    /// <summary>Root signatures, DXIL libraries and collections the distinct state objects (by key) of one input name: an
    /// upload, an import, a chunk of the app's. Each is a string held while it is checked. Real entries: 19,639 at most (3
    /// games' community downloads). <see cref="MaxEntryStateObjectRefs"/>: the same for an entry, what a merge makes and a
    /// download holds. Same values in records.rs.</summary>
    public const int MaxStateObjectRefs = 262_144, MaxEntryStateObjectRefs = 1_048_576;

    /// <summary>What an entry may name when uploads publish without the admin: an input's room short of
    /// <see cref="MaxEntryStateObjectRefs"/>, so a device filling an entry can't stop the rest (the admin's approve may use
    /// the room; `admin block --withdraw` takes the device's records back out).</summary>
    public const int MaxAutoStateObjectRefs = MaxEntryStateObjectRefs - MaxStateObjectRefs;

    /// <summary>The start of the refusal when the state objects of a <see cref="Canonical"/> input name more than its cap
    /// allows: a merge batch that gets it goes one upload at a time.</summary>
    public const string TooManyReferences = "state objects naming more than ";
    public const long MaxRaw = 256L << 20;

    /// <summary>The tags a hash-only recording may hold: all the recorder (proxy.cpp) writes but 'W' (a layer's, never
    /// shared), and 'L'. 'P' / 'Y' / '1' / '2' / 'M' are plan or work-folder records, rebuilt by each client from its own index.</summary>
    public const string Tags = "BGCSRANL";

    public sealed record Counts(int Psos, int GraphicsPsos, int DistinctVs);

    /// <summary>What <see cref="Canonical"/> dropped from a full local recording. <paramref name="StateObjects"/>: ray tracing
    /// state objects that can't be replayed from hashes (a root signature they name isn't in the recording, or a record they
    /// build on is missing or dropped).</summary>
    public sealed record Dropped(int ShaderBlobs, int StateObjects, int UnusedRootSignatures, int Duplicates);

    /// <summary>The canonical form: root-signature 'B' records the PSOs and state objects name, sorted by SHA-1, then the PSO
    /// records sorted by <see cref="Rec.Key"/>, then the state objects, each after the records it builds on (collections, the
    /// base of an addition; otherwise by key), then the 'N', then the 'L' records of kept ones, each by key, no duplicates;
    /// every record checked with the client's parser. A state object
    /// must have every root signature it names as a 'B' (a client can't rebuild those: they aren't in the game's files) and
    /// every record it builds on. <paramref name="local"/>: a full recording of this machine, whose shader blobs (DXIL
    /// libraries included) and unreplayable state objects are dropped, and so is what a layer wrapping the device made: each
    /// 'W' and the record it names as the driver's (per add-on build and settings; the bytes are a mod's). A 'W' names
    /// pipeline and state object records only; a root signature goes when no kept record names it; otherwise
    /// (someone else's upload) they are errors.
    /// InvalidDataException names the first problem, <see cref="TooManyReferences"/> past <paramref name="maxRefs"/>
    /// references (<see cref="MaxEntryStateObjectRefs"/> for an entry).</summary>
    /// <paramref name="layerMade"/>: more records a layer made, known from other recordings (<see cref="MiddlewarePacks.LayerMade"/>):
    /// dropped too in a local recording that has them without their 'W'.
    public static List<Rec> Canonical(IEnumerable<Rec> records, bool local, out Dropped dropped, int maxRefs = MaxStateObjectRefs, IReadOnlySet<string>? layerMade = null,
        int maxRecords = MaxRecords)
    {
        var blobs = new SortedDictionary<string, Rec>(StringComparer.Ordinal);
        var psos = new SortedDictionary<string, Rec>(StringComparer.Ordinal);
        var states = new SortedDictionary<string, (Rec Rec, StateObject So)>(StringComparer.Ordinal);
        var nv = new SortedDictionary<string, Rec>(StringComparer.Ordinal);
        var flags = new SortedDictionary<string, Rec>(StringComparer.Ordinal);
        var layer = new HashSet<string>();   // the records the driver got from a layer, named by 'W' records
        int shaders = 0, layerRecords = 0, dups = 0, n = 0, refs = 0;
        foreach (var r in records)
        {
            switch (r.Tag)
            {
                case 'B':
                    if (r.Payload.Length <= 20) throw new InvalidDataException("empty 'B' record");
                    var body = r.Payload.AsSpan(20);
                    var sha = Hex(r.Payload.AsSpan(0, 20));
                    if (Hex(SHA1.HashData(body)) != sha) throw new InvalidDataException($"blob {sha} doesn't match its SHA-1");
                    if (!Dxbc.IsRootSignatureOnly(body))
                    {
                        if (!local) throw new InvalidDataException($"blob {sha} is not a root signature (shader bytes are never shared)");
                        shaders++;
                    }
                    else if (body.Length > MaxRootSignature) throw new InvalidDataException($"root signature {sha} is over {MaxRootSignature} bytes");
                    else if (!Dxbc.RootSignatureValid(body)) throw new InvalidDataException($"root signature {sha} is malformed");
                    else if (!blobs.TryAdd(sha, r)) dups++;
                    break;
                case 'R' or 'A':
                    var so = ParseStateObject(r);   // InvalidDataException when malformed
                    if (!states.TryAdd(r.Key, (r, so))) dups++;   // a duplicate holds nothing more
                    else if ((refs += so.Libraries.Count + so.RootSignatures.Count + so.Depends.Count) > maxRefs)
                        throw new InvalidDataException($"{TooManyReferences}{maxRefs} root signatures, libraries and collections");
                    break;
                case 'N':
                    NvState.Parse(r);
                    if (!nv.TryAdd(r.Key, r)) dups++;
                    break;
                case 'L':
                    if (r.Payload.Length != 20) throw new InvalidDataException($"'L' record of {r.Payload.Length} bytes");
                    if (!flags.TryAdd(r.Key, r)) dups++;
                    break;
                case 'W' when local:
                    if (r.Payload.Length != 40) throw new InvalidDataException($"'W' record of {r.Payload.Length} bytes");
                    layer.Add(Hex(r.Payload.AsSpan(0, 20)));
                    layerRecords++;
                    break;
                default:
                    Check(r);
                    if (!psos.TryAdd(r.Key, r)) dups++;
                    break;
            }
            // dropped shader blobs and 'W' records don't count (29k shaders of a real 86k-record session)
            if (++n - shaders - layerRecords > maxRecords) throw new InvalidDataException($"more than {maxRecords} records");
        }
        if (local && layerMade != null) layer.UnionWith(layerMade);
        foreach (var k in layer)
        {
            psos.Remove(k);
            states.Remove(k);   // what builds on it can't be replayed from the upload: dropped below
        }
        // state objects: replayable from hashes, in dependency order (a record's key hashes the keys it builds on, so there
        // are no cycles; the walk guards anyway). Depth-first post-order with an explicit stack: uploads can chain any depth.
        var ordered = new List<Rec>();
        var done = new Dictionary<string, bool>(); // key -> kept; false while on the stack
        var stack = new List<(string Key, int Next)>();
        bool Fail(string key, string why) => local ? false : throw new InvalidDataException($"state object {key}: {why}");
        bool? Enter(string key)   // null: pushed, its dependencies next
        {
            if (done.TryGetValue(key, out var kept)) return kept;
            if (!states.TryGetValue(key, out var s)) return false;
            done[key] = false;
            if (s.So.Libraries.Count == 0 && s.So.Depends.Count == 0) return Fail(key, "no DXIL library, collection or base: nothing to create");
            if (s.So.RootSignatures.FirstOrDefault(h => !blobs.ContainsKey(h)) is { } rs) return Fail(key, $"root signature {rs} isn't in the recording");
            stack.Add((key, 0));
            return null;
        }
        foreach (var root in states.Keys)
            for (var result = Enter(root); stack.Count > 0;)
            {
                var (key, next) = stack[^1];
                var s = states[key];
                if (result == false)
                {
                    stack.RemoveAt(stack.Count - 1);
                    result = Fail(key, $"the record it builds on ({s.So.Depends[next - 1]}) isn't in the recording or can't be replayed");
                }
                else if (next == s.So.Depends.Count)
                {
                    stack.RemoveAt(stack.Count - 1);
                    ordered.Add(s.Rec);
                    result = done[key] = true;
                }
                else
                {
                    stack[^1] = (key, next + 1);
                    result = Enter(s.So.Depends[next]);
                }
            }
        if (psos.Count == 0 && ordered.Count == 0) throw new InvalidDataException("no PSO or state object records");

        var used = psos.Values.Select(r => Parse(r).Rs).Concat(ordered.SelectMany(r => ParseStateObject(r).RootSignatures)).ToHashSet();
        var keep = blobs.Where(b => used.Contains(b.Key)).Select(b => b.Value).ToList();
        dropped = new Dropped(shaders, states.Count - ordered.Count, blobs.Count - keep.Count, dups);
        var kept = psos.Keys.Concat(ordered.Select(r => r.Key)).ToHashSet();
        return [.. keep, .. psos.Values, .. ordered, .. nv.Values.Where(r => kept.Contains(Target(r))), .. flags.Values.Where(r => kept.Contains(Target(r)))];
    }

    /// <summary>A shared middleware pack's key: "&lt;GPU vendor&gt;:&lt;DLL vendor&gt;:&lt;DLL
    /// name, lower case&gt;:&lt;DLL SHA-1&gt;". Its entry's content hash is <see cref="PackHash"/>.</summary>
    public static string PackKey(string gpu, string vendor, string dll, string sha1) => $"{gpu}:{vendor}:{dll.ToLowerInvariant()}:{sha1}";

    public static string PackHash(string key) => Hex(SHA1.HashData(Encoding.UTF8.GetBytes(key)));

    public static bool IsPackKey(string? key) => key?.Split(':') is [var gpu, var vendor, var dll, var sha1]
        && gpu is "nvidia" or "amd"
        && vendor is { Length: >= 1 and <= 16 } && vendor.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))
        && dll is { Length: >= 5 and <= 96 } && dll.EndsWith(".dll", StringComparison.Ordinal)
        && dll.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '.' or '-')
        && sha1.Length == 40 && sha1.All(char.IsAsciiHexDigitLower);

    /// <summary>A pack holds PSOs and the root signatures they name, nothing else: InvalidDataException otherwise.</summary>
    public static List<Rec> CheckPack(List<Rec> canonical) =>
        canonical.FindIndex(r => r.Tag is not ('B' or 'C' or 'G' or 'S')) is var i and >= 0
            ? throw new InvalidDataException($"a pack holds only PSOs and root signatures, not '{canonical[i].Tag}' records") : canonical;

    /// <summary>The key of the record an 'N' or 'L' record is about.</summary>
    public static string Target(Rec r) => r.Tag == 'N' ? NvState.Parse(r).Target : Hex(r.Payload);

    /// <summary>An 'L' record (payload: the record's key) for each PSO and state object of <paramref name="records"/> that
    /// names a shader (a stage, a DXIL library) the game doesn't ship (<paramref name="shipped"/>): built at run time, or a
    /// mod's replacement. Another PC has its bytes only in a recording of its own.</summary>
    public static IEnumerable<Rec> LocalOnly(IEnumerable<Rec> records, Func<string, bool> shipped) =>
        records.Where(r => r.Tag switch
        {
            'G' or 'C' or 'S' => Parse(r).Stages.Values.Any(h => !shipped(h)),
            'R' or 'A' => ParseStateObject(r).Libraries.Any(h => !shipped(h)),
            _ => false,
        }).Select(r => new Rec('L', Convert.FromHexString(r.Key)));

    public static Counts Count(IEnumerable<Rec> records)
    {
        int all = 0, gfx = 0;
        var vs = new HashSet<string>();
        foreach (var r in records.Where(r => r.Tag is 'G' or 'C' or 'S'))
        {
            var p = Parse(r);
            all++;
            if (!p.Stages.ContainsKey((int)Stage.Compute)) gfx++;
            if (p.Stages.TryGetValue((int)Stage.Vertex, out var v)) vs.Add(v);
        }
        return new Counts(all, gfx, vs.Count);
    }

    /// <summary>Splits a canonical recording into canonical recordings (uploads) of at most
    /// <paramref name="maxRecords"/> PSO and state object records, about <paramref name="maxRaw"/> bytes and at most
    /// <see cref="MaxStateObjectRefs"/> state object references each, every one
    /// valid on its own: a state object travels with every record it builds on (a shared base then goes in several), a record
    /// with its 'N' and 'L', and each with the root signatures its records name (counted in its bytes). A state object whose
    /// closure alone is over those sizes is left out, with what builds on it (<paramref name="skipped"/>: how many); the rest
    /// still go.</summary>
    public static List<List<Rec>> Chunks(IReadOnlyList<Rec> canonical, int maxRecords, long maxRaw) => Chunks(canonical, maxRecords, maxRaw, out _);

    public static List<List<Rec>> Chunks(IReadOnlyList<Rec> canonical, int maxRecords, long maxRaw, out int skipped)
    {
        skipped = 0;
        var blobs = canonical.Where(r => r.Tag == 'B').ToList();
        var blobOf = blobs.ToDictionary(b => Hex(b.Payload.AsSpan(0, 20)));
        var states = canonical.Where(r => r.Tag is 'R' or 'A').ToDictionary(r => r.Key, r => (Rec: r, So: ParseStateObject(r)));
        var about = canonical.Where(r => r.Tag is 'N' or 'L').ToLookup(Target);
        var tooBig = new HashSet<string>();
        // a chunk holds whole closures, so the walk stops at its records; null: it builds on one no upload can carry
        List<Rec>? Close(Rec r, Dictionary<string, Rec> have)
        {
            var into = new Dictionary<string, Rec>();
            for (var stack = new Stack<Rec>([r]); stack.TryPop(out var x);)
            {
                if (tooBig.Contains(x.Key)) return null;
                if (have.ContainsKey(x.Key) || !into.TryAdd(x.Key, x)) continue;
                foreach (var a in about[x.Key]) into.TryAdd(a.Key, a);
                if (x.Tag is 'R' or 'A') foreach (var d in states[x.Key].So.Depends) stack.Push(states[d].Rec);
            }
            return [.. into.Values];
        }
        var chunks = new List<List<Rec>>();
        var chunk = new Dictionary<string, Rec>();
        var size = new Size(0, 0, 0);
        var chunkRs = new HashSet<string>();
        void Flush()
        {
            if (chunk.Count > 0) chunks.Add(Canonical([.. blobs, .. chunk.Values], local: false, out _));
            chunk = [];
            chunkRs = [];
            size = new Size(0, 0, 0);
        }
        // what a state object names: an upload's are capped (MaxStateObjectRefs)
        long Refs(Rec u) => u.Tag is 'R' or 'A' && states[u.Key].So is var so ? so.Libraries.Count + so.RootSignatures.Count + so.Depends.Count : 0;
        IEnumerable<string> Named(Rec u) => u.Tag is 'R' or 'A' ? states[u.Key].So.RootSignatures : u.Tag is 'G' or 'C' or 'S' ? [Parse(u).Rs] : [];
        // what records add to the chunk, the root signatures it doesn't name yet included
        Size Of(List<Rec> l)
        {
            var rs = l.SelectMany(Named).Where(h => blobOf.ContainsKey(h) && !chunkRs.Contains(h)).Distinct();
            return new(l.Count(u => u.Tag is not ('N' or 'L')), l.Sum(u => 5L + u.Payload.Length) + rs.Sum(h => 5L + blobOf[h].Payload.Length), l.Sum(Refs));
        }
        bool Over(Size s) => s.Refs > MaxStateObjectRefs || s.N > maxRecords || s.Raw > maxRaw;
        foreach (var r in canonical.Where(r => r.Tag is not ('B' or 'N' or 'L')))
        {
            var add = Close(r, chunk);
            if (add != null && chunk.Count > 0 && Over(size + Of(add)))
            {
                Flush();
                add = Close(r, chunk);
            }
            var added = add == null ? default : Of(add);
            if (add == null || chunk.Count == 0 && Over(added))
            {
                tooBig.Add(r.Key);
                skipped++;
                continue;
            }
            size += added;
            foreach (var u in add) chunk[u.Key] = u;
            chunkRs.UnionWith(add.SelectMany(Named));
        }
        Flush();
        return chunks;
    }

    readonly record struct Size(long N, long Raw, long Refs)
    {
        public static Size operator +(Size a, Size b) => new(a.N + b.N, a.Raw + b.Raw, a.Refs + b.Refs);
    }

    /// <summary>The db bytes of <paramref name="records"/>, Brotli (quality 11): an object of the community database. Encoded
    /// as the records are read, holding no copy of them (the bytes are TryCompress's one-shot output).</summary>
    public static byte[] Compress(IEnumerable<Rec> records)
    {
        using var enc = new BrotliEncoder(11, 22);
        using var out_ = new MemoryStream();
        var buf = new byte[1 << 16];
        void Feed(ReadOnlySpan<byte> src, bool final)
        {
            for (var status = OperationStatus.DestinationTooSmall; status == OperationStatus.DestinationTooSmall || !src.IsEmpty;)
            {
                status = enc.Compress(src, buf, out var used, out var wrote, final);
                if (status == OperationStatus.InvalidData) throw new InvalidOperationException("Brotli failed");
                out_.Write(buf, 0, wrote);
                src = src[used..];
                if (status == OperationStatus.Done || status == OperationStatus.NeedMoreData && src.IsEmpty) break;
            }
        }
        Span<byte> head = stackalloc byte[5];
        foreach (var r in records)
        {
            head[0] = (byte)r.Tag;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(head[1..], (uint)r.Payload.Length);
            Feed(head, false);
            Feed(r.Payload, false);
        }
        Feed([], true);
        return out_.ToArray();
    }

    /// <summary>The records of a Brotli-compressed db, at most <paramref name="max"/> bytes decompressed; a torn tail is an error.
    /// <paramref name="limit"/>: only the first that many are made (<see cref="Records"/>). Decoded twice, holding no
    /// decompressed copy: first the stream and its framing to the end, then the records.</summary>
    public static List<Rec> Decompress(ReadOnlySpan<byte> compressed, long max = MaxRaw, int limit = int.MaxValue)
    {
        Walk(compressed, max, null, 0);
        var recs = new List<Rec>();
        Walk(compressed, max, recs, limit);
        return recs;
    }

    /// <summary>One decoding of <see cref="Decompress"/>'s input: the records into <paramref name="into"/> (at most
    /// <paramref name="limit"/>), or with none only the checks; a Brotli error before a framing one.</summary>
    static void Walk(ReadOnlySpan<byte> compressed, long max, List<Rec>? into, int limit)
    {
        // BrotliDecoder, not BrotliStream: the stream reads garbage or a truncated input as a short, valid end
        using var d = new BrotliDecoder();
        var buf = new byte[1 << 16];
        Span<byte> head = stackalloc byte[5];
        int headLen = 0, at = 0;
        long raw = 0, left = 0;   // left: the current payload's bytes still to come
        byte[]? payload = null;
        for (var status = OperationStatus.DestinationTooSmall; status != OperationStatus.Done;)
        {
            status = d.Decompress(compressed, buf, out var used, out var wrote);
            if (status is OperationStatus.InvalidData or OperationStatus.NeedMoreData) throw new InvalidDataException("not a complete Brotli stream");
            if (raw + wrote > max) throw new InvalidDataException($"over {max} bytes decompressed");
            compressed = compressed[used..];
            raw += wrote;
            for (var chunk = buf.AsSpan(0, wrote); !chunk.IsEmpty;)
                if (left > 0)
                {
                    var n = (int)Math.Min(left, chunk.Length);
                    if (payload != null) chunk[..n].CopyTo(payload.AsSpan(at));
                    (at, left) = (at + n, left - n);
                    chunk = chunk[n..];
                    if (left == 0 && payload != null) into!.Add(new Rec((char)head[0], payload));
                }
                else
                {
                    var n = Math.Min(5 - headLen, chunk.Length);
                    chunk[..n].CopyTo(head[headLen..]);
                    headLen += n;
                    chunk = chunk[n..];
                    if (headLen < 5) continue;
                    headLen = 0;
                    left = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(head[1..]);
                    // only on the second pass, whose framing the first checked: no length is allocated before its bytes exist
                    payload = into != null && into.Count < limit ? new byte[left] : null;
                    at = 0;
                    if (left == 0 && payload != null) into!.Add(new Rec((char)head[0], payload));
                }
        }
        if (!compressed.IsEmpty) throw new InvalidDataException("bytes after the Brotli stream");
        if (headLen != 0 || left != 0) throw new InvalidDataException("truncated record");
    }

    /// <summary>Someone else's recording for <see cref="Canonical"/> with local: false, which refuses it by its
    /// <see cref="MaxEntryRecords"/> + 1st record (a shader blob is an error there, so every record counts): only that many are
    /// made, however many the bytes frame.</summary>
    public const int RemoteLimit = MaxEntryRecords + 1;

    /// <summary>The records of an uncompressed db, read to its last byte; a torn tail is an error (unlike <see cref="PsoDb.Read(Stream)"/>,
    /// which stops there like the proxy). The framing is checked to the end before anything is allocated from a length
    /// field, then the first <paramref name="limit"/> records are made.</summary>
    public static List<Rec> Records(ReadOnlySpan<byte> raw, int limit = int.MaxValue)
    {
        static long Len(ReadOnlySpan<byte> raw, long at) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(raw[(int)(at + 1)..]);
        for (var at = 0L; at < raw.Length; at += 5 + Len(raw, at))
            if (at + 5 > raw.Length || at + 5 + Len(raw, at) > raw.Length) throw new InvalidDataException("truncated record");
        var recs = new List<Rec>();
        for (var at = 0L; at < raw.Length && recs.Count < limit; at += 5 + Len(raw, at))
            recs.Add(new Rec((char)raw[(int)at], raw.Slice((int)at + 5, (int)Len(raw, at)).ToArray()));
        return recs;
    }
}

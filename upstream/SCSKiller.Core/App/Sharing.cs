using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using SCSKiller.Core.Planning;

namespace SCSKiller.Core.App;

/// <summary>The upload's X-SCSK-Upload header, sent as base64url of snake_case
/// JSON.</summary>
public sealed record UploadMeta(string StoreBuildKey, string ContentHash, string? Engine = null, string? Vendor = null, string? AppVersion = null,
    UploadDll[]? Middleware = null);

public sealed record UploadDll(string Name, string Sha1);

/// <summary>What sharing did with a game's recording (games\&lt;id&gt;\shared.json). <see cref="Stamp"/>: the recording and
/// content hash last looked at to the end (uploaded, or found to have nothing to share); <see cref="Sent"/>: the uploads of
/// it the server has (SHA-256 of an upload's records, "|", its content hash), kept across a pass cut short; the rest is the
/// last upload, if any (<see cref="Psos"/> / <see cref="NewPsos"/>: summed over its uploads).</summary>
public sealed record SharedRecording(string Stamp, DateTimeOffset? At = null, string? UploadId = null, int Psos = 0, int NewPsos = 0, string[]? Sent = null);

/// <summary>The write side of the community database: a game's own
/// recording (recording.db, never the merge with the community's), stripped to its hash-only form, posted with an anonymous
/// upload device's token. That device is registered once (POST /v1/devices) and kept DPAPI-protected in upload.dat, apart
/// from the Patreon sign-in (auth.dat), which this class never sees. Works signed out. Quiet: failures
/// land in <see cref="Problem"/> and back off; nothing throws but cancellation.</summary>
public sealed class Sharing
{
    public const int MaxBody = 4 << 20;   // = the server's cap
    // One upload: real sessions run 0.8 to 1 KB a record hash-only, so ~20 MB and well under the origin's default 32 MB
    public const int ChunkRecords = 20_000;
    public const long ChunkRaw = 24 << 20;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    readonly string file;
    readonly Func<bool> enabled;
    readonly RouteFailover routes;
    readonly HttpClient http;
    readonly TimeProvider clock;
    DateTimeOffset backoffUntil;

    /// <param name="enabled">Settings.ShareRecordings, read at each call: off means no work and no request at all</param>
    public Sharing(string dataDir, Func<bool> enabled, RouteFailover? routes = null, TimeProvider? clock = null)
    {
        file = Path.Combine(dataDir, "upload.dat");
        this.enabled = enabled;
        this.routes = routes ?? RouteFailover.Default;
        http = new HttpClient(this.routes, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(60) };
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>The last failure, in plain words; null after a success.</summary>
    public string? Problem { get; private set; }

    /// <summary>Why the last share waited without a problem: a recording whose layer records aren't whole yet
    /// (<see cref="Recordings.IncompleteLayerList"/>); null = it didn't. The caller logs it once a pass.</summary>
    public string? Waiting { get; set; }

    /// <summary>The upload device's id (for the admin's trust-device), null before the first registration.</summary>
    public string? DeviceId => Load()?.Id;

    /// <summary>Forgets the upload device; the next upload registers a new one.</summary>
    public void Reset() => File.Delete(file);

    public static SharedRecording? Shared(string gameDir) => Community.Read<SharedRecording>(Path.Combine(gameDir, "shared.json"));

    /// <summary>Uploads <paramref name="gameDir"/>\recording.db for the build <paramref name="contentHash"/> when sharing is on
    /// and it changed since the last look: in uploads of at most <see cref="ChunkRecords"/> records (<see cref="HashOnly.Chunks"/>),
    /// each skipped when the server already has it or the downloaded community entry for that build holds all its records.
    /// <paramref name="meta"/> is built only for an actual upload (it may hash middleware DLLs). Returns the new
    /// <see cref="SharedRecording"/> after an upload, else null.</summary>
    public async Task<SharedRecording?> ShareAsync(string gameDir, string contentHash, Func<UploadMeta> meta,
        Func<IEnumerable<string>>? middlewareShaders = null, Func<IReadOnlySet<string>>? layerMade = null, CancellationToken ct = default)
    {
        var db = new FileInfo(Path.Combine(gameDir, "recording.db"));
        if (!enabled() || !db.Exists || clock.GetUtcNow() < backoffUntil) return null;
        Problem = null;   // per game; a back-off's stays
        // "|2": re-examines stamps that marked an over-cap recording as done; the shader list: flags once it is there
        var list = new FileInfo(Path.Combine(gameDir, ShippedFile));
        var stamp = $"{db.Length}:{db.LastWriteTimeUtc.Ticks}|{contentHash}|2" + (list.Exists ? $"|{list.Length}:{list.LastWriteTimeUtc.Ticks}" : "");
        var last = Shared(gameDir) ?? new("");
        if (last.Stamp == stamp) return null;
        var shared = Path.Combine(gameDir, "shared.json");
        List<PsoDb.Rec> records;
        IReadOnlySet<string>? excluded;
        try { excluded = layerMade?.Invoke(); }   // read now, right before the payload; one that can't be read throws: nothing shared
        catch (Recordings.IncompleteLayerList e) { Waiting = e.Message; return null; }
        try { records = HashOnly.Canonical(PsoDb.Read(db.FullName), local: true, out _, HashOnly.MaxEntryStateObjectRefs, excluded); }   // shader and DXIL library blobs dropped (state objects kept)
        catch (InvalidDataException e)   // e.g. no PSOs yet: not again until the recording changes
        {
            Community.Write(shared, last with { Stamp = stamp });
            Problem = $"A recording has nothing to share ({e.Message}).";
            return null;
        }
        if (Shipped(gameDir, contentHash) is { } shipped)
        {
            shipped.UnionWith(middlewareShaders?.Invoke() ?? []);
            records = HashOnly.Canonical([.. records, .. HashOnly.LocalOnly(records, shipped.Contains)], local: false, out _, HashOnly.MaxEntryStateObjectRefs);
        }
        var have = Community.Downloaded(gameDir)?.ContentHash == contentHash
            ? PsoDb.Read(Path.Combine(gameDir, "community.db")).Where(r => r.Tag != 'B').Select(r => r.Key).ToHashSet() : [];
        var sent = new List<string>();
        var (uploadId, psos, fresh) = ((string?)null, 0, 0);
        var chunks = HashOnly.Chunks(records, ChunkRecords, ChunkRaw, out var tooLarge);
        if (tooLarge > 0) Problem = $"{tooLarge} ray tracing records are more than an upload may carry with what they build on ({ChunkRecords} records, {ChunkRaw >> 20} MB, {HashOnly.MaxStateObjectRefs} references): shared without them.";
        foreach (var whole in chunks)
        {
            // what a layer made, read again right before each payload: one learned during an earlier upload is left out too
            IReadOnlySet<string>? excludedNow;
            try { excludedNow = layerMade?.Invoke(); }
            catch (Recordings.IncompleteLayerList e) { Waiting = e.Message; return Stop(null); }
            catch (Exception e) { return Stop($"What a layer made can't be read ({e.Message}): sharing waits."); }   // any failure: nothing more goes
            List<PsoDb.Rec> chunk;
            try { chunk = excludedNow == null ? whole : HashOnly.Canonical(whole, local: true, out _, layerMade: excludedNow); }
            catch (InvalidDataException) { continue; }   // nothing left of it but root signatures
            var id = Id(chunk, contentHash);   // the same records for another build are new there
            if (last.Sent?.Contains(id) == true || chunk.All(r => r.Tag == 'B' || have.Contains(r.Key)))
            {
                sent.Add(id);
                continue;
            }
            var body = HashOnly.Compress(chunk);
            if (body.Length > MaxBody) return Stop($"A recording is too large to share ({body.Length >> 20} MB compressed, the limit is {MaxBody >> 20} MB).");
            try
            {
                var r = await PostAsync(body, meta(), ct);
                if (r?.StatusCode == HttpStatusCode.Unauthorized)   // the device expired (90 days unused) or was revoked: once with a new one
                {
                    r.Dispose();
                    Reset();
                    r = await PostAsync(body, meta(), ct);
                }
                if (r == null) return Stop(Problem);
                using (r)
                {
                    if (r.StatusCode == HttpStatusCode.Accepted)
                    {
                        var a = await r.Content.ReadFromJsonAsync<JsonElement>(ct);
                        uploadId = a.GetProperty("upload_id").GetString();
                        psos += a.GetProperty("records").GetInt32();
                        fresh += a.GetProperty("new_records").GetInt32();
                        sent.Add(id);
                        continue;
                    }
                    var why = $"Sharing a recording was refused (error {(int)r.StatusCode}).";
                    if (r.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.Forbidden)
                    {
                        Problem = why;
                        if (r.StatusCode == HttpStatusCode.Forbidden) break;   // this device is blocked: done until the recording changes
                        sent.Add(id);   // refused for good; the other uploads still go
                        continue;
                    }
                    BackOff(r);
                    return Stop(why);
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                backoffUntil = clock.GetUtcNow() + TimeSpan.FromMinutes(5);   // offline, or an answer this version can't read
                return Stop(e is HttpRequestException or OperationCanceledException ? "Can't reach the community database to share a recording." : e.Message);
            }
        }
        var got = uploadId is null ? last with { Stamp = stamp, Sent = [.. sent] } : new SharedRecording(stamp, clock.GetUtcNow(), uploadId, psos, fresh, [.. sent]);
        Community.Write(shared, got);   // looked at: not again until the recording changes
        return uploadId is null ? null : got;

        // Cut short: not stamped, so the next pass tries again, skipping what the server already has
        SharedRecording? Stop(string? why)
        {
            if (sent.Count > 0) Community.Write(shared, last with { Sent = [.. (last.Sent ?? []).Union(sent)] });
            if (why is not null) Problem = why;
            return null;
        }
    }

    /// <summary>Uploads each pack of a shared vendor (<see cref="Middleware.SharedVendors"/>) in <paramref name="packsDir"/>
    /// (this PC's own, filled from its recordings on the GPU vendor <paramref name="gpu"/>: <see cref="MiddlewarePack.PackHeader.Gpu"/>)
    /// that changed since its last upload, under its pack key for that vendor, when sharing is on. Stamped per pack file in
    /// packs-shared.json, so a pack goes again only once it gains records. Returns the DLL name and PSO count of each upload.</summary>
    /// <paramref name="layered"/>: the records a layer made (<see cref="MiddlewarePacks.LayerMade"/>), read again before each
    /// pack's payload and never in an upload whatever a pack holds; one that can't be read ends the pass.
    public async Task<List<(string Dll, int Psos)>> SharePacksAsync(string packsDir, string gpu, string appVersion, Func<IReadOnlySet<string>>? layered = null,
        CancellationToken ct = default)
    {
        var done = new List<(string, int)>();
        if (!enabled() || clock.GetUtcNow() < backoffUntil) return done;
        var stampFile = Path.Combine(Path.GetDirectoryName(file)!, "packs-shared.json");
        var stamps = Community.Read<Dictionary<string, string>>(stampFile) ?? [];
        var files = Middleware.SharedVendors.Select(v => Path.Combine(packsDir, v)).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.pack")).Order(StringComparer.OrdinalIgnoreCase).ToList();
        try
        {
            foreach (var path in files)
            {
                var fi = new FileInfo(path);
                var stamp = $"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}|{gpu}";
                var name = Path.GetFileName(path);
                if (stamps.GetValueOrDefault(name) == stamp) continue;
                List<PsoDb.Rec> records;
                MiddlewarePack pack;
                IReadOnlySet<string>? excluded;
                try { excluded = layered?.Invoke(); }
                catch (Recordings.IncompleteLayerList e) { Waiting = e.Message; break; }
                try { records = MiddlewarePacks.Records(pack = MiddlewarePack.Read(path), excluded); }
                catch (Exception e) when (e is InvalidDataException or IOException or JsonException)
                {
                    stamps[name] = stamp;   // nothing to share until it changes
                    continue;
                }
                if (pack.Header.Gpu != gpu)   // another GPU vendor's, or from before packs kept theirs: shared once a recording here adopts it
                {
                    stamps[name] = stamp;
                    continue;
                }
                var key = HashOnly.PackKey(gpu, pack.Header.Vendor, pack.Header.Dll, pack.Header.ContentHash);
                var body = HashOnly.Compress(records);
                if (body.Length > MaxBody)
                {
                    Problem = $"An upscaler pack is too large to share ({body.Length >> 20} MB compressed, the limit is {MaxBody >> 20} MB).";
                    stamps[name] = stamp;
                    continue;
                }
                var meta = new UploadMeta(key, HashOnly.PackHash(key), Vendor: gpu, AppVersion: appVersion);
                var r = await PostAsync(body, meta, ct);
                if (r?.StatusCode == HttpStatusCode.Unauthorized)   // the device expired or was revoked: once with a new one
                {
                    r.Dispose();
                    Reset();
                    r = await PostAsync(body, meta, ct);
                }
                if (r == null) break;
                using (r)
                {
                    if (r.StatusCode == HttpStatusCode.Accepted)
                    {
                        done.Add((pack.Header.Dll, (await r.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("records").GetInt32()));
                        stamps[name] = stamp;
                        continue;
                    }
                    Problem = $"Sharing an upscaler pack was refused (error {(int)r.StatusCode}).";
                    if (r.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge)
                    {
                        stamps[name] = stamp;   // refused for good: not again until it changes
                        continue;
                    }
                    if (r.StatusCode == HttpStatusCode.Forbidden) stamps[name] = stamp;   // blocked: not again until the pack changes, like a recording
                    else BackOff(r);
                    break;
                }
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            backoffUntil = clock.GetUtcNow() + TimeSpan.FromMinutes(5);
            Problem = e is HttpRequestException or OperationCanceledException ? "Can't reach the community database to share an upscaler pack." : e.Message;
        }
        finally { Community.Write(stampFile, stamps); }
        return done;
    }

    /// <summary>A game folder's list of the shaders its build ships (the index's): the content hash (20 bytes), then each
    /// shader's SHA-1. What an upload flags against (<see cref="HashOnly.LocalOnly"/>).</summary>
    public const string ShippedFile = "index.shaders";

    public static void SaveShipped(string gameDir, ShaderIndex index)
    {
        if (index.ContentHash.Length != 40) return;   // not a build Share uploads for
        Directory.CreateDirectory(gameDir);
        var tmp = Path.Combine(gameDir, ShippedFile + ".tmp");
        using (var f = new BufferedStream(File.Create(tmp), 1 << 20))
            foreach (var h in index.Shaders.Keys.Prepend(index.ContentHash)) f.Write(Convert.FromHexString(h));
        File.Move(tmp, Path.Combine(gameDir, ShippedFile), true);
    }

    /// <summary>The shaders of <see cref="ShippedFile"/>, null when it's missing or for another build.</summary>
    internal static HashSet<string>? Shipped(string gameDir, string contentHash)
    {
        var path = Path.Combine(gameDir, ShippedFile);
        var b = File.Exists(path) ? File.ReadAllBytes(path) : [];
        if (b.Length < 20 || b.Length % 20 != 0 || Convert.ToHexStringLower(b.AsSpan(0, 20)) != contentHash) return null;
        return [.. b.Chunk(20).Skip(1).Select(Convert.ToHexStringLower)];
    }

    static string Id(List<PsoDb.Rec> chunk, string contentHash)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var r in chunk)
        {
            h.AppendData([(byte)r.Tag, .. BitConverter.GetBytes(r.Payload.Length)]);
            h.AppendData(r.Payload);
        }
        return $"{Convert.ToHexStringLower(h.GetHashAndReset())}|{contentHash}";
    }

    /// <summary>POST /v1/upload with the upload device's token (registered first if there is none); null when sharing is off or no device could be had.</summary>
    async Task<HttpResponseMessage?> PostAsync(byte[] body, UploadMeta meta, CancellationToken ct)
    {
        if (!enabled() || await DeviceTokenAsync(ct) is not { } token || !enabled()) return null;   // again: turned off while registering
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(routes.Primary, "v1/upload")) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/octet-stream");
        request.Headers.Authorization = new("Bearer", token);
        request.Headers.Add("X-SCSK-Upload", Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(meta, Json)));
        return await http.SendAsync(request, ct);
    }

    async Task<string?> DeviceTokenAsync(CancellationToken ct)
    {
        if (Load()?.Token is { } token) return token;
        using var r = await http.PostAsync(new Uri(routes.Primary, "v1/devices"), null, ct);
        if (!r.IsSuccessStatusCode)
        {
            BackOff(r);   // 3 registrations a day per IP
            Problem = $"Couldn't register for sharing (error {(int)r.StatusCode}).";
            return null;
        }
        var j = await r.Content.ReadFromJsonAsync<JsonElement>(ct);
        var device = new Device(j.GetProperty("device_token").GetString()!, j.GetProperty("device_id").GetString()!);
        Dpapi.Save(file, device);
        return device.Token;
    }

    // 429/503: as long as Retry-After says (1 h without one); 501 (uploads not enabled on this server): a day; others: 5 minutes
    void BackOff(HttpResponseMessage r) => backoffUntil = clock.GetUtcNow() + (r.StatusCode switch
    {
        HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => r.Headers.RetryAfter switch
        {
            { Delta: { } d } => d,
            { Date: { } at } => at - clock.GetUtcNow(),
            _ => TimeSpan.FromHours(1),
        },
        HttpStatusCode.NotImplemented => TimeSpan.FromDays(1),
        _ => TimeSpan.FromMinutes(5),
    });

    // upload.dat: {"Token":"sd1_...","Id":"..."} under DPAPI, like auth.dat but its own file: never the Patreon device
    sealed record Device(string Token, string Id);

    Device? Load() => Dpapi.Load<Device>(file);   // null (another user's or damaged): register again
}

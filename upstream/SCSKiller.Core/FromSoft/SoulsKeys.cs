using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SCSKiller.Core.App;

namespace SCSKiller.Core.FromSoft;

/// <summary>The public RSA keys of a FromSoftware game's archives, read from the game's own exe at run time: it carries
/// them as PEM text ("-----BEGIN RSA PUBLIC KEY-----", in .rdata, in the clear even under Elden Ring's protection); each
/// is matched to its archive by decrypting the archive's first header block to "BHD5". Nothing is shipped: no key is in
/// the source or the binaries. Read-only, a file scan (the game is never run or attached to, anti-cheat or not).
/// Kept only locally, in %LOCALAPPDATA%\SCSKiller\games\&lt;id&gt;\archive.keys, never in plans, logs or anything
/// shared: "exe &lt;SHA-256&gt; &lt;size:write time&gt;" (the exe last scanned: once per exe build), then one
/// "&lt;archive&gt; &lt;key&gt;" line per archive (PKCS#1 DER, base64: a PEM body). A user may add archive lines by hand for
/// a game whose exe doesn't carry its keys in the clear; failing both, a title that names a <see cref="Uxm"/> section
/// (Dark Souls III) downloads them from there, to this file only. Every key is checked before it's used or kept.</summary>
/// <param name="download">fetches a URL's text (null on failure): <see cref="Download.Text"/>, a fake in tests</param>
public sealed class SoulsKeys(string dataDir, Func<string, string?>? download = null)
{
    /// <summary>The community's published keys: UXM Selective Unpack's UXM/ArchiveKeys.cs, pinned to commit 9501be87
    /// (2026-02-15) so an upstream edit can't change what is fetched. One C# dictionary per game (DarkSouls3Keys, ...).</summary>
    public const string Uxm = "https://raw.githubusercontent.com/Nordgaren/UXM-Selective-Unpack/9501be87e272b6dae55e60a13c3f4753ca6fb3bb/UXM/ArchiveKeys.cs";

    public string KeyFile(Game g) => Path.Combine(new AppStore(dataDir).GameDir(g.Id), "archive.keys");

    static string Stamp(string exe) => new FileInfo(exe) is { Exists: true } f ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "";

    /// <summary>The key of each archive (name -> .bhd path) some key opens; <paramref name="missing"/>: the archives none
    /// opens. <paramref name="uxm"/>: the title's dictionary in <see cref="Uxm"/>, downloaded when the file and the exe
    /// leave some archive without a key (null: never).</summary>
    public Dictionary<string, string> Get(Game game, IReadOnlyDictionary<string, string> archives, string? uxm, out List<string> missing)
    {
        var file = KeyFile(game);
        var lines = File.Exists(file) ? File.ReadAllLines(file) : [];
        var scanned = lines.FirstOrDefault(l => l.StartsWith("exe "))?.Split(' ');
        var stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines.Where(l => !l.StartsWith("exe ")))
            if (l.Trim().Split(' ', 2, StringSplitOptions.TrimEntries) is [var name, var key]) stored[name] = key;
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, bhd) in archives)
            if (stored.TryGetValue(name, out var k) && Opens(bhd, k)) keys[name] = k;
        List<string> Missing() => archives.Keys.Where(a => !keys.ContainsKey(a)).ToList();
        missing = Missing();
        if (missing.Count == 0) return keys;

        var write = false;
        void Match(List<string> found)
        {
            foreach (var name in Missing())
                if (found.FirstOrDefault(k => Opens(archives[name], k)) is { } k) { stored[name] = keys[name] = k; write = true; }
        }
        // scan the exe once per build: the stamp saves rehashing an unchanged exe, the hash a touched one. ponytail: an archive
        // added without an exe update isn't matched until the exe changes (FromSoftware's DLCs came with one); delete the file to rescan
        var exeLine = scanned is null ? null : string.Join(' ', scanned);
        var stamp = Stamp(game.ExePath);
        if (stamp != "" && !(scanned is [_, _, var s] && s == stamp))
            try
            {
                var exe = File.ReadAllBytes(game.ExePath); // ponytail: whole exe in memory (Nightreign 119 MB), once per exe build
                var hash = Convert.ToHexStringLower(SHA256.HashData(exe));
                if (scanned is not [_, var h, ..] || h != hash) Match(Pem(exe));
                exeLine = $"exe {hash} {stamp}";
                write = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        // last, the published keys (tried every Detect until they open the archives: a failure isn't remembered)
        if (keys.Count < archives.Count && uxm != null && (download ?? Download.Text)(Uxm) is { } source) Match(UxmKeys(source, uxm));
        missing = Missing();
        if (!write) return keys;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllLines(file, stored.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => $"{p.Key} {p.Value}").Prepend(exeLine).OfType<string>());
        return keys;
    }

    /// <summary>The PEM keys of one game's dictionary (<paramref name="section"/>, e.g. "DarkSouls3Keys") in UXM's C#
    /// source: from its name to the initializer's end ("};"), so another game's keys are never tried.</summary>
    public static List<string> UxmKeys(string source, string section)
    {
        if (Regex.Match(source, $@"\b{Regex.Escape(section)}\s*=") is not { Success: true } m) return [];
        var end = source.IndexOf("};", m.Index, StringComparison.Ordinal);
        return Pem(Encoding.ASCII.GetBytes(source[m.Index..(end < 0 ? source.Length : end)]));
    }

    static readonly byte[] Begin = "-----BEGIN RSA PUBLIC KEY-----"u8.ToArray(), End = "-----END RSA PUBLIC KEY-----"u8.ToArray();

    /// <summary>Every PEM "RSA PUBLIC KEY" block in <paramref name="b"/> that parses: its body (base64 DER), distinct.</summary>
    public static List<string> Pem(ReadOnlySpan<byte> b)
    {
        var keys = new List<string>();
        for (int i = 0, k; (k = b[i..].IndexOf(Begin)) >= 0; i += k + Begin.Length)
        {
            var body = b[(i + k + Begin.Length)..];
            if (body.IndexOf(End) is not (var e and > 0 and < 4096)) continue;
            var key = string.Concat(Encoding.ASCII.GetString(body[..e]).Where(c => !char.IsWhiteSpace(c)));
            if (!keys.Contains(key) && Parses(key)) keys.Add(key);
        }
        return keys;
    }

    static bool Parses(string key)
    {
        try { using var rsa = RSA.Create(); rsa.ImportRSAPublicKey(Convert.FromBase64String(key), out _); return true; }
        catch (Exception e) when (e is FormatException or CryptographicException) { return false; }
    }

    /// <summary>The key decrypts the archive header's first block to the BHD5 magic (one block: cheap).</summary>
    public static bool Opens(string bhd, string key)
    {
        try
        {
            var block = new byte[256];
            using (var f = File.OpenRead(bhd)) if (f.ReadAtLeast(block, block.Length, false) != block.Length) return false;
            return Souls.DecryptBhd(block, key).AsSpan().StartsWith("BHD5"u8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or CryptographicException or InvalidDataException or AggregateException) { return false; }
    }
}

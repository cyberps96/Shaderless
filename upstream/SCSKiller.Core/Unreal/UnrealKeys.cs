using System.Diagnostics;
using System.Reflection.PortableExecutable;
using CUE4Parse.Encryption.Aes;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Unreal;

/// <summary>The pak AES key of an encrypted Unreal game. Kept only locally, in %LOCALAPPDATA%\SCSKiller\games\&lt;id&gt;\aes.key
/// (hex; the user may also put a key there by hand), never in plans, logs or anything meant to be shared. Found statically
/// in the game's own exe (<see cref="Scan"/>), once per exe build: a failed scan is remembered in aes.scan with the exe's
/// size and write time. Every key is checked against an encrypted container before it is used or stored.</summary>
public sealed class UnrealKeys(string dataDir)
{
    string KeyFile(Game g) => Path.Combine(new AppStore(dataDir).GameDir(g.Id), "aes.key");
    string ScanFile(Game g) => Path.Combine(new AppStore(dataDir).GameDir(g.Id), "aes.scan");
    /// <summary>Bump when <see cref="Scan"/> finds keys it missed before: a remembered failure is then scanned again.</summary>
    const int ScanVersion = 3;
    static string Stamp(string exe) => new FileInfo(exe) is { Exists: true } f ? $"{ScanVersion}:{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "";

    /// <summary>A key that <paramref name="opens"/> the game's encrypted containers: the stored one, else a static scan of
    /// the exe (not for anti-cheat games: their exe isn't read). <paramref name="why"/> says how, without the key.</summary>
    public FAesKey? Get(Game game, Func<FAesKey, bool> opens, out string why)
    {
        if (File.Exists(KeyFile(game)) && Parse(File.ReadAllText(KeyFile(game))) is { } stored)
        {
            if (opens(stored)) { why = "stored key"; return stored; }
            why = $"the key in {KeyFile(game)} doesn't open the game's files";
            return null;
        }
        if (Games.GameFiles.DetectAntiCheat(game) != AntiCheat.None) { why = "encrypted; anti-cheat game: its exe isn't scanned for the key"; return null; }
        var stamp = Stamp(game.ExePath);
        if (File.Exists(ScanFile(game)) && File.ReadAllLines(ScanFile(game)) is [var s, var reason, ..] && s == stamp) { why = reason; return null; }
        var sw = Stopwatch.StartNew();
        FAesKey? key;
        string how;
        try { key = Scan(game.ExePath, opens, out how); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) // Xbox app games: the exe is encrypted at rest
        {
            why = "encrypted; the game's exe can't be read, so it isn't scanned for the key: give the key by hand";
            return null;
        }
        why = $"{how} ({sw.Elapsed.TotalSeconds:F1} s)";
        Directory.CreateDirectory(Path.GetDirectoryName(KeyFile(game))!);
        if (key != null) File.WriteAllText(KeyFile(game), key.KeyString);
        else File.WriteAllLines(ScanFile(game), [stamp, why]);
        return key;
    }

    /// <summary>The stored key file's write time, "" if none: a key added or changed re-runs detection.</summary>
    public string KeyStamp(Game game) => File.Exists(KeyFile(game)) ? File.GetLastWriteTimeUtc(KeyFile(game)).Ticks.ToString() : "";

    /// <summary>The stored key, unchecked (a full mount checks it: SubmitKey mounts only what it opens).</summary>
    public FAesKey? Stored(Game game) => File.Exists(KeyFile(game)) ? Parse(File.ReadAllText(KeyFile(game))) : null;

    /// <summary>A key the user supplies (hex, "0x" optional): stored if it opens the game's files.</summary>
    public bool Set(Game game, string key, Func<FAesKey, bool> opens)
    {
        if (Parse(key) is not { } k || !opens(k)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(KeyFile(game))!);
        File.WriteAllText(KeyFile(game), k.KeyString);
        File.Delete(ScanFile(game));
        return true;
    }

    static FAesKey? Parse(string text)
    {
        var hex = text.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? new FAesKey("0x" + hex) : null;
    }

    /// <summary>Static key search, the way AESDumpster works: UE's key callback (UE_REGISTER_ENCRYPTION_KEY) writes the 32 bytes
    /// to its out parameter as 8 x mov dword [reg+d], imm32 (adjacent or interleaved with other code), as 4 x mov r64, imm64
    /// + stores, or as 2 x 16-byte loads from .rdata + stores. Every candidate from the executable sections goes to
    /// <paramref name="opens"/>; the first that opens wins. Streams the exe (4 MB at a time); read-only.</summary>
    public static FAesKey? Scan(string exePath, Func<FAesKey, bool> opens, out string how)
    {
        using var f = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        SectionHeader[] sections;
        using (var pe = new PEReader(f, PEStreamOptions.LeaveOpen)) sections = [.. pe.PEHeaders.SectionHeaders];
        var code = sections.Where(s => (s.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0).ToList();
        var tried = new HashSet<string>();
        FAesKey? found = null;
        var pattern = "";
        void Try(byte[] k, string how)
        {
            if (found != null || k.Distinct().Count() < 16 || !tried.Add(Convert.ToHexString(k))) return; // a real key is random bytes
            var key = new FAesKey("0x" + Convert.ToHexString(k));
            if (opens(key)) (found, pattern) = (key, how);
        }
        long FileOf(long rva) => sections.FirstOrDefault(s => rva >= s.VirtualAddress && rva < s.VirtualAddress + s.SizeOfRawData) is { SizeOfRawData: > 0 } s ? s.PointerToRawData + rva - s.VirtualAddress : -1;
        byte[] At(long off) { var b = new byte[16]; var p = f.Position; f.Position = off; f.ReadAtLeast(b, 16, false); f.Position = p; return b; }

        const int Step = 4 << 20;
        var buf = new byte[Step + 64]; // + room for the longest pattern at the chunk's last position
        foreach (var s in code)
        {
            var stores = new List<(long Pos, int Size, int Base, int Disp, object Val)>(); // Val: bytes, or the file offset of 16 bytes
            var regs = new (long Pos, byte[]? Val)[16];
            var xmms = new (long Pos, long Src)[16];
            void Store(long pos, int size, int bas, int disp, object val)
            {
                stores.RemoveAll(x => pos - x.Pos > 160);
                stores.Add((pos, size, bas, disp, val));
                var mine = stores.Where(x => x.Size == size && x.Base == bas).GroupBy(x => x.Disp).ToDictionary(g => g.Key, g => g.Last().Val);
                var n = 32 / size;
                foreach (var d0 in mine.Keys.Where(d => d <= disp && disp < d + 32))
                    if (Enumerable.Range(0, n).All(k => mine.ContainsKey(d0 + k * size)))
                        Try(Enumerable.Range(0, n).SelectMany(k => mine[d0 + k * size] is byte[] b ? b : At((long)mine[d0 + k * size])).ToArray(),
                            size == 4 ? "8 x mov dword, imm32" : size == 8 ? "4 x mov qword, imm64" : "2 x 16 bytes from .rdata");
            }
            for (long at = s.PointerToRawData, end = s.PointerToRawData + s.SizeOfRawData; at < end && found == null; at += Step)
            {
                f.Position = at;
                var len = f.ReadAtLeast(buf, buf.Length, false);
                var last = (int)Math.Min(Step, end - 1 - at); // chunk positions 1..Step; the next chunk starts at Step
                for (var i = 1; i <= last && i + 16 < len && found == null; i++)
                {
                    long pos = at + i;
                    var b0 = buf[i];
                    if (b0 == 0xC7) // mov dword [base+disp], imm32 (optional REX.B, no REX.W/X)
                    {
                        var rex = (buf[i - 1] & 0xF0) == 0x40 ? buf[i - 1] : 0;
                        // a 0x40-0x4F before it may be the last byte of the previous store's imm32, not a REX prefix: try both
                        int[] prefixes = rex == 0 ? [0] : [rex, 0];
                        foreach (var r in prefixes)
                            if ((r & 0x0A) == 0 && ((buf[i + 1] >> 3) & 7) == 0 && Mem(buf, i + 1, r & 1, out var bas, out var disp) is > 0 and var ml)
                                Store(pos, 4, bas, disp, buf[(i + 1 + ml)..(i + 5 + ml)]);
                    }
                    else if (b0 is 0x48 or 0x49 && buf[i + 1] is >= 0xB8 and <= 0xBF) // mov r64, imm64
                        regs[(buf[i + 1] - 0xB8) | ((b0 & 1) << 3)] = (pos, buf[(i + 2)..(i + 10)]);
                    else if (b0 is 0x48 or 0x49 or 0x4C or 0x4D && buf[i + 1] == 0x89) // mov qword [base+disp], r64
                    {
                        var src = ((buf[i + 2] >> 3) & 7) | ((b0 & 4) << 1);
                        if (regs[src].Val is { } v && pos - regs[src].Pos < 48 && Mem(buf, i + 2, b0 & 1, out var bas, out var disp) > 0) Store(pos, 8, bas, disp, v);
                    }
                    else if (b0 == 0x0F || (b0 is 0x66 or 0xF3 && (buf[i + 1] == 0x0F || ((buf[i + 1] & 0xF0) == 0x40 && buf[i + 2] == 0x0F))) || ((b0 & 0xF0) == 0x40 && buf[i + 1] == 0x0F))
                    {
                        var j = i + (b0 is 0x66 or 0xF3 ? 1 : 0);
                        var rex = (buf[j] & 0xF0) == 0x40 ? buf[j++] : 0;
                        if (buf[j] != 0x0F) continue;
                        var (op, modrm) = (buf[j + 1], buf[j + 2]);
                        var x = ((modrm >> 3) & 7) | ((rex & 4) << 1);
                        if (op is 0x10 or 0x28 or 0x6F && (modrm & 0xC7) == 0x05) // movups/movaps/movdqu/movdqa xmm, [rip+rel32]
                            xmms[x] = (pos, FileOf(s.VirtualAddress + (pos - s.PointerToRawData) + (j + 7 - i) + BitConverter.ToInt32(buf, j + 3)));
                        else if (op is 0x11 or 0x29 or 0x7F && (modrm & 0xC0) != 0xC0 && xmms[x].Src > 0 && pos - xmms[x].Pos < 64
                                 && Mem(buf, j + 2, rex & 1, out var bas, out var disp) > 0)
                            Store(pos, 16, bas, disp, xmms[x].Src);
                    }
                }
            }
            if (found != null) break;
        }
        var textSize = sections.Where(s => s.Name == ".text").Sum(s => (long)s.SizeOfRawData);
        how = found != null ? $"found in the exe: {pattern}"
            : textSize < code.Sum(s => (long)s.SizeOfRawData) / 2 ? $"not found: the exe looks protected (code in {string.Join(", ", code.Select(s => s.Name))}, .text {textSize >> 20} MB), a static scan can't see the key"
            : $"not found in the exe's code ({tried.Count} candidates)";
        return found;
    }

    /// <summary>A memory operand at modrm position p without an index register: base register (with REX.B) and displacement;
    /// returns its length, 0 if it isn't one (register operand, rip-relative, indexed).</summary>
    static int Mem(byte[] b, int p, int rexB, out int bas, out int disp)
    {
        bas = disp = 0;
        int mod = b[p] >> 6, rm = b[p] & 7, q = p + 1;
        if (mod == 3) return 0;
        if (rm == 4) { var sib = b[q++]; if (((sib >> 3) & 7) != 4) return 0; rm = sib & 7; if (mod == 0 && rm == 5) return 0; }
        else if (mod == 0 && rm == 5) return 0;
        bas = rm | (rexB << 3);
        if (mod == 1) disp = (sbyte)b[q++];
        else if (mod == 2) { disp = BitConverter.ToInt32(b, q); q += 4; }
        return q - p;
    }
}

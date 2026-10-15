using Microsoft.VisualStudio.TestTools.UnitTesting;
using SCSKiller.Linux.Core.Games;

namespace SCSKiller.Linux.Tests;

/// <summary>Lutris Epic Games Store layout: `directory` is the launcher's Wine prefix, the game's folder comes from the
/// Epic launcher manifest inside it. Built on a fake prefix in a temp folder.</summary>
[TestClass]
public class LutrisEpicTests
{
    static string MakePrefix(string manifestJson, bool createGameFolder = true)
    {
        string prefix = Path.Combine(Path.GetTempPath(), "shaderless-test-" + Guid.NewGuid().ToString("N"));
        string manifests = Path.Combine(prefix, "drive_c", "ProgramData", "Epic", "EpicGamesLauncher", "Data", "Manifests");
        Directory.CreateDirectory(manifests);
        File.WriteAllText(Path.Combine(manifests, "ABC.item"), manifestJson);
        if (createGameFolder)
        {
            string exeDir = Path.Combine(prefix, "drive_c", "Program Files", "Epic Games", "Game1", "Game", "Binaries", "Win64");
            Directory.CreateDirectory(exeDir);
            File.WriteAllText(Path.Combine(exeDir, "Game.exe"), "x");
        }
        return prefix;
    }

    const string Manifest = """
        { "AppName": "abc123", "InstallLocation": "C:\\Program Files\\Epic Games\\Game1",
          "LaunchExecutable": "Game/Binaries/Win64/Game.exe", "bIsIncompleteInstall": false }
        """;

    [TestMethod]
    public void Resolves_Game_Folder_And_Exe_From_Manifest()
    {
        string prefix = MakePrefix(Manifest);
        try
        {
            var r = LutrisLinuxSource.ResolveEpicGame(prefix, "abc123");
            Assert.IsNotNull(r);
            Assert.AreEqual(Path.Combine(prefix, "drive_c", "Program Files", "Epic Games", "Game1"), r.Value.InstallPath);
            Assert.AreEqual(Path.Combine(r.Value.InstallPath, "Game", "Binaries", "Win64", "Game.exe"), r.Value.Exe);
        }
        finally { Directory.Delete(prefix, true); }
    }

    [TestMethod]
    public void App_Id_Match_Ignores_Case()
    {
        string prefix = MakePrefix(Manifest);
        try { Assert.IsNotNull(LutrisLinuxSource.ResolveEpicGame(prefix, "ABC123")); }
        finally { Directory.Delete(prefix, true); }
    }

    [TestMethod]
    public void Unknown_App_Id_Is_Not_Found()
    {
        string prefix = MakePrefix(Manifest);
        try { Assert.IsNull(LutrisLinuxSource.ResolveEpicGame(prefix, "other")); }
        finally { Directory.Delete(prefix, true); }
    }

    [TestMethod]
    public void Incomplete_Install_Is_Skipped()
    {
        string prefix = MakePrefix(Manifest.Replace("\"bIsIncompleteInstall\": false", "\"bIsIncompleteInstall\": true"));
        try { Assert.IsNull(LutrisLinuxSource.ResolveEpicGame(prefix, "abc123")); }
        finally { Directory.Delete(prefix, true); }
    }

    [TestMethod]
    public void Missing_Game_Folder_Is_Not_Found()
    {
        string prefix = MakePrefix(Manifest, createGameFolder: false);
        try { Assert.IsNull(LutrisLinuxSource.ResolveEpicGame(prefix, "abc123")); }
        finally { Directory.Delete(prefix, true); }
    }

    [TestMethod]
    public void No_Epic_Launcher_In_Prefix_Is_Not_Found()
    {
        string prefix = Path.Combine(Path.GetTempPath(), "shaderless-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(prefix);
        try { Assert.IsNull(LutrisLinuxSource.ResolveEpicGame(prefix, "abc123")); }
        finally { Directory.Delete(prefix, true); }
    }

    [TestMethod]
    public void Unmappable_Windows_Paths_Return_Null()
    {
        Assert.IsNull(LutrisLinuxSource.WindowsPathToLinux("/tmp/none", null));
        Assert.IsNull(LutrisLinuxSource.WindowsPathToLinux("/tmp/none", "not a path"));
        Assert.IsNull(LutrisLinuxSource.WindowsPathToLinux("/tmp/none", "C:\\x"));   // no drive_c there
    }

    [TestMethod]
    public void Game_Section_Reads_Exe_And_Prefix_Only_From_The_Game_Block()
    {
        var cfg = LutrisLinuxSource.ParseGameSection(new[]
        {
            "game:",
            "  exe: /home/u/Games/My Game/Binaries/Win64/My.exe",
            "  prefix: '/home/u/test games'",
            "  args: -x",
            "wine:",
            "  version: proton-cachyos-slr",
            "  exe: should-not-be-read",
        });
        Assert.AreEqual("/home/u/Games/My Game/Binaries/Win64/My.exe", cfg["exe"]);
        Assert.AreEqual("/home/u/test games", cfg["prefix"]);
        Assert.IsFalse(cfg.ContainsKey("version"));
    }

    [TestMethod]
    public void Game_Section_Ignores_Nested_Script_Block()
    {
        var cfg = LutrisLinuxSource.ParseGameSection(new[]
        {
            "game:", "  exe: a.exe", "script:", "  game:", "    exe: nested.exe",
        });
        Assert.AreEqual("a.exe", cfg["exe"]);
    }

    [TestMethod]
    public void Unreal_Install_Root_Is_The_Folder_With_Engine()
    {
        string root = Path.Combine(Path.GetTempPath(), "shaderless-test-" + Guid.NewGuid().ToString("N"));
        string exeDir = Path.Combine(root, "Game", "Binaries", "Win64");
        Directory.CreateDirectory(exeDir);
        Directory.CreateDirectory(Path.Combine(root, "Engine"));
        try
        {
            Assert.AreEqual(root, LutrisLinuxSource.InferInstallRoot(Path.Combine(exeDir, "Game.exe")));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void Other_Games_Use_The_Exe_Folder_As_Install_Root()
    {
        string dir = Path.Combine(Path.GetTempPath(), "shaderless-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { Assert.AreEqual(dir, LutrisLinuxSource.InferInstallRoot(Path.Combine(dir, "x.exe"))); }
        finally { Directory.Delete(dir, true); }
    }
}

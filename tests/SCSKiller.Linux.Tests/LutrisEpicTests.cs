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
}

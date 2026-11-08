// The game this trainer is for. Metro 2033 Redux and Metro Last Light Redux ship the same metro.exe
// since the November 2026 update, so the trainer tells them apart by the folder the game runs from.
using System;
using System.IO;
using System.Text.RegularExpressions;

static class Product
{
    public const string Game = "Metro Last Light Redux", Header = "METRO LAST LIGHT REDUX", Id = "MetroLastLightReduxTrainer";
    public const string SteamAppId = "287390", OtherSteamAppId = "286690";
    public const string Version = "0.1", AssemblyVersion = "0.1.0.0";
    public const string AppName = Game + " Trainer";

    // A video only Last Light has, for copies that are not in a Steam library with a manifest.
    const string LastLightMarker = "vision1.ogv";

    public static string InstallFolderOf(string manifest)
    {
        Match folder = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"(.+?)\"");
        return folder.Success ? folder.Groups[1].Value : null;
    }

    // The Steam app id of the game in this folder, or null when no manifest names it.
    static string SteamAppOf(string folder)
    {
        // A Steam library sits at <library>\steamapps\common\<game> with a manifest for each game.
        string common = Path.GetDirectoryName(folder);
        string apps = common == null ? null : Path.GetDirectoryName(common);
        if (apps == null || !Directory.Exists(apps)) return null;
        foreach (string manifest in Directory.GetFiles(apps, "appmanifest_*.acf"))
            if (string.Equals(InstallFolderOf(manifest), Path.GetFileName(folder), StringComparison.OrdinalIgnoreCase))
                return Path.GetFileNameWithoutExtension(manifest).Substring("appmanifest_".Length);
        return null;
    }

    // True when the folder holds the other Redux game, Metro 2033 Redux.
    public static bool IsOtherGame(string folder)
    {
        try
        {
            folder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
            string app = SteamAppOf(folder);
            if (app != null) return app == OtherSteamAppId;
            return !File.Exists(Path.Combine(folder, LastLightMarker));
        }
        catch (Exception) { return false; }
    }

    // Texts write the game name as {game} and the trainer version as {version}.
    public static string Fill(string text) { return text.Replace("{game}", Game).Replace("{version}", Version); }
}

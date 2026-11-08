// Per-user setup program for the trainer. The trainer is embedded as a resource.
// Switches: /silent, /desktop, /dir <folder>, /uninstall. Uninstall.exe in the install folder is a copy of this program.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle(Product.AppName + " Setup")]
[assembly: AssemblyProduct(Product.AppName)]
[assembly: AssemblyCompany("dollza")]
[assembly: AssemblyVersion(Product.AssemblyVersion)]
[assembly: AssemblyFileVersion(Product.AssemblyVersion)]

class TrainerBusyException : Exception { }

static class Installer
{
    public const string AppName = Product.AppName, Version = Product.Version, Author = "dollza";
    const string ExeName = Product.Id + ".exe", UninstallName = "Uninstall.exe", Payload = "trainer.exe";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Product.Id;
    const int TrainerCloseTimeoutMs = 5000;

    static string StartMenuLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"); } }
    static string DesktopLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"); } }

    // Where an earlier setup put the trainer, or null.
    public static string InstalledDir
    {
        get
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(UninstallKey))
                return key == null ? null : key.GetValue("InstallLocation") as string;
        }
    }

    public static string DefaultDir
    {
        get { return InstalledDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName); }
    }

    public static string TrainerPath(string dir) { return Path.Combine(dir, ExeName); }

    // Uninstall.exe is a copy of setup, so a double click with no switches must still uninstall.
    public static bool RunningAsUninstaller
    {
        get { return string.Equals(Path.GetFileName(Application.ExecutablePath), UninstallName, StringComparison.OrdinalIgnoreCase); }
    }

    // Like most installers, keeps the trainer in a folder of its own inside whatever was picked.
    public static string NormalizeDir(string picked)
    {
        string full = Path.GetFullPath(picked).TrimEnd(Path.DirectorySeparatorChar);
        if (full.EndsWith(":")) full += Path.DirectorySeparatorChar;
        return string.Equals(Path.GetFileName(full), AppName, StringComparison.OrdinalIgnoreCase) ? full : Path.Combine(full, AppName);
    }

    static bool SameDir(string a, string b)
    {
        return string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    // The trainer undoes its changes to the game when it closes, so it is asked to close, never killed.
    static void CloseRunningTrainer()
    {
        foreach (Process trainer in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName)))
        {
            trainer.CloseMainWindow();
            if (!trainer.WaitForExit(TrainerCloseTimeoutMs)) throw new TrainerBusyException();
        }
    }

    static void CreateShortcut(string link, string target)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        dynamic shortcut = shell.CreateShortcut(link);
        shortcut.TargetPath = target;
        shortcut.WorkingDirectory = Path.GetDirectoryName(target);
        shortcut.IconLocation = target + ",0";
        shortcut.Save();
    }

    // Only files this program created are removed, the folder goes only if nothing else is in it.
    // A folder someone already deleted is fine, and one still in use (an open Explorer window,
    // say) stays behind empty rather than stopping setup or uninstall halfway.
    static void RemoveFiles(string dir)
    {
        if (!Directory.Exists(dir)) return;
        File.Delete(TrainerPath(dir));
        File.Delete(Path.Combine(dir, UninstallName));
        try
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void Install(string dir, bool desktopShortcut)
    {
        CloseRunningTrainer();
        string previous = InstalledDir;
        if (previous != null && !SameDir(previous, dir)) RemoveFiles(previous);

        string trainerPath = TrainerPath(dir), uninstaller = Path.Combine(dir, UninstallName);
        Directory.CreateDirectory(dir);
        using (Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream(Payload))
        using (FileStream target = File.Create(trainerPath))
            source.CopyTo(target);
        if (!string.Equals(Application.ExecutablePath, uninstaller, StringComparison.OrdinalIgnoreCase))
            File.Copy(Application.ExecutablePath, uninstaller, true);

        CreateShortcut(StartMenuLink, trainerPath);
        if (desktopShortcut) CreateShortcut(DesktopLink, trainerPath);
        else File.Delete(DesktopLink);

        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", Version);
            key.SetValue("Publisher", Author);
            key.SetValue("DisplayIcon", trainerPath);
            key.SetValue("InstallLocation", dir);
            key.SetValue("UninstallString", "\"" + uninstaller + "\" /uninstall");
            key.SetValue("QuietUninstallString", "\"" + uninstaller + "\" /uninstall /silent");
            key.SetValue("EstimatedSize", (int)((new FileInfo(trainerPath).Length + new FileInfo(uninstaller).Length) / 1024), RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
    }

    // A running exe cannot delete itself, so Uninstall.exe hands over to a copy in the temp folder.
    public static bool HandOverToTempCopy(bool silent)
    {
        if (!RunningAsUninstaller) return false;
        string copy = Path.Combine(Path.GetTempPath(), Product.Id + "Uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(Application.ExecutablePath, copy);
        string arguments = "/uninstall /temp-copy " + Process.GetCurrentProcess().Id + " /dir \"" + Path.GetDirectoryName(Application.ExecutablePath) + "\"" + (silent ? " /silent" : "");
        // The copy must not inherit the install folder as its working folder, or Windows keeps that folder in use.
        Process.Start(new ProcessStartInfo(copy, arguments) { UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        return true;
    }

    public static void WaitForParent(int pid)
    {
        try { Process.GetProcessById(pid).WaitForExit(TrainerCloseTimeoutMs); }
        catch (ArgumentException) { }
    }

    public static void Uninstall(string dir)
    {
        CloseRunningTrainer();
        File.Delete(StartMenuLink);
        File.Delete(DesktopLink);
        if (dir != null) RemoveFiles(dir);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        Registry.CurrentUser.DeleteSubKeyTree(Preferences.KeyPath, false);
    }

    public static void DeleteSelfAfterExit()
    {
        string command = "/c ping -n 3 127.0.0.1 >nul & del /f /q \"" + Application.ExecutablePath + "\"";
        Process.Start(new ProcessStartInfo("cmd.exe", command) { CreateNoWindow = true, UseShellExecute = false });
    }
}

enum GameCheck { NotChosen, Steam, NoGameFile, NotSteam, WrongGame }

// Tells the user whether a folder holds a copy the trainer can work with.
// The trainer itself finds the running game on any disk, it never needs this path.
static class GameLocator
{
    const string SteamAppId = Product.SteamAppId, GameExe = "metro.exe", SteamApi = "steam_api64.dll";
    const int HeaderSize = 0x1000, SectionSize = 40;

    static string InstallFolderOf(string manifest) { return Product.InstallFolderOf(manifest); }

    public static string Find()
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
        {
            string steam = key == null ? null : key.GetValue("SteamPath") as string;
            if (steam == null) return null;
            string libraries = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraries)) return null;
            foreach (Match match in Regex.Matches(File.ReadAllText(libraries), "\"path\"\\s+\"(.+?)\""))
            {
                string apps = Path.Combine(match.Groups[1].Value.Replace(@"\\", @"\"), "steamapps");
                string manifest = Path.Combine(apps, "appmanifest_" + SteamAppId + ".acf");
                if (!File.Exists(manifest)) continue;
                string folder = InstallFolderOf(manifest);
                if (folder == null) continue;
                string game = Path.GetFullPath(Path.Combine(apps, "common", folder));
                if (File.Exists(Path.Combine(game, GameExe))) return game;
            }
        }
        return null;
    }

    // Steam wraps its games in a stub that adds a .bind section to the exe.
    static bool HasSteamStub(string exe)
    {
        byte[] header = new byte[HeaderSize];
        using (FileStream file = File.OpenRead(exe))
            file.Read(header, 0, HeaderSize);
        int pe = BitConverter.ToInt32(header, 0x3C);
        if (pe <= 0 || pe > HeaderSize - 64) return false;
        int count = BitConverter.ToUInt16(header, pe + 6);
        int first = pe + 24 + BitConverter.ToUInt16(header, pe + 20);
        for (int i = 0; i < count && first + (i + 1) * SectionSize <= HeaderSize; i++)
            if (Encoding.ASCII.GetString(header, first + i * SectionSize, 8).TrimEnd('\0') == ".bind") return true;
        return false;
    }

    public static GameCheck Classify(string folder)
    {
        if (folder == null) return GameCheck.NotChosen;
        string exe = Path.Combine(folder, GameExe);
        if (!File.Exists(exe)) return GameCheck.NoGameFile;
        // A Steam library sits at <library>\steamapps\common\<game> with a manifest for each game.
        string common = Path.GetDirectoryName(folder);
        string apps = common == null ? null : Path.GetDirectoryName(common);
        if (apps != null && Directory.Exists(apps))
            foreach (string manifest in Directory.GetFiles(apps, "appmanifest_*.acf"))
                if (string.Equals(InstallFolderOf(manifest), Path.GetFileName(folder), StringComparison.OrdinalIgnoreCase))
                    return Path.GetFileName(manifest) == "appmanifest_" + SteamAppId + ".acf" ? GameCheck.Steam : GameCheck.WrongGame;
        if (Product.IsOtherGame(folder)) return GameCheck.WrongGame;
        return HasSteamStub(exe) && File.Exists(Path.Combine(folder, SteamApi)) ? GameCheck.Steam : GameCheck.NotSteam;
    }
}

class SetupForm : Form
{
    readonly ToggleRow desktopRow = new ToggleRow { On = true };
    readonly ToggleRow launchRow = new ToggleRow { On = true };
    readonly FlatButton installButton = new FlatButton(true);
    readonly FlatButton closeButton = new FlatButton(false);
    readonly FlatButton browseInstall = new FlatButton(false) { Normal = Theme.Track, BackColor = Theme.Card };
    readonly FlatButton browseGame = new FlatButton(false) { Normal = Theme.Track, BackColor = Theme.Card };
    readonly LangBar langBar = new LangBar();
    string installDir = Installer.DefaultDir;
    string gamePath = GameLocator.Find();
    GameCheck gameCheck;
    string resultKey;
    object[] resultArgs = new object[0];
    bool installed;

    public SetupForm()
    {
        BackColor = Theme.Back;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        DoubleBuffered = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(Theme.S(600), Theme.S(452));
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch (Exception) { }
        gameCheck = GameLocator.Classify(gamePath);

        langBar.SetBounds(Theme.S(400), Theme.S(18), Theme.S(180), Theme.S(28));
        langBar.Picked += delegate { ApplyLanguage(); };
        browseInstall.SetBounds(Theme.S(474), Theme.S(98), Theme.S(90), Theme.S(30));
        browseInstall.Click += delegate { PickInstallDir(); };
        browseGame.SetBounds(Theme.S(474), Theme.S(158), Theme.S(90), Theme.S(30));
        browseGame.Click += delegate { PickGameDir(); };
        desktopRow.SetBounds(Theme.S(20), Theme.S(292), Theme.S(560), Theme.S(40));
        desktopRow.BackColor = Theme.Back;
        desktopRow.SnapAnimation();
        launchRow.SetBounds(Theme.S(20), Theme.S(338), Theme.S(560), Theme.S(40));
        launchRow.BackColor = Theme.Back;
        launchRow.SnapAnimation();
        closeButton.SetBounds(Theme.S(350), Theme.S(398), Theme.S(110), Theme.S(36));
        closeButton.Click += delegate { Close(); };
        installButton.SetBounds(Theme.S(470), Theme.S(398), Theme.S(110), Theme.S(36));
        installButton.Click += delegate { RunInstall(); };
        Controls.AddRange(new Control[] { langBar, browseInstall, browseGame, desktopRow, launchRow, closeButton, installButton });
        ApplyLanguage();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.UseDarkTitleBar(Handle);
    }

    void ApplyLanguage()
    {
        Text = Loc.T("setup.window");
        desktopRow.SetText(Loc.T("setup.desktop"), null, null);
        launchRow.SetText(Loc.T("setup.launch"), null, null);
        installButton.Text = Loc.T("setup.install");
        closeButton.Text = Loc.T("setup.close");
        browseInstall.Text = browseGame.Text = Loc.T("setup.browse");
        foreach (FlatButton button in new[] { installButton, closeButton, browseInstall, browseGame }) button.Invalidate();
        Invalidate();
    }

    string PickFolder(string description, string start)
    {
        using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = description, ShowNewFolderButton = true })
        {
            if (start != null && Directory.Exists(start)) dialog.SelectedPath = start;
            return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
        }
    }

    void PickInstallDir()
    {
        string picked = PickFolder(Loc.T("setup.pick.install"), Path.GetDirectoryName(installDir));
        if (picked == null) return;
        installDir = Installer.NormalizeDir(picked);
        Invalidate();
    }

    void PickGameDir()
    {
        string picked = PickFolder(Loc.T("setup.pick.game"), gamePath);
        if (picked == null) return;
        gamePath = picked;
        gameCheck = GameLocator.Classify(gamePath);
        Invalidate();
    }

    void ShowResult(string key, params object[] args)
    {
        resultKey = key;
        resultArgs = args;
        Invalidate();
    }

    void RunInstall()
    {
        try
        {
            Installer.Install(installDir, desktopRow.On);
            installed = true;
            installButton.Enabled = false;
            ShowResult("setup.done");
            if (launchRow.On)
            {
                Process.Start(Installer.TrainerPath(installDir));
                Close();
            }
        }
        catch (TrainerBusyException) { ShowResult("setup.busy"); }
        catch (Exception e) { ShowResult("setup.failed", e.Message); }
    }

    static string GameCheckKey(GameCheck check)
    {
        switch (check)
        {
            case GameCheck.Steam: return "setup.game.ok";
            case GameCheck.NoGameFile: return "setup.game.nofile";
            case GameCheck.NotSteam: return "setup.game.notsteam";
            case GameCheck.WrongGame: return "setup.game.wrong";
            default: return "setup.game.missing";
        }
    }

    void DrawPath(Graphics g, string caption, string path, int x, int y, int width)
    {
        TextRenderer.DrawText(g, caption, Theme.SectionFont, new Point(x, y), Theme.Accent, Theme.Card, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, path, Theme.LabelFont, new Rectangle(x, y + Theme.S(18), width, Theme.S(20)), Theme.Text, Theme.Card, TextFormatFlags.NoPadding | TextFormatFlags.PathEllipsis);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.DrawHeader(g, ClientSize.Width, Theme.S(72), Product.Header, Loc.T("setup.subtitle"));

        Rectangle card = new Rectangle(Theme.S(20), Theme.S(78), Theme.S(560), Theme.S(202));
        Theme.Fill(g, Theme.Card, card, Theme.S(10));
        int inner = card.X + Theme.S(16), width = Theme.S(426);
        DrawPath(g, Loc.T("setup.folder"), installDir, inner, Theme.S(92), width);
        DrawPath(g, Loc.T("setup.gamefolder"), gamePath ?? "—", inner, Theme.S(152), width);

        using (SolidBrush dot = new SolidBrush(gameCheck == GameCheck.Steam ? Theme.Good : Theme.Accent))
            g.FillEllipse(dot, inner, Theme.S(212), Theme.S(9), Theme.S(9));
        TextRenderer.DrawText(g, Loc.T(GameCheckKey(gameCheck)), Theme.LabelFont, new Rectangle(inner + Theme.S(16), Theme.S(208), card.Width - Theme.S(48), Theme.S(36)), Theme.Text, Theme.Card, TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, Loc.T("setup.steamonly"), Theme.SectionFont, new Point(inner, Theme.S(250)), Theme.Accent, Theme.Card, TextFormatFlags.NoPadding);

        if (resultKey != null)
        {
            Color color = installed ? Theme.Good : Theme.Accent;
            TextRenderer.DrawText(g, string.Format(Loc.T(resultKey), resultArgs), Theme.LabelFont, new Rectangle(Theme.S(20), Theme.S(398), Theme.S(320), Theme.S(36)), color, Theme.Back, TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter);
        }
    }

    // Renders the window, for checking the layout.
    public void SavePreview(string path, string game)
    {
        if (game != null)
        {
            gamePath = game;
            gameCheck = GameLocator.Classify(game);
        }
        using (Bitmap bitmap = new Bitmap(Width, Height))
        {
            DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
            bitmap.Save(path, ImageFormat.Png);
        }
    }
}

static class Program
{
    static string ArgumentAfter(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    static int RunUninstall(string[] args, bool silent)
    {
        Environment.CurrentDirectory = Path.GetTempPath();
        if (Installer.HandOverToTempCopy(silent)) return 0;
        string parent = ArgumentAfter(args, "/temp-copy");
        if (parent != null) Installer.WaitForParent(int.Parse(parent));
        if (!silent && MessageBox.Show(Loc.T("uninstall.ask"), Installer.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            if (parent != null) Installer.DeleteSelfAfterExit();
            return 1;
        }
        try
        {
            Installer.Uninstall(ArgumentAfter(args, "/dir") ?? Installer.InstalledDir);
            if (!silent) MessageBox.Show(Loc.T("uninstall.done"), Installer.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (TrainerBusyException)
        {
            if (!silent) MessageBox.Show(Loc.T("setup.busy"), Installer.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
        catch (Exception e)
        {
            if (!silent) MessageBox.Show(string.Format(Loc.T("uninstall.failed"), e.Message), Installer.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            if (parent != null) Installer.DeleteSelfAfterExit();
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        bool silent = args.Contains("/silent");
        Loc.Initialize();
        if (args.Length >= 3 && args[0] == "--classify")
        {
            File.WriteAllText(args[2], GameLocator.Classify(args[1]).ToString());
            return 0;
        }
        if (args.Contains("/uninstall") || Installer.RunningAsUninstaller) return RunUninstall(args, silent);
        if (silent)
        {
            string dir = ArgumentAfter(args, "/dir");
            try { Installer.Install(dir == null ? Installer.DefaultDir : Installer.NormalizeDir(dir), args.Contains("/desktop")); return 0; }
            catch (Exception) { return 1; }
        }

        if (args.Length >= 3 && args[0] == "--preview") Loc.Current = Array.IndexOf(Loc.Codes, args[2]);
        Theme.Initialize();
        Application.EnableVisualStyles();
        if (args.Length >= 3 && args[0] == "--preview")
        {
            SetupForm preview = new SetupForm { StartPosition = FormStartPosition.Manual, Location = new Point(-6000, 0) };
            preview.Show();
            Application.DoEvents();
            preview.SavePreview(args[1], args.Length >= 4 ? args[3] : null);
            preview.Close();
            return 0;
        }
        Application.Run(new SetupForm());
        return 0;
    }
}

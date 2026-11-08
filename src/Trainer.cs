// Trainer for the Steam version of Metro Last Light Redux (metro.exe of the November 2026 update).
// Uses the engine's own debug flags and a few small code patches that are undone on exit.
// It never touches the network. The only thing it stores is the language and sound choice in HKCU.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle(Product.AppName)]
[assembly: AssemblyProduct(Product.AppName)]
[assembly: AssemblyCompany("dollza")]
[assembly: AssemblyVersion(Product.AssemblyVersion)]
[assembly: AssemblyFileVersion(Product.AssemblyVersion)]

static class Native
{
    public const int PROCESS_VM_READ = 0x10, PROCESS_VM_WRITE = 0x20, PROCESS_VM_OPERATION = 0x08, PROCESS_QUERY_INFORMATION = 0x400, PROCESS_SUSPEND_RESUME = 0x800;
    public const uint PAGE_EXECUTE_READWRITE = 0x40, MEM_COMMIT_RESERVE = 0x3000;
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool VirtualProtectEx(IntPtr h, IntPtr addr, IntPtr size, uint protect, out uint old);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool FlushInstructionCache(IntPtr h, IntPtr addr, IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, IntPtr size, uint type, uint protect);
    [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr h);
    [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr wnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr wnd, out int pid);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetKeyNameText(int lParam, System.Text.StringBuilder name, int size);
}

class Patch
{
    public readonly int[] Signature, PatchedSignature;
    public readonly int Offset;
    public readonly byte[] Bytes, Original;
    public long Rva = -1;

    public Patch(string signature, int offset, string bytes)
    {
        Signature = Sig.Parse(signature);
        Offset = offset;
        Bytes = bytes.Split(' ').Select(x => Convert.ToByte(x, 16)).ToArray();
        // The bytes under a patch are never wildcards, so the signature itself holds the original code.
        Original = Signature.Skip(offset).Take(Bytes.Length).Select(x => (byte)x).ToArray();
        PatchedSignature = (int[])Signature.Clone();
        for (int i = 0; i < Bytes.Length; i++) PatchedSignature[offset + i] = Bytes[i];
    }
}

enum HookCave { NoReload, NoSpread, RapidFire, FilterDrain }

// A jump from the game's code into a cave of the trainer's. As with a patch, the signature holds the
// original bytes, and a jump left by a trainer that was killed is recognised so it can be undone.
class CodeHook
{
    public readonly int[] Signature, HookedSignature;
    public readonly int Offset, Length;
    public readonly byte[] Original;
    public readonly HookCave Cave;
    public long Rva = -1;
    public bool Hooked;

    public CodeHook(string signature, int offset, int length, HookCave cave)
    {
        Signature = Sig.Parse(signature);
        Offset = offset;
        Length = length;
        Cave = cave;
        Original = Signature.Skip(offset).Take(length).Select(x => (byte)x).ToArray();
        HookedSignature = (int[])Signature.Clone();
        HookedSignature[offset] = 0xE9;
        for (int i = 1; i < length; i++) HookedSignature[offset + i] = i < 5 ? -1 : 0x90;
    }
}

class Feature
{
    public string Key;
    public int Bit;
    public Patch[] Patches = new Patch[0];
    public CodeHook[] Hooks = new CodeHook[0];
    public bool IsSpeed, IsNoClip, IsGod;
    public bool On, Applied, Available;
    public ToggleRow Row;
}

static class Sig
{
    public static int[] Parse(string text)
    {
        return text.Split(' ').Select(x => x == "??" ? -1 : Convert.ToInt32(x, 16)).ToArray();
    }

    // Every position the signature matches at.
    public static List<int> FindAll(byte[] image, int[] sig)
    {
        List<int> hits = new List<int>();
        int last = image.Length - sig.Length, first = sig[0];
        for (int i = 0; i <= last; i++)
        {
            if (image[i] != first) continue;
            bool match = true;
            for (int j = 1; j < sig.Length && match; j++)
                match = sig[j] < 0 || image[i + j] == sig[j];
            if (match) hits.Add(i);
        }
        return hits;
    }

    // Resolves a global the matched instruction reads. All matches must agree, otherwise -1.
    public static long FindGlobal(byte[] image, int imageRva, string text, int dispOffset, int instructionLength)
    {
        long result = -1;
        foreach (int hit in FindAll(image, Parse(text)))
        {
            long rva = imageRva + hit + instructionLength + BitConverter.ToInt32(image, hit + dispOffset);
            if (result >= 0 && result != rva) return -1;
            result = rva;
        }
        return result;
    }
}

static class Catalog
{
    // GodBit is the engine's global god mode, which covers every character. It is never set, only
    // cleared if an older version of the trainer left it on; god mode now covers the player only.
    public const int GodBit = 0x02, StealthBit = 0x04, AmmoBit = 0x08, ManagedBits = GodBit | StealthBit | AmmoBit;

    // Military-grade ammo is also the money. The engine's ammo flag makes its count read 10999 and
    // skips taking it away, which made the whole shop free. These two functions belong to the money
    // only: the ammo feature masks the flag out of them, the money feature forces the free path.
    // Each signature leaves the other feature's byte as a wildcard, so either can be on alone or both.
    const string MoneyCountMaskSig = "41 57 48 83 EC 40 F6 05 ?? ?? ?? ?? 08 4C 8B F9 ?? ?? B8 F7 2A 00 00";
    const string MoneyCountFreeSig = "41 57 48 83 EC 40 F6 05 ?? ?? ?? ?? ?? 4C 8B F9 74 0C B8 F7 2A 00 00";
    const string MoneyTakeMaskSig = "33 FF F6 05 ?? ?? ?? ?? 08 ?? 68 3B FD 73";
    const string MoneyTakeFreeSig = "33 FF F6 05 ?? ?? ?? ?? ?? 75 68 3B FD 73";

    // Both sit in the shared shot routine, which has the weapon in r14. After a shot it stores the
    // rounds minus the rounds fired (sub edi, r13d); before the bullets it keeps the weapon's
    // current spread for all of them (movaps xmm14, xmm0).
    const string NoReloadSig = "49 8B 06 41 2B FD 8B D7 49 8B CE FF 90 60 13 00 00";
    const string NoSpreadSig = "49 8B 04 24 49 8B CC 44 0F 28 F0 45 0F 57 FF FF 90 08 01 00 00 66 44 0F 6F 25";

    // The weapon update fires while its cooldown (weapon+0x570) is not above zero and adds the shot
    // interval, which a virtual call returns in xmm0, for every shot (addss xmm0, [r14+0x570]).
    const string RapidFireSig = "49 8B CE 49 8B 06 FF 90 18 14 00 00 F3 41 0F 58 86 70 05 00 00 41 0F B7 96 3A 07 00 00";

    // The battery object keeps its charge at +0x480 and loses rate * dt every frame:
    // subss xmm3, xmm1, then clamped to 0..1 and stored. Without the subtraction the charge stays,
    // while the hand charger still adds to it.
    const string BatterySig = "F3 0F 10 B9 80 04 00 00 49 8B F9 0F 28 DF C7 40 20 00 00 80 3F C7 40 C8 00 00 00 00 F3 0F 59 CA F3 0F 5C D9 0F 28 E3";

    // Filter time: subss xmm0, xmm9 takes this frame's use off [rsi+0x44c]; xmm2 is the drain multiplier.
    const string FilterDrainSig = "F3 44 0F 59 CA 44 0F 28 C0 F3 44 0F 5E 86 54 04 00 00 F3 41 0F 5C C1 F3 0F 11 AE B4 04 00 00 41 0F 28 C9 F3 0F 11 45 77 E8 ?? ?? ?? ??";

    // The order is the index MainForm places the rows by.
    public static List<Feature> Build()
    {
        return new List<Feature>
        {
            new Feature { Key = "god", IsGod = true },
            new Feature { Key = "ammo", Bit = AmmoBit, Patches = new[] {
                new Patch(MoneyCountMaskSig, 12, "00"),
                new Patch(MoneyTakeMaskSig, 8, "00") } },
            new Feature { Key = "stealth", Bit = StealthBit },
            new Feature { Key = "medkits", Patches = new[] {
                new Patch("B8 01 00 00 00 D3 E0 F7 D0 41 21 80 78 04 00 00 41 C7 80 84 04 00 00 FF 00 00 00 48 8B 0D ?? ?? ?? ?? 45 33 C0 33 D2", 9, "90 90 90 90 90 90 90") } },
            // The filter time drains by frame time x a multiplier the game normally keeps at 1. Some
            // scenes raise it to empty the filter on purpose and wait for the player to change it, so
            // the cave only skips the drain while the multiplier is normal. The patches keep the spare filters.
            new Feature { Key = "filters",
                Hooks = new[] { new CodeHook(FilterDrainSig, 18, 5, HookCave.FilterDrain) },
                Patches = new[] {
                new Patch("0F 5B C0 F3 0F 5C C6 F3 0F 5D D0 F3 0F 5C CA F3 0F 58 D6 F3 0F 11 8F 80 06 00 00 F3 0F 11 93 54 04 00 00 F3 0F 11 93 4C 04 00 00", 11, "90 90 90 90"),
                new Patch("0F 5B C0 F3 0F 5C C6 F3 0F 5D D0 F3 0F 5C CA F3 0F 58 D6 F3 0F 11 8E 80 06 00 00 F3 0F 11 93 54 04 00 00 F3 0F 11 93 4C 04 00 00", 11, "90 90 90 90") } },
            new Feature { Key = "nomask", Patches = new[] {
                new Patch("CC 8B 05 ?? ?? ?? ?? A8 01 75 07 A8 02 75 03 33 C0 C3", 9, "EB") } },
            // Firearms take their rounds out in one shared shot routine, which is hooked into a cave
            // that skips it for the player's weapon. The patch covers a separate single-round path.
            new Feature { Key = "noreload",
                Hooks = new[] { new CodeHook(NoReloadSig, 0, 6, HookCave.NoReload) },
                Patches = new[] {
                new Patch("48 8B CF FF 93 ?? ?? ?? ?? 48 8B CF 8D 50 FF FF 93 ?? ?? ?? ?? C6 87 10 0C 00 00 01 48 83 BF B8 05 00 00 00 75 32", 14, "00") } },
            new Feature { Key = "air", Patches = new[] {
                new Patch("C3 F3 0F 10 8B 00 0C 00 00 0F 57 C0 F3 0F 5C CE 0F 2F C8 F3 0F 11 8B 00 0C 00 00 73 1A C7 83 00 0C 00 00 00 00 00 00", 12, "90 90 90 90"),
                new Patch("89 83 00 0C 00 00 EB 4E F3 0F 10 83 00 0C 00 00 F3 0F 5C C7 0F 2F C6 F3 0F 11 83 00 0C 00 00 73 16 48 8B 03", 16, "90 90 90 90") } },
            // Last Light has no Volt Driver; the flashlight battery takes its row.
            new Feature { Key = "battery", Patches = new[] { new Patch(BatterySig, 32, "0F 1F 40 00") } },
            new Feature { Key = "freeze", Patches = new[] {
                new Patch("48 8B 89 00 01 00 00 49 8B F8 48 85 C9 0F 84 ?? ?? ?? ?? 48 8B 01 FF 50 18 48 85 C0 0F 84 ?? ?? ?? ??", 13, "90 E9") } },
            new Feature { Key = "speed", IsSpeed = true },
            new Feature { Key = "noclip", IsNoClip = true },
            new Feature { Key = "money", Patches = new[] {
                new Patch(MoneyCountFreeSig, 16, "90 90"),
                new Patch(MoneyTakeFreeSig, 9, "EB") } },
            // Each shot plays a camera track whose strength starts from a 1.0 constant and is scaled by
            // weapon upgrades. Starting it from zero (xorps xmm6, xmm6) leaves the camera still. The
            // hook gives the player's bullets no spread, so they go where the crosshair points.
            new Feature { Key = "norecoil",
                Hooks = new[] { new CodeHook(NoSpreadSig, 7, 8, HookCave.NoSpread) },
                Patches = new[] {
                new Patch("33 ED 0F 29 74 24 50 8B F5 F3 0F 10 35 72 66 BC 00 0F 29 7C 24 40 0F 57 FF", 9, "0F 57 F6 0F 1F 44 00 00") } },
            // The cave shortens the shot interval of the player's weapon, so automatic weapons fire
            // faster and the others take the next shot sooner. Enemies' weapons are left as they are.
            new Feature { Key = "rapid",
                Hooks = new[] { new CodeHook(RapidFireSig, 12, 9, HookCave.RapidFire) } },
        };
    }
}

enum AttachResult { Connected, NotRunning, NoAccess, Waiting, Unsupported, WrongGame }

class Game : IDisposable
{
    const string FlagAmmoSig = "F6 05 ?? ?? ?? ?? 08 75 ?? 8B 81 ?? ?? ?? ?? 85 C0 74 ?? FF C8 89 81";
    const string SpeedSig = "F3 0F 10 05 ?? ?? ?? ?? F3 0F 59 F0 45 85 F6 74 ?? 85 FF B8 D0 09 00 00";
    const float StockSpeedScale = 1.25f;
    const double StartupGraceSeconds = 30;

    // The player's physics move call. While noclip is on it goes to a small cave that moves the
    // capsule directly with the controller's own setter, which skips every collision check.
    // The same cave also records which object is the player, for god mode.
    const string NoClipSiteSig = "41 0F 28 D8 48 89 44 24 30 41 0F 28 D1 48 8D 85 28 01 00 00 48 89 44 24 28 C6 44 24 20 01 E8 ?? ?? ?? ?? 8B 15 ?? ?? ?? ??";
    const string NoClipSetterSig = "40 53 48 83 EC 30 48 8B D9 48 8B 89 C8 00 00 00 48 85 C9 0F 84 ?? ?? ?? ?? F3 0F 10 02 F3 0F 10 4A 04 F3 0F 11 44 24 20 F3 0F 10 42 08";
    const string MoveSig = "48 89 5C 24 10 48 89 74 24 18 57 48 83 EC 70 48 8B 01 48 8B F2 0F 29 74 24 60 48 8B F9 0F 29 7C 24 50 0F 28 F3 0F 28 FA FF 50 10";
    // The damage immunity check that runs for every character. God mode sends it to a cave that
    // answers "immune" for the player and leaves everyone else to the game.
    const string GodCheckSig = "48 83 EC 28 F6 05 ?? ?? ?? ?? 02 74 ?? 85 D2 74 ?? 48 8D 0D";
    const string GodCheckHookedSig = "E9 ?? ?? ?? ?? 90 90 90 90 90 90 74 ?? 85 D2 74 ?? 48 8D 0D";
    // The game object; [game+0x38] is the player, whose weapons link to player+0xA0 through [weapon+0x118].
    const string GameSig = "48 8B 05 ?? ?? ?? ?? 48 8B 48 38 48 85 C9 4C 8D 81 A0 00 00 00";
    const int NoClipCallOffset = 30, GodCheckLength = 11;
    const int CaveSize = 0x1000, CaveEnabled = 0, CaveSpeed = 4, CaveVertical = 8, CaveController = 0x10, CavePlayer = 0x18, CaveGodEnabled = 0x20;
    const int CaveTeleport = 0x24, CaveTarget = 0x28, CaveRapidScale = 0x38, CaveFilterLimit = 0x3C, FootOffset = 0x1A8;
    const int CaveCodeOffset = 0x40, CaveGodOffset = 0x200, CaveNoReloadOffset = 0x300, CaveNoSpreadOffset = 0x380, CaveRapidOffset = 0x400, CaveFilterOffset = 0x480;
    const long NearestCave = 0x100000, FarthestCave = 0x70000000, CaveStep = 0x10000;
    const float NoClipSpeedScale = 2f, NoClipVerticalSpeed = 6f, NoClipBoost = 3f;
    // Half the shot interval: twice the rate of fire.
    const float RapidFireScale = 0.5f;
    // Above this drain multiplier a scene is emptying the filter on purpose (5 where you first change one).
    const float FilterDrainLimit = 2.5f;
    const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_SPACE = 0x20;

    readonly List<Feature> features;
    IntPtr handle;
    Process process;
    long moduleBase, flagsRva = -1, speedRva = -1, noClipSiteRva = -1, moveRva = -1, godCheckRva = -1, godFlagsRva = -1, caveAddress;
    long gameRva = -1;
    bool moveHooked, godHooked;
    // Position save and teleport run through the move cave, so they hook it until they are done.
    bool savePending, saveArmed, teleportPending, teleportArmed, hasSaved;
    byte[] savedFoot = new byte[12];
    float speedDefault, noClipVertical;

    public double SpeedMultiplier = 2.0;
    public bool Attached { get { return process != null && !process.HasExited; } }
    public long FlagsRva { get { return flagsRva; } }
    public long SpeedRva { get { return speedRva; } }

    public Game(List<Feature> features)
    {
        this.features = features;
    }

    static bool IsThisGame(Process p)
    {
        try { return !Product.IsOtherGame(Path.GetDirectoryName(p.MainModule.FileName)); }
        catch (Exception) { return true; }   // unreadable path: the signatures still decide
    }

    public AttachResult TryAttach()
    {
        Detach();
        // Both Redux games run as the same metro.exe, so only the folder it runs from tells them apart.
        Process[] found = Process.GetProcessesByName("metro");
        if (found.Length == 0) return AttachResult.NotRunning;
        Process p = found.FirstOrDefault(IsThisGame);
        if (p == null) return AttachResult.WrongGame;
        IntPtr h = Native.OpenProcess(Native.PROCESS_VM_READ | Native.PROCESS_VM_WRITE | Native.PROCESS_VM_OPERATION | Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_SUSPEND_RESUME, false, p.Id);
        if (h == IntPtr.Zero) return AttachResult.NoAccess;
        try
        {
            bool settled = (DateTime.Now - p.StartTime).TotalSeconds > StartupGraceSeconds;
            long moduleAddress = p.MainModule.BaseAddress.ToInt64();
            int textRva, textSize;
            ReadTextRange(h, moduleAddress, out textRva, out textSize);
            byte[] image = ReadRange(h, moduleAddress + textRva, textSize);

            long ammo = Sig.FindGlobal(image, textRva, FlagAmmoSig, 2, 7);
            long speed = Sig.FindGlobal(image, textRva, SpeedSig, 4, 8);
            bool hooksOk = ResolveHooks(image, textRva);
            gameRva = Sig.FindGlobal(image, textRva, GameSig, 3, 7);
            // Two independent reads of the flag word must agree, unless the god check is already hooked.
            bool flagsOk = ammo >= 0 && (godFlagsRva < 0 || godFlagsRva == ammo);

            int resolved = 0;
            foreach (Feature f in features)
            {
                f.Applied = false;
                if (f.Bit != 0) f.Available = flagsOk && ResolvePatches(f, image, textRva);
                else if (f.IsSpeed) f.Available = speed >= 0;
                else if (f.IsNoClip) { f.Available = hooksOk; f.Applied = moveHooked; }
                else if (f.IsGod) { f.Available = hooksOk && flagsOk; f.Applied = godHooked; }
                else f.Available = ResolvePatches(f, image, textRva);
                if (f.Hooks.Length > 0) f.Available &= gameRva >= 0 && ResolveCodeHooks(f, image, textRva);
                if (f.Available) resolved++;
            }
            // Steam decrypts the code a moment after launch, so a miss may only mean "too early".
            if (resolved == 0 || (resolved < features.Count && !settled))
            {
                Native.CloseHandle(h);
                return resolved == 0 && settled ? AttachResult.Unsupported : AttachResult.Waiting;
            }

            handle = h;
            process = p;
            moduleBase = moduleAddress;
            flagsRva = flagsOk ? ammo : -1;
            speedRva = speed;
            // A larger value can only be left over from a trainer that was killed.
            if (speedRva >= 0) speedDefault = Math.Min(ReadFloat(speedRva), StockSpeedScale);
            return AttachResult.Connected;
        }
        catch (Exception)
        {
            Native.CloseHandle(h);
            return AttachResult.Waiting;
        }
    }

    // Code still patched by a trainer that was killed is recognised too, so it can be restored.
    static bool ResolvePatches(Feature f, byte[] image, int textRva)
    {
        foreach (Patch patch in f.Patches)
        {
            List<int> hits = Sig.FindAll(image, patch.Signature);
            if (hits.Count == 0)
            {
                hits = Sig.FindAll(image, patch.PatchedSignature);
                f.Applied |= hits.Count == 1;
            }
            if (hits.Count != 1) return false;
            patch.Rva = textRva + hits[0] + patch.Offset;
        }
        return true;
    }

    // Hooks still in place from a trainer that was killed are recognised, so Sync can undo them.
    bool ResolveHooks(byte[] image, int textRva)
    {
        List<int> sites = Sig.FindAll(image, Sig.Parse(NoClipSiteSig)), moves = Sig.FindAll(image, Sig.Parse(MoveSig));
        List<int> checks = Sig.FindAll(image, Sig.Parse(GodCheckSig)), hooked = Sig.FindAll(image, Sig.Parse(GodCheckHookedSig));
        if (sites.Count != 1 || moves.Count != 1 || checks.Count + hooked.Count != 1) return false;
        if (Sig.FindAll(image, Sig.Parse(NoClipSetterSig)).Count != 1) return false;
        noClipSiteRva = textRva + sites[0] + NoClipCallOffset;
        moveRva = textRva + moves[0];
        moveHooked = noClipSiteRva + 5 + BitConverter.ToInt32(image, sites[0] + NoClipCallOffset + 1) != moveRva;
        godHooked = hooked.Count == 1;
        int check = godHooked ? hooked[0] : checks[0];
        godCheckRva = textRva + check;
        if (!godHooked) godFlagsRva = godCheckRva + 4 + 7 + BitConverter.ToInt32(image, check + 6);
        return true;
    }

    static bool ResolveCodeHooks(Feature f, byte[] image, int textRva)
    {
        foreach (CodeHook hook in f.Hooks)
        {
            List<int> sites = Sig.FindAll(image, hook.Signature), hooked = Sig.FindAll(image, hook.HookedSignature);
            if (sites.Count + hooked.Count != 1) return false;
            hook.Hooked = hooked.Count == 1;
            hook.Rva = textRva + (hook.Hooked ? hooked[0] : sites[0]) + hook.Offset;
            f.Applied |= hook.Hooked;
        }
        return true;
    }

    static void ReadTextRange(IntPtr h, long module, out int rva, out int size)
    {
        byte[] header = ReadRange(h, module, 0x1000);
        int pe = BitConverter.ToInt32(header, 0x3C);
        int section = pe + 24 + BitConverter.ToUInt16(header, pe + 20);
        size = BitConverter.ToInt32(header, section + 8);
        rva = BitConverter.ToInt32(header, section + 12);
    }

    static byte[] ReadRange(IntPtr h, long address, int size)
    {
        const int page = 0x10000;
        byte[] result = new byte[size], chunk = new byte[page];
        for (int offset = 0; offset < size; offset += page)
        {
            int length = Math.Min(page, size - offset);
            IntPtr read;
            if (Native.ReadProcessMemory(h, new IntPtr(address + offset), chunk, new IntPtr(length), out read))
                Buffer.BlockCopy(chunk, 0, result, offset, read.ToInt32());
        }
        return result;
    }

    byte[] Read(long rva, int size)
    {
        byte[] buf = new byte[size];
        IntPtr read;
        if (!Native.ReadProcessMemory(handle, new IntPtr(moduleBase + rva), buf, new IntPtr(size), out read) || read.ToInt32() != size)
            throw new InvalidOperationException("memory read failed");
        return buf;
    }

    void Write(long rva, byte[] data)
    {
        IntPtr written;
        if (!Native.WriteProcessMemory(handle, new IntPtr(moduleBase + rva), data, new IntPtr(data.Length), out written))
            throw new InvalidOperationException("memory write failed");
    }

    void WriteCode(long rva, byte[] data)
    {
        IntPtr address = new IntPtr(moduleBase + rva), length = new IntPtr(data.Length);
        uint old, ignored;
        if (!Native.VirtualProtectEx(handle, address, length, Native.PAGE_EXECUTE_READWRITE, out old))
            throw new InvalidOperationException("page protection change failed");
        try { Write(rva, data); }
        finally { Native.VirtualProtectEx(handle, address, length, old, out ignored); }
        Native.FlushInstructionCache(handle, address, length);
    }

    // The game is paused for the write so no thread can run half-written code.
    void SetPatches(Feature f, bool on)
    {
        Native.NtSuspendProcess(handle);
        try
        {
            foreach (Patch patch in f.Patches)
                WriteCode(patch.Rva, on ? patch.Bytes : patch.Original);
        }
        finally { Native.NtResumeProcess(handle); }
        f.Applied = on;
    }

    void WriteAbsolute(long address, byte[] data)
    {
        Write(address - moduleBase, data);
    }

    long AllocateCave()
    {
        for (long distance = NearestCave; distance < FarthestCave; distance += CaveStep)
        {
            IntPtr cave = Native.VirtualAllocEx(handle, new IntPtr(moduleBase - distance), new IntPtr(CaveSize), Native.MEM_COMMIT_RESERVE, Native.PAGE_EXECUTE_READWRITE);
            if (cave != IntPtr.Zero) return cave.ToInt64();
        }
        throw new InvalidOperationException("no memory near the game");
    }

    class Asm
    {
        readonly long origin;
        public readonly List<byte> Bytes = new List<byte>();
        public Asm(long origin) { this.origin = origin; }
        public void Hex(string text) { Bytes.AddRange(text.Split(' ').Select(x => Convert.ToByte(x, 16))); }
        // Emits an instruction ending in a rip-relative disp32, followed by `tail` more bytes.
        public void Rip(string op, long target, int tail)
        {
            Hex(op);
            Bytes.AddRange(BitConverter.GetBytes((int)(target - (origin + Bytes.Count + 4 + tail))));
        }
        public int Here { get { return Bytes.Count; } }
        // A forward jump whose target is set later with Land.
        public int JumpFrom(string op)
        {
            Hex(op);
            Bytes.AddRange(new byte[4]);
            return Bytes.Count - 4;
        }
        public void Land(int at)
        {
            byte[] rel = BitConverter.GetBytes(Bytes.Count - (at + 4));
            for (int i = 0; i < 4; i++) Bytes[at + i] = rel[i];
        }
        public void JumpTo(string op, int target)
        {
            Hex(op);
            Bytes.AddRange(BitConverter.GetBytes(target - (Bytes.Count + 4)));
        }
    }

    // void cave(controller rcx, velocity* rdx, float dt xmm3), called with the player in rdi.
    // Falls through to the real move call unless noclip is enabled; otherwise adds velocity and
    // vertical input to the capsule centre. A pending teleport puts the feet at the saved spot once.
    byte[] BuildMoveCave(long cave)
    {
        Asm a = new Asm(cave + CaveCodeOffset);
        a.Rip("48 89 0D", cave + CaveController, 0);               // mov [controller], rcx
        a.Rip("48 89 3D", cave + CavePlayer, 0);                   // mov [player], rdi
        a.Rip("80 3D", cave + CaveTeleport, 1); a.Hex("00");       // cmp byte [teleport], 0
        int toTeleport = a.JumpFrom("0F 85");                      // jne teleport
        a.Rip("80 3D", cave + CaveEnabled, 1); a.Hex("00");        // cmp byte [enabled], 0
        a.Hex("74 0A");                                            // je  move
        a.Hex("48 83 B9 C8 00 00 00 00");                          // cmp qword [rcx+0xC8], 0
        a.Hex("75 05");                                            // jne fly
        int move = a.Here;
        a.Rip("E9", moduleBase + moveRva, 0);                      // move: jmp original
        a.Hex("53 56 48 83 EC 58 48 89 CB 48 89 D6");              // fly: push rbx, rsi; rbx = controller, rsi = velocity
        a.Hex("F3 0F 11 5C 24 50");                                // keep dt
        a.Hex("48 8B 8B C8 00 00 00 48 8B 01 FF 50 18");           // centre = px->getPosition()
        a.Hex("F3 0F 10 00 F3 0F 10 48 04 F3 0F 10 50 08");        // xmm0-2 = centre
        a.Hex("F3 0F 10 5C 24 50 F3 0F 10 26");                    // xmm3 = dt, xmm4 = velocity.x
        a.Rip("F3 0F 59 25", cave + CaveSpeed, 0);
        a.Hex("F3 0F 59 E3 F3 0F 58 C4");                          // x += velocity.x * speed * dt
        a.Rip("F3 0F 10 25", cave + CaveVertical, 0);
        a.Hex("F3 0F 59 E3 F3 0F 58 CC F3 0F 10 66 08");           // y += vertical * dt
        a.Rip("F3 0F 59 25", cave + CaveSpeed, 0);
        a.Hex("F3 0F 59 E3 F3 0F 58 D4");                          // z += velocity.z * speed * dt
        a.Hex("F3 0F 11 44 24 40 F3 0F 11 4C 24 44 F3 0F 11 54 24 48");
        a.Hex("48 89 D9 48 8D 54 24 40 48 8B 03 FF 90 10 01 00 00"); // controller->setCentre(&position)
        a.Hex("48 83 C4 58 5E 5B C3");

        a.Land(toTeleport);                                        // teleport:
        a.Hex("48 83 B9 C8 00 00 00 00");                          // cmp qword [rcx+0xC8], 0
        a.JumpTo("0F 84", move);                                   // je  move
        a.Hex("53 56 48 83 EC 58 48 89 CB");                       // push rbx, rsi; rbx = controller
        a.Rip("C6 05", cave + CaveTeleport, 1); a.Hex("00");       // mov byte [teleport], 0
        a.Hex("48 8B 8B C8 00 00 00 48 8B 01 FF 50 18");           // centre = px->getPosition()
        a.Hex("F3 0F 10 00 F3 0F 5C 83 A8 01 00 00");              // x = centre.x - foot.x
        a.Rip("F3 0F 58 05", cave + CaveTarget, 0);                //     + target.x
        a.Hex("F3 0F 10 48 04 F3 0F 5C 8B AC 01 00 00");           // y = centre.y - foot.y
        a.Rip("F3 0F 58 0D", cave + CaveTarget + 4, 0);            //     + target.y
        a.Hex("F3 0F 10 50 08 F3 0F 5C 93 B0 01 00 00");           // z = centre.z - foot.z
        a.Rip("F3 0F 58 15", cave + CaveTarget + 8, 0);            //     + target.z
        a.Hex("F3 0F 11 44 24 40 F3 0F 11 4C 24 44 F3 0F 11 54 24 48");
        a.Hex("48 89 D9 48 8D 54 24 40 48 8B 03 FF 90 10 01 00 00"); // controller->setCentre(&position)
        a.Hex("48 83 C4 58 5E 5B C3");
        return a.Bytes.ToArray();
    }

    // bool cave(character rcx, bool log edx): immune when god mode is on and rcx is the player,
    // otherwise runs the game's own check, starting with the two instructions the jump replaced.
    byte[] BuildGodCave(long cave)
    {
        Asm a = new Asm(cave + CaveGodOffset);
        a.Rip("80 3D", cave + CaveGodEnabled, 1); a.Hex("00");     // cmp byte [god], 0
        a.Hex("74 0F");                                            // je  original
        a.Rip("48 3B 0D", cave + CavePlayer, 0);                   // cmp rcx, [player]
        a.Hex("75 06");                                            // jne original
        a.Hex("B8 01 00 00 00 C3");                                // return 1
        a.Hex("48 83 EC 28");                                      // original: sub rsp, 0x28
        a.Rip("F6 05", moduleBase + flagsRva, 1); a.Hex("02");     // test byte [flags], 2
        a.Rip("E9", moduleBase + godCheckRva + GodCheckLength, 0); // jmp back
        return a.Bytes.ToArray();
    }

    // Tests whether the weapon in r14 belongs to the player, using rax and rdx. Falls through when it
    // does not and returns the jump that is taken when it does.
    int EmitPlayerWeaponJump(Asm a)
    {
        a.Rip("48 8B 05", moduleBase + gameRva, 0);                // mov rax, [game]
        a.Hex("48 85 C0"); int noGame = a.JumpFrom("0F 84");       // test rax, rax; je other
        a.Hex("48 8B 40 38 48 85 C0"); int noPlayer = a.JumpFrom("0F 84"); // rax = player; je other
        a.Hex("49 8B 96 18 01 00 00");                             // mov rdx, [r14+0x118]
        a.Hex("48 85 D2"); int noOwner = a.JumpFrom("0F 84");      // test rdx, rdx; je other
        a.Hex("48 05 A0 00 00 00 48 3B 02"); int player = a.JumpFrom("0F 84"); // cmp player+0xA0, [rdx]; je player
        a.Land(noGame); a.Land(noPlayer); a.Land(noOwner);         // other:
        return player;
    }

    int CaveOffset(HookCave cave)
    {
        if (cave == HookCave.NoReload) return CaveNoReloadOffset;
        if (cave == HookCave.NoSpread) return CaveNoSpreadOffset;
        return cave == HookCave.RapidFire ? CaveRapidOffset : CaveFilterOffset;
    }

    // No reload: rounds in edi, rounds fired in r13d. The player's weapon keeps edi.
    // No spread: the weapon's spread in xmm0, kept in xmm14 for the bullets. The player's weapon gets
    // zero. rax holds the vtable the next instruction calls through, so it is saved.
    // Rapid fire: the shot interval in xmm0 is scaled for the player's weapon before it is added to
    // the cooldown. rax and rdx are free there: the code after the hook loads rdx itself.
    byte[] BuildHookCave(CodeHook hook, long cave)
    {
        Asm a = new Asm(cave + CaveOffset(hook.Cave));
        if (hook.Cave == HookCave.NoReload)
        {
            int player = EmitPlayerWeaponJump(a);
            a.Hex("41 2B FD");                                     // sub edi, r13d
            a.Land(player);
            a.Hex("49 8B 06");                                     // mov rax, [r14]
        }
        else if (hook.Cave == HookCave.FilterDrain)
        {
            a.Rip("0F 2F 15", cave + CaveFilterLimit, 0);          // comiss xmm2, [limit]
            int keep = a.JumpFrom("0F 86");                        // jbe keep (normal use: filter time stays)
            a.Hex("F3 41 0F 5C C1");                               // subss xmm0, xmm9
            a.Land(keep);
        }
        else if (hook.Cave == HookCave.RapidFire)
        {
            int player = EmitPlayerWeaponJump(a);
            int done = a.JumpFrom("E9");                           // other: jmp done
            a.Land(player);
            a.Rip("F3 0F 59 05", cave + CaveRapidScale, 0);        // mulss xmm0, [scale]
            a.Land(done);
            a.Hex("F3 41 0F 58 86 70 05 00 00");                   // addss xmm0, [r14+0x570]
        }
        else
        {
            a.Hex("50 52");                                        // push rax; push rdx
            int player = EmitPlayerWeaponJump(a);
            a.Hex("44 0F 28 F0"); int done = a.JumpFrom("E9");     // movaps xmm14, xmm0; jmp done
            a.Land(player);
            a.Hex("45 0F 57 F6");                                  // xorps xmm14, xmm14
            a.Land(done);
            a.Hex("5A 58 45 0F 57 FF");                            // pop rdx; pop rax; xorps xmm15, xmm15
        }
        a.Rip("E9", moduleBase + hook.Rva + hook.Length, 0);       // jmp back
        return a.Bytes.ToArray();
    }

    void SetCodeHooks(Feature f, bool on)
    {
        if (on && caveAddress == 0) CreateCave();
        foreach (CodeHook hook in f.Hooks)
        {
            if (!on) { WriteHook(hook.Rva, hook.Original); continue; }
            List<byte> jump = new List<byte> { 0xE9 };
            jump.AddRange(BitConverter.GetBytes((int)(caveAddress + CaveOffset(hook.Cave) - (moduleBase + hook.Rva + 5))));
            jump.AddRange(Enumerable.Repeat((byte)0x90, hook.Length - 5));
            WriteHook(hook.Rva, jump.ToArray());
        }
    }

    byte[] GodCheckOriginal()
    {
        List<byte> b = new List<byte> { 0x48, 0x83, 0xEC, 0x28, 0xF6, 0x05 };
        b.AddRange(BitConverter.GetBytes((int)(flagsRva - (godCheckRva + GodCheckLength))));
        b.Add(0x02);
        return b.ToArray();
    }

    void CreateCave()
    {
        long cave = AllocateCave();
        caveAddress = cave;
        WriteAbsolute(cave + CaveSpeed, BitConverter.GetBytes(NoClipSpeedScale));
        WriteAbsolute(cave + CaveRapidScale, BitConverter.GetBytes(RapidFireScale));
        WriteAbsolute(cave + CaveFilterLimit, BitConverter.GetBytes(FilterDrainLimit));
        WriteAbsolute(cave + CaveCodeOffset, BuildMoveCave(cave));
        WriteAbsolute(cave + CaveGodOffset, BuildGodCave(cave));
        foreach (CodeHook hook in features.SelectMany(f => f.Hooks).Where(h => h.Rva >= 0))
            WriteAbsolute(cave + CaveOffset(hook.Cave), BuildHookCave(hook, cave));
        Native.FlushInstructionCache(handle, new IntPtr(cave), new IntPtr(CaveSize));
    }

    // The game is paused for each hook write so no thread can run half-written code.
    void WriteHook(long rva, byte[] data)
    {
        Native.NtSuspendProcess(handle);
        try { WriteCode(rva, data); }
        finally { Native.NtResumeProcess(handle); }
    }

    void AimMoveCall(long target)
    {
        WriteHook(noClipSiteRva + 1, BitConverter.GetBytes((int)(target - (moduleBase + noClipSiteRva + 5))));
    }

    void SetGodHook(bool on)
    {
        if (!on) { WriteHook(godCheckRva, GodCheckOriginal()); return; }
        List<byte> jump = new List<byte> { 0xE9 };
        jump.AddRange(BitConverter.GetBytes((int)(caveAddress + CaveGodOffset - (moduleBase + godCheckRva + 5))));
        jump.AddRange(Enumerable.Repeat((byte)0x90, GodCheckLength - 5));
        WriteHook(godCheckRva, jump.ToArray());
    }

    // Noclip and god mode share one cave. The move call stays hooked while either is on, because
    // that is where the cave learns which object is the player.
    void SyncHooks()
    {
        Feature noClip = features.First(f => f.IsNoClip), god = features.First(f => f.IsGod);
        if (!noClip.Available) return;
        bool fly = noClip.On, immune = god.On && god.Available;
        bool hookMove = fly || immune || savePending || teleportPending;
        if (hookMove && caveAddress == 0) CreateCave();
        if (caveAddress != 0)
        {
            // Clearing the controller first means the next value the cave writes is from a fresh frame.
            if (savePending && !saveArmed)
            {
                WriteAbsolute(caveAddress + CaveController, new byte[8]);
                saveArmed = true;
            }
            if (teleportPending && !teleportArmed)
            {
                WriteAbsolute(caveAddress + CaveTarget, savedFoot);
                WriteAbsolute(caveAddress + CaveTeleport, BitConverter.GetBytes(1));
                teleportArmed = true;
            }
            if (fly && !noClip.Applied)
            {
                noClipVertical = 0;
                WriteAbsolute(caveAddress + CaveVertical, BitConverter.GetBytes(0f));
            }
            WriteAbsolute(caveAddress + CaveEnabled, BitConverter.GetBytes(fly ? 1 : 0));
            WriteAbsolute(caveAddress + CaveGodEnabled, BitConverter.GetBytes(immune ? 1 : 0));
        }
        if (hookMove != moveHooked)
        {
            AimMoveCall(hookMove ? caveAddress + CaveCodeOffset : moduleBase + moveRva);
            moveHooked = hookMove;
        }
        // Undoing a hook left by a killed trainer needs the flag word to rebuild the original bytes.
        if (immune != godHooked && flagsRva >= 0)
        {
            SetGodHook(immune);
            godHooked = immune;
        }
        noClip.Applied = fly;
        god.Applied = immune;
    }

    public static bool KeyDown(int key)
    {
        return Native.GetAsyncKeyState(key) < 0;
    }

    public bool GameHasFocus()
    {
        int pid;
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
        return pid == process.Id;
    }

    // Runs many times a second while noclip is on. Keys count only while the game is in front.
    public void UpdateNoClipInput()
    {
        if (!features.Any(f => f.IsNoClip && f.Applied)) return;
        float vertical = 0;
        if (GameHasFocus())
        {
            if (KeyDown(VK_SPACE)) vertical += NoClipVerticalSpeed;
            if (KeyDown(VK_CONTROL)) vertical -= NoClipVerticalSpeed;
            if (KeyDown(VK_SHIFT)) vertical *= NoClipBoost;
        }
        if (vertical == noClipVertical) return;
        noClipVertical = vertical;
        WriteAbsolute(caveAddress + CaveVertical, BitConverter.GetBytes(vertical));
    }

    public bool NoClipHooked
    {
        get { return noClipSiteRva + 5 + BitConverter.ToInt32(Read(noClipSiteRva + 1, 4), 0) != moveRva; }
    }

    public bool GodHooked { get { return Read(godCheckRva, 1)[0] == 0xE9; } }
    public bool IsHooked(CodeHook hook) { return Read(hook.Rva, 1)[0] == 0xE9; }
    public bool HasSavedPosition { get { return hasSaved; } }

    public bool RequestSavePosition()
    {
        if (!features.First(f => f.IsNoClip).Available) return false;
        savePending = true;
        saveArmed = false;
        return true;
    }

    public bool RequestTeleport()
    {
        if (!hasSaved || !features.First(f => f.IsNoClip).Available) return false;
        teleportPending = true;
        teleportArmed = false;
        return true;
    }

    public void CancelPositionRequests()
    {
        savePending = teleportPending = false;
        if (caveAddress != 0) WriteAbsolute(caveAddress + CaveTeleport, BitConverter.GetBytes(0));
    }

    // Finishes a save or teleport once the cave has seen a frame. Returns true when one finished,
    // so the caller can sync and let the move hook go if nothing else needs it.
    public bool UpdatePositionRequests()
    {
        if (caveAddress == 0 || !moveHooked) return false;
        bool finished = false;
        if (savePending && saveArmed)
        {
            long controller = BitConverter.ToInt64(Read(caveAddress - moduleBase + CaveController, 8), 0);
            if (controller != 0)
            {
                byte[] foot = new byte[12];
                IntPtr read;
                if (Native.ReadProcessMemory(handle, new IntPtr(controller + FootOffset), foot, new IntPtr(12), out read) && read.ToInt32() == 12)
                {
                    savedFoot = foot;
                    hasSaved = true;
                }
                savePending = false;
                finished = true;
            }
        }
        if (teleportPending && teleportArmed && Read(caveAddress - moduleBase + CaveTeleport, 1)[0] == 0)
        {
            teleportPending = false;
            finished = true;
        }
        return finished;
    }
    public bool GodCaveActive { get { return caveAddress != 0 && Read(caveAddress - moduleBase + CaveGodEnabled, 1)[0] != 0; } }

    public int ReadFlags() { return BitConverter.ToInt32(Read(flagsRva, 4), 0); }
    public byte[] ReadPatchBytes(Patch patch) { return Read(patch.Rva, patch.Bytes.Length); }
    public float ReadFloat(long rva) { return BitConverter.ToSingle(Read(rva, 4), 0); }

    // Brings the game in line with the toggles. Bits outside ManagedBits keep the game's own values.
    public void Sync()
    {
        if (flagsRva >= 0)
        {
            int wanted = features.Where(f => f.On && f.Bit != 0).Sum(f => f.Bit);
            int current = ReadFlags();
            int updated = (current & ~Catalog.ManagedBits) | wanted;
            if (updated != current) Write(flagsRva, BitConverter.GetBytes(updated));
        }
        foreach (Feature f in features)
        {
            if (!f.Available) continue;
            if (f.IsSpeed)
            {
                float target = f.On ? (float)(speedDefault * SpeedMultiplier) : speedDefault;
                if (ReadFloat(speedRva) != target) Write(speedRva, BitConverter.GetBytes(target));
            }
            else if ((f.Patches.Length > 0 || f.Hooks.Length > 0) && f.On != f.Applied)
            {
                SetCodeHooks(f, f.On);
                SetPatches(f, f.On);
            }
        }
        SyncHooks();
    }

    public void Detach()
    {
        if (handle != IntPtr.Zero) Native.CloseHandle(handle);
        handle = IntPtr.Zero;
        process = null;
        moduleBase = caveAddress = 0;
        flagsRva = speedRva = noClipSiteRva = moveRva = godCheckRva = godFlagsRva = gameRva = -1;
        moveHooked = godHooked = false;
        savePending = saveArmed = teleportPending = teleportArmed = hasSaved = false;
        foreach (Feature f in features) f.Applied = false;
    }

    public void Dispose()
    {
        Detach();
    }
}

class SpeedSlider : Control
{
    const double Min = 1.0, Max = 4.0, Step = 0.25;
    double value = 2.0;

    public event EventHandler ValueChanged;
    public double Value { get { return value; } }

    public SpeedSlider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        Cursor = Cursors.SizeWE;
    }

    int TrackLeft { get { return Theme.S(92); } }
    int TrackRight { get { return Width - Theme.S(72); } }

    void Pick(int x)
    {
        double ratio = (x - TrackLeft) / (double)(TrackRight - TrackLeft);
        double picked = Math.Round((Min + ratio * (Max - Min)) / Step) * Step;
        picked = Math.Max(Min, Math.Min(Max, picked));
        if (picked == value) return;
        value = picked;
        Invalidate();
        if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Pick(e.X); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (e.Button == MouseButtons.Left) Pick(e.X); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Theme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int mid = Height / 2, pad = Theme.S(10);
        TextRenderer.DrawText(g, Loc.T("multiplier"), Theme.LabelFont, new Point(pad, mid - Theme.LabelFont.Height / 2), Theme.Muted, Theme.Card, TextFormatFlags.NoPadding);
        string label = "×" + value.ToString("0.00", CultureInfo.InvariantCulture);
        Size size = TextRenderer.MeasureText(label, Theme.ValueFont, Size.Empty, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, label, Theme.ValueFont, new Point(Width - pad - size.Width, mid - Theme.ValueFont.Height / 2), Theme.Accent, Theme.Card, TextFormatFlags.NoPadding);

        float height = Theme.S(4), knob = Theme.S(14);
        float x = TrackLeft + (float)((value - Min) / (Max - Min)) * (TrackRight - TrackLeft);
        Theme.Fill(g, Theme.Track, new RectangleF(TrackLeft, mid - height / 2, TrackRight - TrackLeft, height), height / 2);
        Theme.Fill(g, Theme.Accent, new RectangleF(TrackLeft, mid - height / 2, x - TrackLeft, height), height / 2);
        using (SolidBrush brush = new SolidBrush(Theme.Accent))
            g.FillEllipse(brush, x - knob / 2, mid - knob / 2, knob, knob);
    }
}

// Two short tones generated in memory, rising for "on" and falling for "off".
class Sounds
{
    readonly SoundPlayer on = Make(659, 988), off = Make(659, 440);
    public bool Enabled = true;

    static SoundPlayer Make(double first, double second)
    {
        const int rate = 44100, toneSamples = rate * 70 / 1000;
        byte[] pcm = new byte[toneSamples * 2 * 2];
        double[] tones = { first, second };
        for (int t = 0; t < 2; t++)
            for (int i = 0; i < toneSamples; i++)
            {
                double time = i / (double)rate;
                double envelope = Math.Sin(Math.PI * i / toneSamples);
                double wave = Math.Sin(2 * Math.PI * tones[t] * time) + 0.25 * Math.Sin(4 * Math.PI * tones[t] * time);
                short sample = (short)(wave * envelope * 6500);
                int at = (t * toneSamples + i) * 2;
                pcm[at] = (byte)sample;
                pcm[at + 1] = (byte)(sample >> 8);
            }
        MemoryStream wav = new MemoryStream();
        BinaryWriter w = new BinaryWriter(wav);
        w.Write(0x46464952); w.Write(36 + pcm.Length); w.Write(0x45564157);
        w.Write(0x20746D66); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(0x61746164); w.Write(pcm.Length); w.Write(pcm);
        wav.Position = 0;
        SoundPlayer player = new SoundPlayer(wav);
        player.Load();
        return player;
    }

    public void Play(bool activated)
    {
        if (!Enabled) return;
        // A machine with no audio device must not take the trainer down.
        try { (activated ? on : off).Play(); }
        catch (Exception) { }
    }

    public void PlayBothAndWait()
    {
        on.PlaySync();
        off.PlaySync();
    }
}

class MainForm : Form
{
    const int VK_BACK = 0x08, VK_ESCAPE = 0x1B, VK_SPACE = 0x20, VK_DELETE = 0x2E, DisableAllKey = VK_DELETE;
    const string SaveAction = "pos.save", TeleportAction = "pos.load";

    // Single keys, no modifier. They avoid the game's own bindings (1-5, most letters, F5) and keys
    // other tools commonly take: Steam screenshots on F12, ReShade-based mods on Home, F5 and F6.
    static readonly Dictionary<string, int> DefaultKeys = new Dictionary<string, int>
    {
        { "god", 0x70 }, { "ammo", 0x71 }, { "stealth", 0x72 }, { "medkits", 0x73 },
        { "filters", 0x76 }, { "nomask", 0x77 }, { "noreload", 0x78 }, { "air", 0x79 }, { "noclip", 0x7A },
        { "battery", 0x36 }, { "freeze", 0x37 }, { "speed", 0x38 }, { "money", 0x39 }, { "norecoil", 0x30 }, { "rapid", 0xBD },
        { SaveAction, 0x21 }, { TeleportAction, 0x22 },
    };
    const int NormalRetryTicks = 3, UnsupportedRetryTicks = 20;

    readonly List<Feature> features = Catalog.Build();
    readonly Game game;
    readonly Sounds sounds = new Sounds();
    readonly SpeedSlider slider = new SpeedSlider();
    readonly ToggleRow soundRow = new ToggleRow();
    readonly FlatButton disableAll = new FlatButton(false);
    readonly ActionRow saveRow = new ActionRow();
    readonly ActionRow teleportRow = new ActionRow { Available = false };
    readonly ToggleRow topRow = new ToggleRow();
    bool positionSaved;
    readonly LangBar langBar = new LangBar();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 500 };
    readonly System.Windows.Forms.Timer inputTimer = new System.Windows.Forms.Timer { Interval = 15 };
    readonly Dictionary<string, int> keys = new Dictionary<string, int>(DefaultKeys);
    readonly Dictionary<string, bool> held = new Dictionary<string, bool>();
    readonly HashSet<int> captureIgnore = new HashSet<int>();
    string capturing;
    bool disableHeld;
    readonly List<Tuple<string, Rectangle>> cards = new List<Tuple<string, Rectangle>>();
    string statusKey = "status.notrunning";
    object[] statusArgs = new object[0];
    Color statusColor = Theme.Faint;
    int attachCooldown;

    public MainForm()
    {
        game = new Game(features);
        LoadKeys();
        BackColor = Theme.Back;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        DoubleBuffered = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(Theme.S(780), Theme.S(660));
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch (Exception) { }

        int left = 20, right = 398, column = 362, y = 84, title = 34, world = 406;
        AddCard("section.survival", left, y, column, 310);
        AddCard("section.weapons", right, y, column, 310);
        AddCard("section.world", left, world, 740, 176);

        int[] survival = { 0, 2, 3, 4, 5, 12 }, weapons = { 1, 6, 13, 14, 7, 8 };
        for (int i = 0; i < survival.Length; i++) Place(survival[i], left, y + title + i * 44, 44);
        for (int i = 0; i < weapons.Length; i++) Place(weapons[i], right, y + title + i * 44, 44);
        Place(9, left, world + title, 44);
        Place(10, right, world + title, 44);
        Place(11, left, world + title + 44, 44);

        slider.SetBounds(Theme.S(right + 6), Theme.S(world + title + 46), Theme.S(column - 12), Theme.S(34));
        slider.ValueChanged += delegate { game.SpeedMultiplier = slider.Value; SyncGame(); };
        Controls.Add(slider);

        saveRow.SetBounds(Theme.S(left + 6), Theme.S(world + title + 88), Theme.S(350), Theme.S(44));
        saveRow.Activated += delegate { SavePosition(); };
        saveRow.HotkeyClicked += delegate { StartCapture(SaveAction); };
        teleportRow.SetBounds(Theme.S(right + 6), Theme.S(world + title + 88), Theme.S(350), Theme.S(44));
        teleportRow.Activated += delegate { TeleportBack(); };
        teleportRow.HotkeyClicked += delegate { StartCapture(TeleportAction); };
        Controls.Add(saveRow);
        Controls.Add(teleportRow);

        topRow.SetBounds(Theme.S(130), Theme.S(592), Theme.S(170), Theme.S(32));
        topRow.BackColor = Theme.Back;
        topRow.On = Preferences.Read("TopMost") == "1";
        topRow.SnapAnimation();
        TopMost = topRow.On;
        topRow.Toggled += delegate
        {
            TopMost = topRow.On;
            Preferences.Write("TopMost", topRow.On ? "1" : "0");
            sounds.Play(topRow.On);
        };
        Controls.Add(topRow);

        soundRow.SetBounds(Theme.S(20), Theme.S(592), Theme.S(100), Theme.S(32));
        soundRow.BackColor = Theme.Back;
        soundRow.On = Preferences.Read("Sound") != "0";
        soundRow.SnapAnimation();
        sounds.Enabled = soundRow.On;
        soundRow.Toggled += delegate
        {
            sounds.Enabled = soundRow.On;
            Preferences.Write("Sound", soundRow.On ? "1" : "0");
            sounds.Play(soundRow.On);
        };
        Controls.Add(soundRow);

        disableAll.SetBounds(Theme.S(310), Theme.S(592), Theme.S(450), Theme.S(32));
        disableAll.Click += delegate { DisableAll(); };
        Controls.Add(disableAll);

        PlaceLangBar();
        langBar.Picked += delegate { ApplyLanguage(); };
        Controls.Add(langBar);

        ApplyLanguage();
        timer.Tick += delegate { SyncGame(); };
        timer.Start();
        inputTimer.Tick += delegate { PollInput(); };
        inputTimer.Start();
    }

    void PollInput()
    {
        try
        {
            bool attached = game.Attached;
            PollHotkeys(ActiveForm == this || (attached && game.GameHasFocus()));
            if (attached)
            {
                game.UpdateNoClipInput();
                if (game.UpdatePositionRequests()) SyncGame();
            }
            bool saved = attached && game.HasSavedPosition;
            if (saved != positionSaved)
            {
                positionSaved = saved;
                UpdatePositionRows();
            }
        }
        catch (Exception) { }
    }

    void LoadKeys()
    {
        string saved = Preferences.Read("Keys") ?? "";
        foreach (string pair in saved.Split(';'))
        {
            string[] parts = pair.Split(':');
            int vk;
            if (parts.Length == 2 && keys.ContainsKey(parts[0]) && int.TryParse(parts[1], out vk) && vk >= 0 && vk < 256)
                keys[parts[0]] = vk;
        }
    }

    void SaveKeys()
    {
        Preferences.Write("Keys", string.Join(";", keys.Select(k => k.Key + ":" + k.Value)));
    }

    static string KeyName(int vk)
    {
        if (vk == 0) return "—";
        if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);
        if ((vk >= 0x30 && vk <= 0x39) || (vk >= 0x41 && vk <= 0x5A)) return ((char)vk).ToString();
        if (vk >= 0x60 && vk <= 0x69) return "Num " + (vk - 0x60);
        switch (vk)
        {
            case 0x04: return "Mouse 3";
            case 0x05: return "Mouse 4";
            case 0x06: return "Mouse 5";
            case 0x09: return "Tab";
            case 0x0D: return "Enter";
            case 0x13: return "Pause";
            case 0x14: return "Caps";
            case 0x21: return "PgUp";
            case 0x22: return "PgDn";
            case 0x23: return "End";
            case 0x24: return "Home";
            case 0x25: return "←";
            case 0x26: return "↑";
            case 0x27: return "→";
            case 0x28: return "↓";
            case 0x2D: return "Ins";
            case 0x2E: return "Del";
            case 0x6A: return "Num *";
            case 0x6B: return "Num +";
            case 0x6D: return "Num -";
            case 0x6E: return "Num .";
            case 0x6F: return "Num /";
            case 0xBD: return "-";
            case 0xBB: return "=";
        }
        System.Text.StringBuilder name = new System.Text.StringBuilder(32);
        int scan = (int)Native.MapVirtualKey((uint)vk, 0);
        return Native.GetKeyNameText(scan << 16, name, name.Capacity) > 0 ? name.ToString() : "#" + vk;
    }

    void RefreshHotkeyLabels()
    {
        Func<string, string> label = action => capturing == action ? Loc.T("key.press") : KeyName(keys[action]);
        foreach (Feature f in features) f.Row.Hotkey = label(f.Key);
        saveRow.Hotkey = label(SaveAction);
        teleportRow.Hotkey = label(TeleportAction);
        disableAll.Text = Loc.T("disable") + "  (" + KeyName(DisableAllKey) + ")";
        disableAll.Invalidate();
    }

    void StartCapture(string action)
    {
        capturing = action;
        // Keys already down, such as the mouse button that started this, are not taken as the answer.
        captureIgnore.Clear();
        for (int vk = 1; vk < 256; vk++) if (Game.KeyDown(vk)) captureIgnore.Add(vk);
        RefreshHotkeyLabels();
    }

    // Mouse buttons 1-2, modifiers and Space are left alone: the game and noclip need them.
    static bool Bindable(int vk)
    {
        if (vk < 4 || vk == VK_SPACE || vk == DisableAllKey) return false;
        if (vk >= 0x10 && vk <= 0x12) return false;
        if (vk >= 0xA0 && vk <= 0xA5) return false;
        return vk != 0x5B && vk != 0x5C;
    }

    // Esc cancels, Backspace leaves the action without a key, a key already in use moves here.
    void CaptureKey()
    {
        for (int vk = 1; vk < 256; vk++)
        {
            if (!Game.KeyDown(vk)) { captureIgnore.Remove(vk); continue; }
            if (captureIgnore.Contains(vk) || (!Bindable(vk) && vk != VK_ESCAPE && vk != VK_BACK)) continue;
            if (vk != VK_ESCAPE)
            {
                int key = vk == VK_BACK ? 0 : vk;
                foreach (string other in keys.Keys.Where(a => a != capturing && key != 0 && keys[a] == key).ToList()) keys[other] = 0;
                keys[capturing] = key;
                held[capturing] = true;
                SaveKeys();
                sounds.Play(true);
            }
            capturing = null;
            RefreshHotkeyLabels();
            return;
        }
    }

    // A running level keeps RegisterHotKey messages from arriving, so the keys are polled. They only
    // count while the game or the trainer is in front, so other programs keep their keys.
    void PollHotkeys(bool active)
    {
        if (capturing != null) { CaptureKey(); return; }
        foreach (KeyValuePair<string, int> binding in keys.ToList())
        {
            bool down = active && binding.Value != 0 && Game.KeyDown(binding.Value), was;
            held.TryGetValue(binding.Key, out was);
            if (down && !was) Perform(binding.Key);
            held[binding.Key] = down;
        }
        bool off = active && Game.KeyDown(DisableAllKey);
        if (off && !disableHeld) DisableAll();
        disableHeld = off;
    }

    void Perform(string action)
    {
        if (action == SaveAction) { SavePosition(); return; }
        if (action == TeleportAction) { TeleportBack(); return; }
        Feature feature = features.First(f => f.Key == action);
        if (!feature.Row.Available) return;
        feature.Row.On = !feature.Row.On;
        OnToggled(feature);
    }

    void UpdatePositionRows()
    {
        saveRow.SetText(Loc.T("pos.save"), Loc.T(positionSaved ? "pos.saved" : "pos.save.sub"), Loc.T("pos.save.button"));
        teleportRow.SetText(Loc.T("pos.load"), Loc.T(positionSaved ? "pos.load.sub" : "pos.load.none"), Loc.T("pos.load.button"));
        teleportRow.Available = positionSaved;
    }

    void SavePosition()
    {
        if (!game.Attached || !game.RequestSavePosition()) return;
        sounds.Play(true);
        SyncGame();
    }

    void TeleportBack()
    {
        if (!game.Attached || !game.RequestTeleport()) return;
        sounds.Play(true);
        SyncGame();
    }

    void AddCard(string key, int x, int y, int width, int height)
    {
        cards.Add(Tuple.Create(key, new Rectangle(Theme.S(x), Theme.S(y), Theme.S(width), Theme.S(height))));
    }

    void Place(int index, int cardX, int y, int height)
    {
        Feature feature = features[index];
        feature.Row = new ToggleRow();
        feature.Row.HotkeyClicked += delegate { StartCapture(feature.Key); };
        feature.Row.SetBounds(Theme.S(cardX + 6), Theme.S(y), Theme.S(350), Theme.S(height));
        feature.Row.Toggled += delegate { OnToggled(feature); };
        Controls.Add(feature.Row);
    }

    // Last Light's title is longer than 2033's, so the language bar starts after whatever the title needs.
    void PlaceLangBar()
    {
        int title = Theme.S(74) + TextRenderer.MeasureText(Product.Header, Theme.HeadFont, Size.Empty, TextFormatFlags.NoPadding).Width;
        langBar.SetBounds(Math.Max(Theme.S(310), title + Theme.S(18)), Theme.S(26), Theme.S(180), Theme.S(28));
    }

    void ApplyLanguage()
    {
        Text = Product.AppName;
        PlaceLangBar();
        foreach (Feature f in features)
            f.Row.SetText(Loc.T(f.Key), Loc.T(f.Key + ".sub"), Loc.T("notfound"));
        soundRow.SetText(Loc.T("sound"), null, null);
        RefreshHotkeyLabels();
        topRow.SetText(Loc.T("topmost"), null, null);
        disableAll.Invalidate();
        UpdatePositionRows();
        slider.Invalidate();
        Invalidate();
    }

    void OnToggled(Feature feature)
    {
        feature.On = feature.Row.On;
        sounds.Play(feature.On);
        SyncGame();
    }

    void DisableAll()
    {
        bool any = false;
        foreach (Feature f in features)
        {
            any |= f.On;
            f.On = false;
            f.Row.On = false;
        }
        if (any) sounds.Play(false);
        SyncGame();
    }

    void SetStatus(string key, Color color, params object[] args)
    {
        if (statusKey == key && statusColor == color && statusArgs.SequenceEqual(args)) return;
        statusKey = key;
        statusArgs = args;
        statusColor = color;
        Invalidate(new Rectangle(ClientSize.Width / 2, 0, ClientSize.Width / 2, Theme.S(80)));
    }

    void SyncGame()
    {
        try
        {
            if (!game.Attached)
            {
                game.Detach();
                if (attachCooldown-- > 0) return;
                AttachResult result = game.TryAttach();
                if (result != AttachResult.Connected)
                {
                    attachCooldown = result == AttachResult.Unsupported ? UnsupportedRetryTicks : NormalRetryTicks;
                    ShowProblem(result);
                    return;
                }
                foreach (Feature f in features) f.Row.Available = f.Available;
            }
            game.Sync();
            SetStatus("status.connected", Theme.Good, features.Count(f => f.Available), features.Count);
        }
        catch (Exception)
        {
            game.Detach();
            SetStatus("status.lost", Theme.Accent);
        }
    }

    void ShowProblem(AttachResult result)
    {
        foreach (Feature f in features) f.Row.Available = true;
        if (result == AttachResult.NotRunning) SetStatus("status.notrunning", Theme.Faint);
        else if (result == AttachResult.NoAccess) SetStatus("status.access", Theme.Accent);
        else if (result == AttachResult.Unsupported) SetStatus("status.unsupported", Theme.Accent);
        else if (result == AttachResult.WrongGame) SetStatus("status.wronggame", Theme.Accent);
        else SetStatus("status.waiting", Theme.Accent);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.UseDarkTitleBar(Handle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.DrawHeader(g, ClientSize.Width, Theme.S(78), Product.Header, Loc.T("subtitle"));

        string status = string.Format(Loc.T(statusKey), statusArgs);
        Size size = TextRenderer.MeasureText(status, Theme.StatusFont, Size.Empty, TextFormatFlags.NoPadding);
        int pillWidth = size.Width + Theme.S(36), pillHeight = Theme.S(32);
        int pillX = ClientSize.Width - Theme.S(20) - pillWidth, pillY = Theme.S(24);
        Theme.Fill(g, Theme.Card, new RectangleF(pillX, pillY, pillWidth, pillHeight), pillHeight / 2f);
        using (SolidBrush brush = new SolidBrush(statusColor))
            g.FillEllipse(brush, pillX + Theme.S(13), pillY + (pillHeight - Theme.S(9)) / 2f, Theme.S(9), Theme.S(9));
        TextRenderer.DrawText(g, status, Theme.StatusFont, new Point(pillX + Theme.S(28), pillY + (pillHeight - size.Height) / 2), Theme.Text, Theme.Card, TextFormatFlags.NoPadding);

        foreach (Tuple<string, Rectangle> card in cards)
        {
            Theme.Fill(g, Theme.Card, card.Item2, Theme.S(10));
            TextRenderer.DrawText(g, Loc.T(card.Item1), Theme.SectionFont, new Point(card.Item2.X + Theme.S(16), card.Item2.Y + Theme.S(12)), Theme.Accent, Theme.Card, TextFormatFlags.NoPadding);
        }

        Size hintSize = TextRenderer.MeasureText(Loc.T("hint"), Theme.SubFont, Size.Empty, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Loc.T("hint"), Theme.SubFont, new Point(Theme.S(20), Theme.S(634)), Theme.Faint, Theme.Back, TextFormatFlags.NoPadding);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        timer.Stop();
        inputTimer.Stop();
        try
        {
            foreach (Feature f in features) f.On = false;
            if (game.Attached)
            {
                game.CancelPositionRequests();
                game.Sync();
            }
        }
        catch (Exception) { }
        game.Dispose();
        base.OnFormClosing(e);
    }

    // Renders the window with a few switches on, for checking the layout.
    public void SavePreview(string path)
    {
        foreach (int i in new[] { 0, 1, 4, 10, 11 }) { features[i].Row.On = true; features[i].Row.SnapAnimation(); }
        SetStatus("status.connected", Theme.Good, features.Count, features.Count);
        using (Bitmap bitmap = new Bitmap(Width, Height))
        {
            DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
            bitmap.Save(path, ImageFormat.Png);
        }
    }
}

static class Diagnostics
{
    static readonly string[,] LanguageCases =
    {
        { "ru-RU", "ru" }, { "ru-UA", "ru" }, { "uk-UA", "uk" }, { "zh-CN", "zh" }, { "zh-TW", "zh" },
        { "en-US", "en" }, { "de-DE", "en" }, { "be-BY", "en" }, { "ja-JP", "en" },
    };

    static bool CheckLanguages(StreamWriter log)
    {
        bool ok = true;
        for (int i = 0; i < LanguageCases.GetLength(0); i++)
        {
            string detected = Loc.Codes[Loc.Detect(new CultureInfo(LanguageCases[i, 0]))];
            bool match = detected == LanguageCases[i, 1];
            log.WriteLine("language {0} -> {1} {2}", LanguageCases[i, 0], detected, match ? "ok" : "WRONG");
            ok &= match;
        }
        foreach (string key in Loc.Keys)
            for (Loc.Current = 0; Loc.Current < Loc.Codes.Length; Loc.Current++)
                if (string.IsNullOrWhiteSpace(Loc.T(key))) { log.WriteLine("missing text {0}", key); ok = false; }
        Loc.Current = 0;
        return ok;
    }

    static bool Finish(StreamWriter log, bool ok)
    {
        log.WriteLine(ok ? "PASS" : "FAIL");
        return ok;
    }

    // Turns everything on, checks the game's memory really changed, then restores it all.
    public static bool SelfTest(string path)
    {
        List<Feature> features = Catalog.Build();
        using (StreamWriter log = new StreamWriter(path))
        using (Game game = new Game(features))
        {
            bool ok = CheckLanguages(log);
            new Sounds().PlayBothAndWait();
            log.WriteLine("sounds ok");
            AttachResult result = game.TryAttach();
            log.WriteLine("attach {0}", result);
            if (result != AttachResult.Connected) return Finish(log, false);
            int flagsBefore = game.ReadFlags();
            float speedBefore = game.ReadFloat(game.SpeedRva);
            log.WriteLine("flags rva 0x{0:X} = 0x{1:X}, speed rva 0x{2:X} = {3}", game.FlagsRva, flagsBefore, game.SpeedRva, speedBefore);
            foreach (Feature f in features)
            {
                log.WriteLine("{0}: {1}", f.Key, f.Available ? "ok" : "MISSING");
                ok &= f.Available;
                f.On = true;
            }
            game.Sync();
            int flagsOn = game.ReadFlags();
            const int bits = Catalog.StealthBit | Catalog.AmmoBit;
            ok &= (flagsOn & Catalog.ManagedBits) == bits && (flagsOn & ~Catalog.ManagedBits) == (flagsBefore & ~Catalog.ManagedBits);
            ok &= game.ReadFloat(game.SpeedRva) == speedBefore * 2;
            ok &= game.NoClipHooked && game.GodHooked && game.GodCaveActive;
            foreach (Feature f in features)
            {
                foreach (Patch patch in f.Patches)
                    ok &= game.ReadPatchBytes(patch).SequenceEqual(patch.Bytes);
                foreach (CodeHook hook in f.Hooks)
                    ok &= game.IsHooked(hook);
            }
            log.WriteLine("flags on 0x{0:X}, speed on {1}, noclip {2}, god hook {3}, code hooks {4}", flagsOn, game.ReadFloat(game.SpeedRva), game.NoClipHooked, game.GodHooked, features.SelectMany(f => f.Hooks).Count(game.IsHooked));
            foreach (Feature f in features) f.On = false;
            game.Sync();
            int flagsAfter = game.ReadFlags();
            ok &= (flagsAfter & Catalog.ManagedBits) == 0 && (flagsAfter & ~Catalog.ManagedBits) == (flagsBefore & ~Catalog.ManagedBits);
            ok &= game.ReadFloat(game.SpeedRva) == speedBefore && !game.NoClipHooked && !game.GodHooked;
            ok &= !features.SelectMany(f => f.Hooks).Any(game.IsHooked);
            foreach (Feature f in features)
                foreach (Patch patch in f.Patches)
                    ok &= game.ReadPatchBytes(patch).SequenceEqual(patch.Original);
            log.WriteLine("flags after 0x{0:X}, speed after {1}", game.ReadFlags(), game.ReadFloat(game.SpeedRva));
            return Finish(log, ok);
        }
    }

    // Leaves everything switched on and exits without cleaning up, like a killed trainer would.
    public static bool Abandon(string path)
    {
        List<Feature> features = Catalog.Build();
        using (StreamWriter log = new StreamWriter(path))
        using (Game game = new Game(features))
        {
            if (game.TryAttach() != AttachResult.Connected) return Finish(log, false);
            foreach (Feature f in features) f.On = true;
            game.Sync();
            return Finish(log, true);
        }
    }

    // Attaches to a game a killed trainer left patched and checks everything can be restored.
    public static bool Recover(string path)
    {
        List<Feature> features = Catalog.Build();
        using (StreamWriter log = new StreamWriter(path))
        using (Game game = new Game(features))
        {
            AttachResult result = game.TryAttach();
            log.WriteLine("attach {0}", result);
            if (result != AttachResult.Connected) return Finish(log, false);
            bool ok = true;
            foreach (Feature f in features.Where(x => x.Patches.Length > 0 || x.IsNoClip || x.IsGod))
            {
                log.WriteLine("{0}: available {1}, still patched {2}", f.Key, f.Available, f.Applied);
                ok &= f.Available && f.Applied;
            }
            log.WriteLine("flags 0x{0:X}, speed {1}", game.ReadFlags(), game.ReadFloat(game.SpeedRva));
            game.Sync();
            foreach (Feature f in features)
                foreach (Patch patch in f.Patches)
                    ok &= game.ReadPatchBytes(patch).SequenceEqual(patch.Original);
            ok &= (game.ReadFlags() & Catalog.ManagedBits) == 0 && game.ReadFloat(game.SpeedRva) == 1.25f && !game.NoClipHooked && !game.GodHooked;
            ok &= !features.SelectMany(f => f.Hooks).Any(game.IsHooked);
            log.WriteLine("flags after 0x{0:X}, speed after {1}", game.ReadFlags(), game.ReadFloat(game.SpeedRva));
            return Finish(log, ok);
        }
    }

    // Logs each attach state for a while, to check recognition across a game launch and exit.
    public static bool Watch(string path, int seconds)
    {
        List<Feature> features = Catalog.Build();
        using (StreamWriter log = new StreamWriter(path) { AutoFlush = true })
        using (Game game = new Game(features))
        {
            string last = null;
            for (DateTime end = DateTime.Now.AddSeconds(seconds); DateTime.Now < end; Thread.Sleep(1000))
            {
                string state = game.Attached ? AttachResult.Connected.ToString() : game.TryAttach().ToString();
                if (state == last) continue;
                last = state;
                log.WriteLine("{0:HH:mm:ss} {1} ({2} of {3})", DateTime.Now, state, features.Count(f => f.Available), features.Count);
            }
            return true;
        }
    }
}

static class Program
{
    static void FocusRunningTrainer()
    {
        Process current = Process.GetCurrentProcess();
        foreach (Process other in Process.GetProcessesByName(current.ProcessName))
            if (other.Id != current.Id) Native.SetForegroundWindow(other.MainWindowHandle);
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--selftest") return Diagnostics.SelfTest(args[1]) ? 0 : 1;
        if (args.Length >= 2 && args[0] == "--abandon") return Diagnostics.Abandon(args[1]) ? 0 : 1;
        if (args.Length >= 2 && args[0] == "--recover") return Diagnostics.Recover(args[1]) ? 0 : 1;
        if (args.Length >= 3 && args[0] == "--watch") return Diagnostics.Watch(args[1], int.Parse(args[2])) ? 0 : 1;

        Loc.Initialize();
        if (args.Length >= 3 && args[0] == "--preview") Loc.Current = Array.IndexOf(Loc.Codes, args[2]);
        Theme.Initialize();
        Application.EnableVisualStyles();
        if (args.Length >= 3 && args[0] == "--preview")
        {
            MainForm preview = new MainForm { StartPosition = FormStartPosition.Manual, Location = new Point(-6000, 0) };
            preview.Show();
            Application.DoEvents();
            preview.SavePreview(args[1]);
            preview.Close();
            return 0;
        }

        // Two trainers would fight over the same flags and patches.
        bool firstInstance;
        using (new Mutex(true, Product.Id + ".Instance", out firstInstance))
        {
            if (!firstInstance) { FocusRunningTrainer(); return 0; }
            Application.Run(new MainForm());
        }
        return 0;
    }
}

// Dark theme and custom controls shared by the trainer and its setup program.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class Theme
{
    public static readonly Color Back = Color.FromArgb(17, 19, 23), Card = Color.FromArgb(27, 30, 36), CardHover = Color.FromArgb(35, 39, 46);
    public static readonly Color Text = Color.FromArgb(232, 230, 225), Muted = Color.FromArgb(144, 150, 160), Faint = Color.FromArgb(82, 87, 96);
    public static readonly Color Good = Color.FromArgb(103, 199, 124), Track = Color.FromArgb(58, 62, 71), Chip = Color.FromArgb(46, 50, 59);
    public static readonly Color Accent = Color.FromArgb(232, 108, 52);
    public static readonly Color Tunnel = Color.FromArgb(42, 24, 17);
    public static readonly Font LangFont = new Font("Microsoft YaHei UI", 8.5f);
    public static Font TitleFont, SubFont, KeyFont, HeadFont, SectionFont, StatusFont, LabelFont, ValueFont, ButtonFont;
    public static float K = 1f;
    public static int S(float value) { return (int)Math.Round(value * K); }

    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr wnd, int attribute, ref int value, int size);

    public static void Initialize()
    {
        SetProcessDPIAware();
        using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) K = g.DpiX / 96f;
        ApplyFonts();
    }

    public static void UseDarkTitleBar(IntPtr window)
    {
        int dark = 1;
        DwmSetWindowAttribute(window, 20, ref dark, 4);
    }

    // Segoe UI has no Chinese glyphs, so Chinese gets its own family.
    public static void ApplyFonts()
    {
        string regular = Loc.IsChinese ? "Microsoft YaHei UI" : "Segoe UI";
        string strong = Loc.IsChinese ? "Microsoft YaHei UI" : "Segoe UI Semibold";
        FontStyle strongStyle = Loc.IsChinese ? FontStyle.Bold : FontStyle.Regular;
        TitleFont = new Font(strong, 10f, strongStyle);
        SubFont = new Font(regular, 8f);
        KeyFont = new Font("Segoe UI Semibold", 8f);
        HeadFont = new Font(strong, 15f, strongStyle);
        SectionFont = new Font(strong, 8f, strongStyle);
        StatusFont = new Font(regular, 9f);
        LabelFont = new Font(regular, 8.5f);
        ValueFont = new Font("Segoe UI Semibold", 10f);
        ButtonFont = new Font(strong, 9f, strongStyle);
    }

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        float d = radius * 2;
        GraphicsPath path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void Fill(Graphics g, Color color, RectangleF r, float radius)
    {
        using (GraphicsPath path = Round(r, radius))
        using (SolidBrush brush = new SolidBrush(color))
            g.FillPath(brush, path);
    }

    // A key hint ending at `right`, centred in a row of `height`. Returns where it was drawn.
    public static Rectangle DrawChip(Graphics g, string text, int right, int height)
    {
        Size size = TextRenderer.MeasureText(text, KeyFont, Size.Empty, TextFormatFlags.NoPadding);
        int width = Math.Max(size.Width + S(14), S(34)), chipHeight = size.Height + S(8);
        int x = right - width, y = (height - chipHeight) / 2;
        Fill(g, Chip, new RectangleF(x, y, width, chipHeight), S(5));
        TextRenderer.DrawText(g, text, KeyFont, new Rectangle(x, y, width, chipHeight), Text, Chip, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        return new Rectangle(x, y, width, chipHeight);
    }

    // A station-sign ring with the letter M. It is drawn here, so no artwork from the game is used.
    public static void DrawEmblem(Graphics g, float x, float y, float size)
    {
        using (Pen ring = new Pen(Accent, size * 0.08f))
            g.DrawEllipse(ring, x + size * 0.06f, y + size * 0.06f, size * 0.88f, size * 0.88f);
        using (Font font = new Font("Segoe UI Semibold", size * 0.42f, FontStyle.Bold, GraphicsUnit.Pixel))
        using (SolidBrush brush = new SolidBrush(Accent))
        using (StringFormat centre = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString("M", font, brush, new RectangleF(x, y + size * 0.02f, size, size), centre);
    }

    // The header band: a dim tunnel light fading into the background, the emblem and the titles.
    public static void DrawHeader(Graphics g, int width, int height, string title, string subtitle)
    {
        using (LinearGradientBrush glow = new LinearGradientBrush(new Rectangle(0, 0, width, height), Tunnel, Back, LinearGradientMode.Vertical))
            g.FillRectangle(glow, 0, 0, width, height);
        using (Pen line = new Pen(Color.FromArgb(70, Accent)))
            g.DrawLine(line, S(20), height - 1, width - S(20), height - 1);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // Only the emblem's letter uses GDI+ text; the hint is put back so other text keeps ClearType.
        TextRenderingHint hint = g.TextRenderingHint;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        DrawEmblem(g, S(20), S(14), S(44));
        g.TextRenderingHint = hint;
        TextRenderer.DrawText(g, title, HeadFont, new Point(S(74), S(14)), Text, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, subtitle, SubFont, new Point(S(75), S(46)), Muted, TextFormatFlags.NoPadding);
    }
}

class ToggleRow : Control
{
    readonly Timer animation = new Timer { Interval = 15 };
    string title, subtitle, hotkey, unavailable;
    bool on, hover, available = true;
    float knob;
    Rectangle chip;

    public event EventHandler Toggled, HotkeyClicked;

    public ToggleRow()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.Card;
        Cursor = Cursors.Hand;
        TabStop = true;
        animation.Tick += delegate
        {
            float target = on ? 1f : 0f;
            knob += Math.Sign(target - knob) * 0.2f;
            if (Math.Abs(target - knob) < 0.21f) { knob = target; animation.Stop(); }
            Invalidate();
        };
    }

    public void SetText(string title, string subtitle, string unavailable)
    {
        this.title = title;
        this.subtitle = subtitle;
        this.unavailable = unavailable;
        Invalidate();
    }

    public string Hotkey
    {
        get { return hotkey; }
        set { hotkey = value; Invalidate(); }
    }

    public bool On
    {
        get { return on; }
        set { if (on == value) return; on = value; animation.Start(); Invalidate(); }
    }

    public bool Available
    {
        get { return available; }
        set { if (available == value) return; available = value; Cursor = value ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }

    void Flip()
    {
        if (!available) return;
        On = !On;
        if (Toggled != null) Toggled(this, EventArgs.Empty);
    }

    public void SnapAnimation() { knob = on ? 1f : 0f; animation.Stop(); Invalidate(); }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Focus();
        if (chip.Contains(PointToClient(Cursor.Position)) && HotkeyClicked != null) HotkeyClicked(this, EventArgs.Empty);
        else Flip();
    }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Space) Flip(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

    static Color Mix(Color a, Color b, float t)
    {
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Color back = hover && available ? Theme.CardHover : Theme.Card;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.Fill(g, back, new RectangleF(0, 0, Width - 1, Height - 1), Theme.S(6));

        int pad = Theme.S(10);
        int titleHeight = Theme.TitleFont.Height, subHeight = subtitle == null ? 0 : Theme.SubFont.Height;
        int top = (Height - titleHeight - subHeight) / 2;
        TextRenderer.DrawText(g, title, Theme.TitleFont, new Point(pad, top), available ? Theme.Text : Theme.Faint, back, TextFormatFlags.NoPadding);
        if (subtitle != null)
            TextRenderer.DrawText(g, available ? subtitle : unavailable, Theme.SubFont, new Point(pad, top + titleHeight), available ? Theme.Muted : Theme.Faint, back, TextFormatFlags.NoPadding);

        float switchWidth = Theme.S(36), switchHeight = Theme.S(20);
        float switchX = Width - pad - switchWidth, switchY = (Height - switchHeight) / 2f;
        Color track = available ? Mix(Theme.Track, Theme.Accent, knob) : Theme.Track;
        Theme.Fill(g, track, new RectangleF(switchX, switchY, switchWidth, switchHeight), switchHeight / 2);
        float inset = Theme.S(3), size = switchHeight - inset * 2;
        float knobX = switchX + inset + (switchWidth - size - inset * 2) * knob;
        using (SolidBrush brush = new SolidBrush(available ? (knob > 0.5f ? Theme.Back : Theme.Text) : Theme.Faint))
            g.FillEllipse(brush, knobX, switchY + inset, size, size);

        chip = hotkey != null ? Theme.DrawChip(g, hotkey, (int)switchX - Theme.S(10), Height) : Rectangle.Empty;
    }
}

// Looks like ToggleRow, but its right side is a button for a one-off action such as a teleport.
class ActionRow : Control
{
    string title, subtitle, button, hotkey;
    bool hover, available = true;
    Rectangle chip;

    public event EventHandler Activated, HotkeyClicked;

    public ActionRow()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.Card;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    public void SetText(string title, string subtitle, string button)
    {
        this.title = title;
        this.subtitle = subtitle;
        this.button = button;
        Invalidate();
    }

    public string Hotkey
    {
        get { return hotkey; }
        set { hotkey = value; Invalidate(); }
    }

    public bool Available
    {
        get { return available; }
        set { if (available == value) return; available = value; Cursor = value ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }

    void Fire()
    {
        if (available && Activated != null) Activated(this, EventArgs.Empty);
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Focus();
        if (chip.Contains(PointToClient(Cursor.Position)) && HotkeyClicked != null) HotkeyClicked(this, EventArgs.Empty);
        else Fire();
    }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) Fire(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Color back = hover && available ? Theme.CardHover : Theme.Card;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.Fill(g, back, new RectangleF(0, 0, Width - 1, Height - 1), Theme.S(6));

        int pad = Theme.S(10);
        int titleHeight = Theme.TitleFont.Height, subHeight = subtitle == null ? 0 : Theme.SubFont.Height;
        int top = (Height - titleHeight - subHeight) / 2;
        TextRenderer.DrawText(g, title, Theme.TitleFont, new Point(pad, top), available ? Theme.Text : Theme.Faint, back, TextFormatFlags.NoPadding);
        if (subtitle != null)
            TextRenderer.DrawText(g, subtitle, Theme.SubFont, new Point(pad, top + titleHeight), available ? Theme.Muted : Theme.Faint, back, TextFormatFlags.NoPadding);

        Size text = TextRenderer.MeasureText(button ?? "", Theme.ButtonFont, Size.Empty, TextFormatFlags.NoPadding);
        int width = text.Width + Theme.S(24), height = Theme.S(26);
        int x = Width - pad - width, y = (Height - height) / 2;
        Color fill = available ? Theme.Accent : Theme.Track;
        Theme.Fill(g, fill, new RectangleF(x, y, width, height), height / 2f);
        TextRenderer.DrawText(g, button, Theme.ButtonFont, new Rectangle(x, y, width, height), available ? Theme.Back : Theme.Faint, fill, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        chip = hotkey != null ? Theme.DrawChip(g, hotkey, x - Theme.S(10), Height) : Rectangle.Empty;
    }
}

class FlatButton : Control
{
    readonly bool primary;
    bool hover;

    public Color Normal = Theme.Card;

    public FlatButton(bool primary)
    {
        this.primary = primary;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Back;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Color back = !Enabled ? (primary ? Theme.Card : Normal) : primary ? Theme.Accent : hover ? Theme.CardHover : Normal;
        Color fore = !Enabled ? Theme.Faint : primary ? Theme.Back : Theme.Text;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.Fill(g, back, new RectangleF(0, 0, Width - 1, Height - 1), Theme.S(6));
        TextRenderer.DrawText(g, Text, Theme.ButtonFont, ClientRectangle, fore, back, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

class LangBar : Control
{
    int hover = -1;

    public event EventHandler Picked;

    public LangBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
    }

    int SegmentAt(int x)
    {
        return Math.Max(0, Math.Min(Loc.Codes.Length - 1, x * Loc.Codes.Length / Width));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int segment = SegmentAt(e.X);
        if (segment != hover) { hover = segment; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        int segment = SegmentAt(e.X);
        if (segment == Loc.Current) return;
        Loc.Current = segment;
        Preferences.Write("Language", Loc.Codes[segment]);
        Theme.ApplyFonts();
        Invalidate();
        if (Picked != null) Picked(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.Fill(g, Theme.Card, new RectangleF(0, 0, Width - 1, Height - 1), Height / 2f);
        float segment = Width / (float)Loc.Codes.Length;
        for (int i = 0; i < Loc.Codes.Length; i++)
        {
            RectangleF cell = new RectangleF(i * segment + 2, 2, segment - 4, Height - 5);
            Color back = i == Loc.Current ? Theme.Accent : i == hover ? Theme.CardHover : Theme.Card;
            Color fore = i == Loc.Current ? Theme.Back : Theme.Muted;
            Theme.Fill(g, back, cell, cell.Height / 2);
            TextRenderer.DrawText(g, Loc.Labels[i], Theme.LangFont, Rectangle.Round(cell), fore, back, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}

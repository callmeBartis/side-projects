using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace WslTray;

/// One palette, resolved once from the Windows app theme. Everything else in the UI
/// reads colors from here - no hardcoded Color.X scattered across the forms.
///
/// Yaru, the Ubuntu desktop look: near-black greys, a headerbar that is part of the
/// window rather than chrome around it, and Ubuntu orange as the single accent.
public static class Theme
{
    public static bool Dark { get; private set; } = ReadSystemDark();

    public static Color Bg => Dark ? Rgb(0x1E, 0x1E, 0x1E) : Rgb(0xFA, 0xFA, 0xFA);
    public static Color Header => Dark ? Rgb(0x30, 0x30, 0x30) : Rgb(0xEB, 0xEB, 0xEB);
    public static Color Sidebar => Dark ? Rgb(0x26, 0x26, 0x26) : Rgb(0xF2, 0xF2, 0xF2);
    public static Color Surface => Dark ? Rgb(0x2C, 0x2C, 0x2C) : Color.White;
    public static Color SurfaceAlt => Dark ? Rgb(0x3A, 0x3A, 0x3A) : Rgb(0xE6, 0xE6, 0xE6);
    public static Color Border => Dark ? Rgb(0x3D, 0x3D, 0x3D) : Rgb(0xDC, 0xDC, 0xDC);
    public static Color Text => Dark ? Color.White : Rgb(0x2E, 0x2E, 0x2E);
    public static Color Muted => Dark ? Rgb(0xAD, 0xAD, 0xAD) : Rgb(0x6E, 0x6E, 0x6E);

    /// Ubuntu orange. The one saturated colour in the whole app.
    public static Color Accent => Rgb(0xE9, 0x54, 0x20);
    public static Color AccentText => Color.White;

    public static Color Ok => Rgb(0x26, 0xA2, 0x69);
    public static Color Warn => Rgb(0xE5, 0xA5, 0x0A);
    public static Color Crit => Rgb(0xE0, 0x1B, 0x24);

    // Segoe UI Variable Text is the closest thing Windows has to Ubuntu Sans.
    // Ubuntu Mono ships with the Ubuntu font package and is used for readings, where
    // tabular digits stop numbers from jittering between polls.
    private static readonly string UiFamily = Has("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
    private static readonly string MonoFamily = Has("Ubuntu Mono") ? "Ubuntu Mono" : "Consolas";

    public static readonly Font Body = new(UiFamily, 9.5F);
    public static readonly Font BodyBold = new(UiFamily, 9.5F, FontStyle.Bold);
    public static readonly Font Small = new(UiFamily, 8.5F);
    public static readonly Font Title = new(UiFamily, 12F, FontStyle.Bold);
    public static readonly Font Brand = new(UiFamily, 13F, FontStyle.Bold);
    public static readonly Font HeaderTitle = new(UiFamily, 10F, FontStyle.Bold);
    public static readonly Font Mono = new(MonoFamily, 11F);
    public static readonly Font MonoSmall = new(MonoFamily, 10F);

    private static bool Has(string family)
    {
        try
        {
            using var f = new FontFamily(family);
            return true;
        }
        catch { return false; }
    }

    private static Color Rgb(int r, int g, int b) => Color.FromArgb(r, g, b);

    /// Blends a colour onto the surface so tints stay flat instead of translucent -
    /// GDI+ alpha over a repainted parent is a reliable source of smears.
    public static Color Tint(Color c, double alpha, Color? over = null)
    {
        var bg = over ?? Surface;
        return Color.FromArgb(
            (int)(c.R * alpha + bg.R * (1 - alpha)),
            (int)(c.G * alpha + bg.G * (1 - alpha)),
            (int)(c.B * alpha + bg.B * (1 - alpha)));
    }

    public static Color Shift(Color c, double factor) => Color.FromArgb(
        Math.Clamp((int)(c.R * factor), 0, 255),
        Math.Clamp((int)(c.G * factor), 0, 255),
        Math.Clamp((int)(c.B * factor), 0, 255));

    private static bool ReadSystemDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    /// Accent while things are calm, then amber and red as load gets real. Ubuntu orange
    /// and amber sit close together, so the warn step starts late and red is unmistakable.
    public static Color ForLoad(double percent) =>
        percent >= 90 ? Crit : percent >= 80 ? Warn : Accent;
}

public static class Draw
{
    public static GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        if (r.Width <= 0 || r.Height <= 0) { path.AddRectangle(r); return path; }

        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        if (radius <= 0) { path.AddRectangle(r); return path; }

        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRounded(Graphics g, Color color, Rectangle r, int radius)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        using var path = RoundedPath(r, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    /// The colour actually painted behind a control. A Card keeps BackColor at the window
    /// tone and paints its surface in OnPaint, so asking Parent.BackColor inside a card
    /// returns the wrong colour and every child ends up with a visible box behind it.
    public static Color GroundOf(Control c) =>
        c.Parent is Card ? Theme.Surface : (c.Parent?.BackColor ?? Theme.Surface);

    public static void StrokeRounded(Graphics g, Color color, Rectangle r, int radius, float width = 1f)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        using var path = RoundedPath(r, radius);
        using var pen = new Pen(color, width);
        g.DrawPath(pen, path);
    }
}

/// Card surface with rounded corners and a hairline border.
public sealed class Card : Panel
{
    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        Padding = new Padding(18);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Bg);

        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        Draw.FillRounded(g, Theme.Surface, r, 12);
        Draw.StrokeRounded(g, Theme.Border, r, 12);
    }
}

/// The window's own header bar, GNOME style: the title and window buttons live inside
/// the window instead of in a system frame around it. The form itself is borderless.
public sealed class TitleBar : Control
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    // wParam/lParam are pointer sized; declaring them int happens to work for these two
    // constants and silently would not for a message carrying a handle or packed coordinates.
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, nint wParam, nint lParam);

    private const int WmNcLButtonDown = 0xA1;
    private const int HtCaption = 0x2;

    private const int BtnSize = 30;
    private const int BtnGap = 4;
    private const int EdgePad = 10;

    private int _hoverBtn = -1;   // 0 = minimise, 1 = close

    public TitleBar(string title)
    {
        Text = title;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Top;
        Height = 46;
    }

    private Rectangle CloseRect => new(Width - EdgePad - BtnSize, (Height - BtnSize) / 2, BtnSize, BtnSize);
    private Rectangle MinRect => new(CloseRect.X - BtnGap - BtnSize, CloseRect.Y, BtnSize, BtnSize);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hit = CloseRect.Contains(e.Location) ? 1 : MinRect.Contains(e.Location) ? 0 : -1;
        if (hit != _hoverBtn) { _hoverBtn = hit; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hoverBtn != -1) { _hoverBtn = -1; Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        var form = FindForm();
        if (form == null) return;

        // Left button only. Real window chrome does not close on a right-click, and reaching
        // for the context menu on a title bar is a reflex.
        if (e.Button != MouseButtons.Left) { base.OnMouseDown(e); return; }

        if (CloseRect.Contains(e.Location)) { form.Close(); return; }
        if (MinRect.Contains(e.Location)) { form.WindowState = FormWindowState.Minimized; return; }

        // Hand the drag to the window manager so snapping and screen edges still work.
        ReleaseCapture();
        SendMessage(form.Handle, WmNcLButtonDown, HtCaption, IntPtr.Zero);
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Theme.Header);

        using (var pen = new Pen(Theme.Dark ? Theme.Tint(Color.Black, 0.35, Theme.Header) : Theme.Border))
            g.DrawLine(pen, 0, Height - 1, Width, Height - 1);

        TextRenderer.DrawText(g, Text, Theme.HeaderTitle,
            new Rectangle(0, 0, Width, Height), Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        DrawButton(g, MinRect, 0);
        DrawButton(g, CloseRect, 1);
    }

    private void DrawButton(Graphics g, Rectangle r, int index)
    {
        var hovered = _hoverBtn == index;
        var ground = index == 1 && hovered
            ? Theme.Crit
            : hovered ? Theme.Tint(Theme.Text, 0.14, Theme.Header) : Theme.Tint(Theme.Text, 0.07, Theme.Header);

        using (var b = new SolidBrush(ground)) g.FillEllipse(b, r);

        var fg = index == 1 && hovered ? Color.White : Theme.Text;
        using var pen = new Pen(fg, 1.4f);
        var cx = r.X + r.Width / 2;
        var cy = r.Y + r.Height / 2;

        if (index == 0)
        {
            g.DrawLine(pen, cx - 5, cy + 3, cx + 5, cy + 3);
        }
        else
        {
            g.DrawLine(pen, cx - 4, cy - 4, cx + 4, cy + 4);
            g.DrawLine(pen, cx + 4, cy - 4, cx - 4, cy + 4);
        }
    }
}

/// A labelled usage bar: caption left, value right, track underneath.
public sealed class MetricBar : Control
{
    private double _percent;
    private string _caption = "";
    private string _value = "";

    public MetricBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 44;
        Font = Theme.Body;
    }

    public void Set(string caption, double percent, string value)
    {
        if (_caption == caption && Math.Abs(_percent - percent) < 0.05 && _value == value) return;
        _caption = caption;
        _percent = double.IsNaN(percent) ? 0 : Math.Clamp(percent, 0, 100);
        _value = value;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Surface);

        TextRenderer.DrawText(g, _caption, Theme.BodyBold, new Point(-1, 0), Theme.Text);

        var valSize = TextRenderer.MeasureText(g, _value, Theme.Mono);
        TextRenderer.DrawText(g, _value, Theme.Mono,
            new Point(Width - valSize.Width + 1, -1), Theme.Muted);

        const int barH = 6;
        int top = 24;
        Draw.FillRounded(g, Theme.SurfaceAlt, new Rectangle(0, top, Width, barH), barH / 2);

        var w = (int)Math.Round(Width * _percent / 100.0);
        if (w >= barH)
            Draw.FillRounded(g, Theme.ForLoad(_percent), new Rectangle(0, top, w, barH), barH / 2);
        else if (w > 0)
            Draw.FillRounded(g, Theme.ForLoad(_percent), new Rectangle(0, top, barH, barH), barH / 2);
    }
}

/// Sidebar entry: no button chrome, an accent bar and tinted ground when active.
/// Derives from Button so it keeps keyboard handling, and so automation (BM_CLICK)
/// can drive it the same way as any other button.
public sealed class NavItem : Button
{
    private bool _hover;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Active { get; set; }

    public NavItem(string text)
    {
        Text = text;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 36;
        Font = Theme.Body;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Ground comes from the parent: the sidebar has its own tone, and clearing to
        // the card colour instead would draw a visible box behind every entry.
        var ground = Draw.GroundOf(this);
        g.Clear(ground);

        var r = new Rectangle(0, 0, Width - 1, Height - 1);

        if (Active) Draw.FillRounded(g, Theme.Tint(Theme.Accent, 0.22, ground), r, 8);
        else if (_hover) Draw.FillRounded(g, Theme.Tint(Theme.Text, 0.09, ground), r, 8);

        if (Active)
        {
            // Accent rail on the left edge, the giveaway of a network console.
            Draw.FillRounded(g, Theme.Accent, new Rectangle(0, 8, 3, Height - 16), 2);
        }

        // ShowFocusCues: only draw the ring when the user is navigating by keyboard,
        // otherwise a clicked-then-left-behind focus looks like a second active entry.
        if (Focused && ShowFocusCues) Draw.StrokeRounded(g, Theme.Accent, Rectangle.Inflate(r, -1, -1), 8);

        var textColor = Active ? (Theme.Dark ? Color.White : Theme.Accent) : Theme.Text;
        var bounds = new Rectangle(14, 0, Width - 18, Height);
        TextRenderer.DrawText(g, Text, Active ? Theme.BodyBold : Theme.Body, bounds, textColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }
}

/// Flat button with real hover, pressed, focused and disabled states.
public class FlatBtn : Button
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Primary { get; set; }

    /// Overrides the accent for this one button. Destructive actions must not look like
    /// the ordinary primary action sitting next to them.
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color? Tone { get; set; }

    private bool _hover, _down;

    public FlatBtn(string text, bool primary = false)
    {
        Text = text;
        Primary = primary;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = Theme.Body;
        Height = 34;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var ground = Draw.GroundOf(this);
        g.Clear(ground);

        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        var accent = Tone ?? Theme.Accent;
        Color fill, fg;
        Color? stroke = null;

        if (!Enabled)
        {
            fill = Theme.Tint(Theme.Border, 0.35, ground);
            fg = Theme.Muted;
        }
        else if (Primary)
        {
            fill = _down ? Theme.Shift(accent, 0.86)
                 : _hover ? Theme.Shift(accent, 1.14)
                 : accent;
            fg = Theme.AccentText;
        }
        else
        {
            fill = _down ? Theme.SurfaceAlt
                 : _hover ? Theme.Tint(Theme.Border, 0.35, ground)
                 : ground;
            fg = Tone ?? Theme.Text;
            stroke = _hover ? Theme.Tint(accent, 0.45, Theme.Border) : Theme.Border;
        }

        Draw.FillRounded(g, fill, r, 8);
        if (stroke.HasValue) Draw.StrokeRounded(g, stroke.Value, r, 8);
        if (Focused && Enabled && ShowFocusCues)
            Draw.StrokeRounded(g, accent, Rectangle.Inflate(r, -2, -2), 6, 1.4f);

        TextRenderer.DrawText(g, Text, Font, r, fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

/// Number field with its own chrome. NumericUpDown draws system spinner buttons that
/// stay light grey in a dark app and cannot be themed.
public sealed class NumBox : Control
{
    private readonly TextBox _edit;
    private int _value, _min, _max;
    private bool _hoverUp, _hoverDown;

    private const int ArrowW = 20;

    public event Action<int>? ValueChanged;

    public NumBox(int value, int min, int max, int width)
    {
        _min = min;
        _max = max;
        _value = Math.Clamp(value, min, max);

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        // The wrapper is chrome, not a control: without this Tab lands on it first, where
        // there is no caret, no focus ring and no key handling, and typing does nothing.
        SetStyle(ControlStyles.Selectable, false);
        Size = new Size(width, 30);
        BackColor = Theme.SurfaceAlt;

        _edit = new TextBox
        {
            Text = _value.ToString(),
            BorderStyle = BorderStyle.None,
            BackColor = Theme.SurfaceAlt,
            ForeColor = Theme.Text,
            Font = Theme.Body,
            TextAlign = HorizontalAlignment.Right,
            Location = new Point(9, 7),
            Width = width - ArrowW - 18,
        };
        _edit.KeyPress += (s, e) =>
        {
            if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true;
        };
        _edit.LostFocus += (s, e) => Commit();
        _edit.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Up) { Value = _value + 1; e.Handled = true; }
            else if (e.KeyCode == Keys.Down) { Value = _value - 1; e.Handled = true; }
            else if (e.KeyCode == Keys.Enter) { Commit(); e.Handled = true; }
        };
        Controls.Add(_edit);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set
        {
            var v = Math.Clamp(value, _min, _max);
            if (v == _value && _edit.Text == v.ToString()) return;
            _value = v;
            _edit.Text = v.ToString();
            Invalidate();
            ValueChanged?.Invoke(v);
        }
    }

    public void SetRange(int min, int max)
    {
        _min = min;
        _max = max;
        Value = _value;
    }

    private void Commit() => Value = int.TryParse(_edit.Text, out var v) ? v : _value;

    protected override void OnEnabledChanged(EventArgs e)
    {
        _edit.Enabled = Enabled;
        _edit.ForeColor = Enabled ? Theme.Text : Theme.Muted;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (Enabled) { Commit(); Value = _value + Math.Sign(e.Delta); }
        base.OnMouseWheel(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var up = UpRect.Contains(e.Location);
        var down = DownRect.Contains(e.Location);
        if (up != _hoverUp || down != _hoverDown) { _hoverUp = up; _hoverDown = down; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hoverUp = _hoverDown = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (!Enabled) return;

        // Commit first: clicking an arrow does not move focus off the text box, so without
        // this the number the user just typed is thrown away and replaced by old value +/- 1.
        if (UpRect.Contains(e.Location)) { Commit(); Value = _value + 1; }
        else if (DownRect.Contains(e.Location)) { Commit(); Value = _value - 1; }
        else _edit.Focus();
        base.OnMouseDown(e);
    }

    private Rectangle UpRect => new(Width - ArrowW - 1, 1, ArrowW, Height / 2 - 1);
    private Rectangle DownRect => new(Width - ArrowW - 1, Height / 2, ArrowW, Height / 2 - 1);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Draw.GroundOf(this));

        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        var ground = Enabled ? Theme.SurfaceAlt : Theme.Tint(Theme.SurfaceAlt, 0.5);
        _edit.BackColor = ground;

        Draw.FillRounded(g, ground, r, 7);
        Draw.StrokeRounded(g, _edit.Focused ? Theme.Accent : Theme.Border, r, 7);

        var arrow = Enabled ? Theme.Muted : Theme.Tint(Theme.Muted, 0.5, ground);
        DrawArrow(g, UpRect, true, _hoverUp && Enabled ? Theme.Text : arrow);
        DrawArrow(g, DownRect, false, _hoverDown && Enabled ? Theme.Text : arrow);
    }

    private static void DrawArrow(Graphics g, Rectangle area, bool up, Color color)
    {
        var cx = area.X + area.Width / 2;
        var cy = area.Y + area.Height / 2;
        var pts = up
            ? new[] { new Point(cx - 4, cy + 2), new Point(cx + 4, cy + 2), new Point(cx, cy - 3) }
            : new[] { new Point(cx - 4, cy - 2), new Point(cx + 4, cy - 2), new Point(cx, cy + 3) };
        using var b = new SolidBrush(color);
        g.FillPolygon(b, pts);
    }
}

/// Text field with the same chrome as NumBox: rounded surface, a border that turns to
/// the accent on focus, and the row height of a button so a form line stays aligned.
/// A bare TextBox draws a hard system border that cannot be rounded or coloured.
public sealed class Field : Control
{
    private readonly TextBox _edit;

    public Field(int width, string placeholder = "")
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        // Chrome, not a control: Tab belongs on the TextBox inside it.
        SetStyle(ControlStyles.Selectable, false);
        Size = new Size(width, 34);
        BackColor = Theme.SurfaceAlt;

        _edit = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.SurfaceAlt,
            ForeColor = Theme.Text,
            Font = Theme.Body,
            PlaceholderText = placeholder,
            Width = width - 24,
        };
        _edit.Location = new Point(12, (Height - _edit.Height) / 2);
        _edit.GotFocus += (s, e) => Invalidate();
        _edit.LostFocus += (s, e) => Invalidate();
        _edit.TextChanged += (s, e) => OnTextChanged(EventArgs.Empty);
        Controls.Add(_edit);
    }

    [AllowNull]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public override string Text
    {
        get => _edit.Text;
        set => _edit.Text = value;
    }

    public void Clear() => _edit.Clear();

    // The chrome is ours but the caret is the TextBox's, so a click on the padding has
    // to be handed over or the field looks dead at its edges.
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Enabled) _edit.Focus();
        base.OnMouseDown(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        _edit.ForeColor = Enabled ? Theme.Text : Theme.Muted;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Draw.GroundOf(this));

        var ground = Enabled ? Theme.SurfaceAlt : Theme.Tint(Theme.SurfaceAlt, 0.5);
        _edit.BackColor = ground;

        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        Draw.FillRounded(g, ground, r, 8);
        Draw.StrokeRounded(g, _edit.Focused ? Theme.Accent : Theme.Border, r, 8,
            _edit.Focused ? 1.4f : 1f);
    }
}

/// Dropdown in the app's own chrome.
///
/// The ComboBox arrow button is drawn by the shell and stays light grey in a dark app, so
/// the combo is made wider than the panel that holds it and its arrow is clipped away;
/// the chevron drawn on top is ours. The list itself is owner-drawn. Keeping a real
/// ComboBox underneath means keyboard handling and the popup still come from Windows.
public sealed class Picker : Control
{
    private const int ChevronW = 26;

    private readonly Panel _clip;
    private readonly ComboBox _cb;
    private readonly Panel _chevron;
    private bool _hoverChevron;

    public Picker(int width)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        // Chrome, not a control: Tab belongs on the ComboBox inside it.
        SetStyle(ControlStyles.Selectable, false);
        Size = new Size(width, 34);
        BackColor = Theme.SurfaceAlt;

        _cb = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 20,
            BackColor = Theme.SurfaceAlt,
            ForeColor = Theme.Text,
            Font = Theme.Body,
        };

        // Even in flat style the combo draws its own 1px border in a light system colour,
        // so it is inset by 2px on every side and the clip panel cuts all four borders
        // away. The arrow button is not clipped but hidden under the chevron, because the
        // popup is always at least as wide as the combo - widening it to clip the arrow
        // would make the list hang over the field.
        var clipH = _cb.Height - 4;
        _clip = new Panel
        {
            Location = new Point(3, (Height - clipH) / 2),
            Size = new Size(width - 6, clipH),
            BackColor = Theme.SurfaceAlt,
        };
        _cb.Location = new Point(-2, -2);
        _cb.Width = _clip.Width + 4;
        _cb.DrawItem += DrawEntry;
        _cb.GotFocus += (s, e) => Invalidate();
        _cb.LostFocus += (s, e) => Invalidate();
        _clip.Controls.Add(_cb);

        _chevron = new Panel
        {
            Size = new Size(ChevronW, _clip.Height),
            Location = new Point(_clip.Width - ChevronW, 0),
            BackColor = Theme.SurfaceAlt,
            Cursor = Cursors.Hand,
        };
        _chevron.Paint += (s, e) => DrawChevron(e.Graphics, _chevron.ClientRectangle);
        _chevron.MouseEnter += (s, e) => { _hoverChevron = true; _chevron.Invalidate(); };
        _chevron.MouseLeave += (s, e) => { _hoverChevron = false; _chevron.Invalidate(); };
        _chevron.Click += (s, e) => { if (Enabled) { _cb.Focus(); _cb.DroppedDown = true; } };
        _clip.Controls.Add(_chevron);
        _chevron.BringToFront();

        Controls.Add(_clip);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ComboBox.ObjectCollection Items => _cb.Items;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public object? SelectedItem
    {
        get => _cb.SelectedItem;
        set => _cb.SelectedItem = value;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _cb.SelectedIndex;
        set => _cb.SelectedIndex = value;
    }

    public event EventHandler SelectedIndexChanged
    {
        add => _cb.SelectedIndexChanged += value;
        remove => _cb.SelectedIndexChanged -= value;
    }

    private void DrawEntry(object? sender, DrawItemEventArgs e)
    {
        // The closed box must not take the list's highlight, or a focused picker looks
        // like an open menu.
        var closed = (e.State & DrawItemState.ComboBoxEdit) != 0;
        var ground = !closed && (e.State & DrawItemState.Selected) != 0
            ? Theme.Tint(Theme.Accent, 0.30, Theme.SurfaceAlt)
            : Theme.SurfaceAlt;

        using (var bg = new SolidBrush(ground)) e.Graphics.FillRectangle(bg, e.Bounds);
        if (e.Index < 0) return;

        var r = e.Bounds;
        // The closed box sits 2px further left than it looks, so its text needs that back.
        r.X += closed ? 9 : 7;
        r.Width -= closed ? ChevronW + 12 : 10;
        TextRenderer.DrawText(e.Graphics, _cb.Items[e.Index]?.ToString() ?? "", Theme.Body, r,
            Enabled ? Theme.Text : Theme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private void DrawChevron(Graphics g, Rectangle area)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var cx = area.Width / 2;
        var cy = area.Height / 2;
        var color = !Enabled ? Theme.Tint(Theme.Muted, 0.5, _chevron.BackColor)
                  : _hoverChevron ? Theme.Text
                  : Theme.Muted;
        using var pen = new Pen(color, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(pen, new[]
        {
            new PointF(cx - 4, cy - 2),
            new PointF(cx, cy + 2.5f),
            new PointF(cx + 4, cy - 2),
        });
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        _clip.Invalidate(true);
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Draw.GroundOf(this));

        var ground = Enabled ? Theme.SurfaceAlt : Theme.Tint(Theme.SurfaceAlt, 0.5);
        _clip.BackColor = ground;
        _cb.BackColor = ground;
        // The chevron sits on top of the combo, so it keeps its own colour: without this it
        // stayed bright while everything around it dimmed, on every disabled dropdown.
        _chevron.BackColor = ground;

        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        Draw.FillRounded(g, ground, r, 8);
        Draw.StrokeRounded(g, _cb.Focused ? Theme.Accent : Theme.Border, r, 8, _cb.Focused ? 1.4f : 1f);
    }
}

/// Two-value unit picker. A dropdown for exactly two options is overkill, and a
/// ComboBox drags in a system-drawn arrow that will not go dark.
public sealed class UnitToggle : FlatBtn
{
    private readonly string[] _units;
    private int _index;

    public UnitToggle(params string[] units) : base(units[0])
    {
        _units = units;
        Width = 58;
        Click += (s, e) =>
        {
            _index = (_index + 1) % _units.Length;
            Text = _units[_index];
        };
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Unit
    {
        get => _units[_index];
        set
        {
            var i = Array.IndexOf(_units, value);
            if (i < 0) return;
            _index = i;
            Text = _units[i];
        }
    }
}

/// Checkbox with an accent-filled box. The system control paints its tick in the
/// Windows accent colour (blue by default), which fights the app's own accent.
public sealed class Check : CheckBox
{
    private bool _hover;
    private const int Box = 18;

    public Check(string text)
    {
        Text = text;
        AutoSize = false;
        Height = 24;
        Font = Theme.Body;
        ForeColor = Theme.Text;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Width = TextRenderer.MeasureText(text, Theme.Body).Width + Box + 12;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var ground = Draw.GroundOf(this);
        g.Clear(ground);

        var box = new Rectangle(0, (Height - Box) / 2, Box, Box);

        if (Checked)
        {
            Draw.FillRounded(g, Enabled ? Theme.Accent : Theme.Tint(Theme.Accent, 0.4, ground), box, 5);
            using var pen = new Pen(Theme.AccentText, 2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            g.DrawLines(pen, new[]
            {
                new Point(box.X + 4, box.Y + 9),
                new Point(box.X + 7, box.Y + 12),
                new Point(box.X + 14, box.Y + 5),
            });
        }
        else
        {
            Draw.FillRounded(g, _hover && Enabled ? Theme.Tint(Theme.Text, 0.08, ground) : ground, box, 5);
            Draw.StrokeRounded(g, Enabled ? Theme.Border : Theme.Tint(Theme.Border, 0.5, ground), box, 5);
        }

        if (Focused && ShowFocusCues)
            Draw.StrokeRounded(g, Theme.Accent, Rectangle.Inflate(box, 2, 2), 7);

        var textRect = new Rectangle(Box + 10, 0, Width - Box - 10, Height);
        TextRenderer.DrawText(g, Text, Font, textRect, Enabled ? ForeColor : Theme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }
}

public static class UiHelp
{
    /// Anchor.Right does not work inside an AutoScroll panel (the panel's layout width is
    /// virtual), so full-width children get resized explicitly instead.
    public static void StretchToWidth(ScrollableControl root, params Control[] children)
    {
        void Apply()
        {
            var w = root.ClientSize.Width - root.Padding.Horizontal;
            if (root.VerticalScroll.Visible) w -= SystemInformation.VerticalScrollBarWidth;
            if (w < 320) w = 320;

            foreach (var c in children)
            {
                if (c is Label lbl) { lbl.MaximumSize = new Size(w, 0); continue; }
                c.Width = w;
                if (c is ListView lv) FitLastColumn(lv);
            }
        }
        root.Resize += (s, e) => Apply();
        Apply();
    }

    /// Gives the trailing column whatever width is left, so rows never end in dead space.
    public static void FitLastColumn(ListView lv)
    {
        if (lv.Columns.Count == 0) return;
        int used = 0;
        for (int i = 0; i < lv.Columns.Count - 1; i++) used += lv.Columns[i].Width;
        // No margin subtracted, and no minimum: whatever is left over is drawn by the shell
        // as a bright unpainted strip in the header, however narrow it is.
        var rest = lv.ClientSize.Width - used;
        if (rest > 0) lv.Columns[lv.Columns.Count - 1].Width = rest;
    }

    public static Label Lbl(string text, Font? font = null, Color? color = null) => new()
    {
        Text = text,
        Font = font ?? Theme.Body,
        ForeColor = color ?? Theme.Text,
        AutoSize = true,
        BackColor = Color.Transparent,
    };

    public static Card Card() => new();

    /// ListView with owner-drawn header and rows: the shell draws both, and it ignores
    /// every colour we set - a white header and a system-blue selection in a dark app.
    public static ListView List(params (string header, int width)[] cols)
    {
        var lv = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            Font = Theme.Body,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            MultiSelect = false,
            OwnerDraw = true,
            // The only way to set row height on a WinForms ListView.
            SmallImageList = new ImageList { ImageSize = new Size(1, 26) },
        };
        foreach (var (h, w) in cols) lv.Columns.Add(h, w);

        lv.DrawColumnHeader += (s, e) =>
        {
            using (var bg = new SolidBrush(Theme.Surface)) e.Graphics.FillRectangle(bg, e.Bounds);
            using (var pen = new Pen(Theme.Border))
                e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);

            var r = e.Bounds;
            r.X += 10; r.Width -= 10;
            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", Theme.Small, r, Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };

        lv.DrawItem += (s, e) =>
        {
            var selected = e.Item?.Selected == true && lv.Items.Count > 0;
            var ground = selected ? Theme.Tint(Theme.Accent, 0.18) : Theme.Surface;
            using var bg = new SolidBrush(ground);
            e.Graphics.FillRectangle(bg, e.Bounds);

            if (selected)
                Draw.FillRounded(e.Graphics, Theme.Accent, new Rectangle(e.Bounds.X, e.Bounds.Y + 4, 2, e.Bounds.Height - 8), 1);
        };

        lv.DrawSubItem += (s, e) =>
        {
            if (e.Item == null) return;
            var selected = e.Item.Selected;
            var first = e.ColumnIndex == 0;

            // A row carrying only its first cell is a placeholder message, not data.
            var placeholder = e.Item.SubItems.Count == 1;
            var color = placeholder ? Theme.Muted
                      : first ? Theme.Text
                      : (e.SubItem?.Text == "-" ? Theme.Muted : Theme.Text);

            var r = e.Bounds;
            r.X += 10; r.Width -= 12;

            // A running/stopped cell is drawn as a dot: in a list a colour reads faster
            // than a word, and the word stays in the item so the value is still there.
            if (!first && e.SubItem?.Text is "running" or "stopped")
            {
                const int d = 9;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var dot = new SolidBrush(e.SubItem.Text == "running" ? Theme.Ok : Theme.Crit);
                e.Graphics.FillEllipse(dot, r.X + 2, r.Y + (r.Height - d) / 2, d, d);
                return;
            }

            var font = placeholder ? Theme.Body
                     : first ? Theme.BodyBold
                     : Theme.MonoSmall;

            TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? "", font, r,
                selected && first ? Theme.Text : color,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };

        return lv;
    }

    public static NumBox Num(int value, int min, int max, int width = 88) =>
        new(value, min, max, width);

    public static Check Check(string text) => new(text);
}

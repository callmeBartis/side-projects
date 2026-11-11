using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClaudeUsageWidget;

/// A layered child window inside explorer's taskbar, placed left of the tray, the same
/// trick TrafficMonitor uses. Windows 11 has no public API for taskbar widgets.
///
/// Being a child of another process's window attaches our input queue to explorer's, so
/// nothing on this thread may block: a hang here freezes the taskbar too.
internal sealed class Widget : NativeWindow
{
    private const int BaseWidth = 112; // at 96 dpi
    private const int Gap = 8;

    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 2000 };
    private IntPtr _taskbar;
    private (int X, int W, int H) _placed;

    private Usage? _usage;
    private bool _stale;

    public Widget(ContextMenuStrip menu)
    {
        _menu = menu;
        _watch.Tick += (_, _) => EnsureAttached();
        _watch.Start();
        EnsureAttached();
    }

    public void Show(Usage? usage, bool stale)
    {
        _usage = usage;
        _stale = stale;
        Render();
    }

    /// Re-attaches after an explorer restart and follows the tray when it changes width.
    private void EnsureAttached()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return;

        if (taskbar != _taskbar || Handle == IntPtr.Zero)
        {
            if (Handle != IntPtr.Zero) DestroyHandle();
            _taskbar = taskbar;
            _placed = default;
            CreateHandle(new CreateParams
            {
                Parent = taskbar,
                Style = WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
                ExStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            });
        }
        Place();
    }

    private float Scale => GetDpiForWindow(_taskbar) / 96f;

    private void Place()
    {
        GetWindowRect(_taskbar, out var tb);
        var tray = FindWindowEx(_taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        int right = tray != IntPtr.Zero && GetWindowRect(tray, out var tr) ? tr.Left : tb.Right;

        int w = (int)(BaseWidth * Scale), h = tb.Bottom - tb.Top;
        var target = (X: right - tb.Left - w - (int)(Gap * Scale), W: w, H: h);
        bool moved = target != _placed;
        _placed = target;

        // HWND_TOP every tick: the XAML content bridge covers the whole taskbar and
        // explorer restacks it now and then.
        SetWindowPos(Handle, HWND_TOP, target.X, 0, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        if (moved) Render();
    }

    private static bool LightTaskbar() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is 1;

    private static Color FillFor(double pct) =>
        pct >= 95 ? Color.FromArgb(229, 72, 77) :
        pct >= 80 ? Color.FromArgb(242, 179, 61) :
        Color.FromArgb(217, 119, 87); // Claude orange

    private void Render()
    {
        if (Handle == IntPtr.Zero || _placed.W == 0) return;
        var (_, w, h) = _placed;
        float s = Scale;

        using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            // Alpha 1, not 0: fully transparent pixels of a layered window let clicks through.
            g.Clear(Color.FromArgb(1, 0, 0, 0));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Grayscale AA: ClearType needs an opaque background and fringes on alpha.
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            var fg = LightTaskbar() ? Color.FromArgb(_stale ? 120 : 230, 0, 0, 0) : Color.FromArgb(_stale ? 120 : 240, 255, 255, 255);
            var dim = Color.FromArgb(fg.A * 2 / 3, fg);
            using var font = new Font("Segoe UI", 11f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            using var bold = new Font("Segoe UI Semibold", 12f * s, FontStyle.Regular, GraphicsUnit.Pixel);

            if (_usage == null)
            {
                using var b = new SolidBrush(dim);
                g.DrawString(_stale ? "Keine Daten" : "Claude …", bold, b, new RectangleF(0, 0, w, h), new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
            }
            else
            {
                Row(g, "5h", _usage.Session.Percent, h * 0.29f, w, s, fg, dim, font, bold);
                Row(g, "7d", _usage.Weekly.Percent, h * 0.71f, w, s, fg, dim, font, bold);
            }
        }
        Present(bmp);
    }

    private void Row(Graphics g, string label, double pct, float cy, int w, float s, Color fg, Color dim, Font font, Font bold)
    {
        var mid = new StringFormat { LineAlignment = StringAlignment.Center };
        using var dimBrush = new SolidBrush(dim);
        using var fgBrush = new SolidBrush(fg);
        g.DrawString(label, font, dimBrush, new RectangleF(2 * s, cy - 10 * s, 20 * s, 20 * s), mid);

        float bx = 24 * s, bw = 46 * s, bh = 4 * s, by = cy - bh / 2;
        using (var track = new SolidBrush(Color.FromArgb(fg.A / 5, fg))) FillPill(g, track, bx, by, bw, bh);
        var fill = Math.Clamp(pct, 0, 100) / 100 * bw;
        if (fill > 0)
            using (var b = new SolidBrush(Color.FromArgb(_stale ? 120 : 255, FillFor(pct)))) FillPill(g, b, bx, by, (float)Math.Max(fill, bh), bh);

        g.DrawString($"{pct:0}%", bold, fgBrush, new RectangleF(bx + bw, cy - 10 * s, w - bx - bw - 2 * s, 20 * s),
            new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center });
    }

    private static void FillPill(Graphics g, Brush b, float x, float y, float w, float h)
    {
        using var p = new GraphicsPath();
        p.AddArc(x, y, h, h, 90, 180);
        p.AddArc(x + w - h, y, h, h, 270, 180);
        p.CloseFigure();
        g.FillPath(b, p);
    }

    private void Present(Bitmap bmp)
    {
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var hbmp = bmp.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(mem, hbmp);
        try
        {
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
            UpdateLayeredWindow(Handle, screen, IntPtr.Zero, ref size, mem, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(hbmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_LBUTTONUP:
            case WM_RBUTTONUP:
                // Without foreground the menu would not close on an outside click.
                SetForegroundWindow(Handle);
                _menu.Show(Cursor.Position, ToolStripDropDownDirection.AboveLeft);
                return;
            case WM_MOUSEACTIVATE:
                m.Result = MA_NOACTIVATE;
                return;
        }
        base.WndProc(ref m);
    }

    private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000;
    private const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_MOUSEACTIVATE = 0x0021;
    private static readonly IntPtr MA_NOACTIVATE = 3, HWND_TOP = IntPtr.Zero;
    private const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

    [StructLayout(LayoutKind.Sequential)] private record struct RECT(int Left, int Top, int Right, int Bottom);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, IntPtr pptDst, ref SIZE size, IntPtr src, ref POINT pptSrc, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}

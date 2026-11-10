using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace IScreen.Tray;

static class TrayIcons
{
    /// <summary>A small monitor glyph: blue while waiting, green while a Mac is connected.</summary>
    public static Icon Create(bool connected)
    {
        var accent = connected ? Color.FromArgb(34, 170, 90) : Color.FromArgb(52, 120, 230);
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(accent);
            using (var body = RoundedRect(new RectangleF(1, 3, 30, 21), 4)) g.FillPath(fill, body);
            g.FillRectangle(Brushes.White, 5, 7, 22, 13);
            g.FillRectangle(fill, 13, 24, 6, 3);
            using (var foot = RoundedRect(new RectangleF(8, 27, 16, 3), 1.5f)) g.FillPath(fill, foot);
            // The "i" of iScreen.
            g.FillEllipse(fill, 14.5f, 8.5f, 3, 3);
            g.FillRectangle(fill, 14.5f, 12.5f, 3, 6);
        }
        // Two icons for the life of the process: the handle is intentionally not destroyed.
        return Icon.FromHandle(bitmap.GetHicon());
    }

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>"Start with Windows" via the per-user Run key (no admin rights needed).</summary>
static class AutoStart
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "iScreen";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string command &&
                   command.Contains(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(ValueName, $"\"{Application.ExecutablePath}\"");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}

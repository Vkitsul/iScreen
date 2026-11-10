using System.Runtime.InteropServices;

namespace IScreen.Host.Tools;

/// <summary>Synthetic desktop-like image: ClearType text in saturated colours, 1px stripes, gradients.</summary>
public static partial class TestPattern
{
    public readonly record struct Region(string Name, int X, int Y, int W, int H);

    public static (byte[] Bgra, Region[] Regions) Create(int width, int height)
    {
        var bgra = new byte[width * height * 4];
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        var bmi = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        IntPtr dib = CreateDIBSection(dc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr oldBmp = SelectObject(dc, dib);
        SetBkMode(dc, 1); // transparent

        int half = height / 2;
        Fill(dc, 0, 0, width, half, Rgb(255, 255, 255));
        Fill(dc, 0, half, width, height - half, Rgb(30, 30, 30));

        uint[] onLight = [Rgb(0, 0, 0), Rgb(220, 0, 0), Rgb(0, 70, 230), Rgb(0, 140, 0), Rgb(200, 0, 200), Rgb(230, 120, 0)];
        uint[] onDark = [Rgb(255, 255, 255), Rgb(0, 220, 255), Rgb(255, 220, 0), Rgb(255, 80, 160), Rgb(120, 180, 255), Rgb(80, 255, 80)];
        string[] fonts = ["Segoe UI", "Consolas", "Segoe UI"];
        int[] sizes = [-15, -13, -11];

        int textW = Math.Min(width / 2, 1100);
        int y = 10;
        for (int f = 0; f < fonts.Length; f++)
        {
            IntPtr font = CreateFontW(sizes[f], 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5 /*CLEARTYPE*/, 0, fonts[f]);
            IntPtr oldFont = SelectObject(dc, font);
            foreach (var c in onLight)
            {
                SetTextColor(dc, c);
                TextOutW(dc, 10, y, Sample, Sample.Length);
                y += -sizes[f] + 6;
            }
            SelectObject(dc, oldFont);
            DeleteObject(font);
        }
        int lightTextBottom = y;

        y = half + 10;
        for (int f = 0; f < fonts.Length; f++)
        {
            IntPtr font = CreateFontW(sizes[f], 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, fonts[f]);
            IntPtr oldFont = SelectObject(dc, font);
            foreach (var c in onDark)
            {
                SetTextColor(dc, c);
                TextOutW(dc, 10, y, Sample, Sample.Length);
                y += -sizes[f] + 6;
            }
            SelectObject(dc, oldFont);
            DeleteObject(font);
        }
        int darkTextBottom = y;

        GdiFlush();
        Marshal.Copy(bits, bgra, 0, bgra.Length);
        SelectObject(dc, oldBmp);
        DeleteObject(dib);
        DeleteDC(dc);

        // Pixel-level patterns drawn directly.
        int px = textW + 40, pw = Math.Min(400, width - px - 10), ph = 120;
        for (int yy = 20; yy < 20 + ph; yy++)
        for (int xx = px; xx < px + pw; xx++)
            Set(bgra, width, xx, yy, (xx & 1) == 0 ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)255));
        for (int yy = 160; yy < 160 + ph; yy++)
        for (int xx = px; xx < px + pw; xx++)
            Set(bgra, width, xx, yy, ((xx ^ yy) & 1) == 0 ? ((byte)0, (byte)255, (byte)0) : ((byte)255, (byte)0, (byte)255));
        for (int yy = 300; yy < 300 + ph; yy++)
        for (int xx = px; xx < px + pw; xx++)
        {
            float t = (xx - px) / (float)pw;
            var (r, g, b) = Hue(t);
            float s = (yy - 300) / (float)ph;
            Set(bgra, width, xx, yy, ((byte)(r * (1 - s) + 128 * s), (byte)(g * (1 - s) + 128 * s), (byte)(b * (1 - s) + 128 * s)));
        }

        for (int i = 0; i < bgra.Length; i += 4) bgra[i + 3] = 255;

        var regions = new[]
        {
            new Region("text on light", 0, 0, textW, Math.Min(lightTextBottom, half)),
            new Region("text on dark", 0, half, textW, Math.Min(darkTextBottom, height) - half),
            new Region("1px red/blue stripes", px, 20, pw, ph),
            new Region("1px green/magenta checker", px, 160, pw, ph),
            new Region("hue gradient", px, 300, pw, ph),
            new Region("whole frame", 0, 0, width, height),
        };
        return (bgra, regions);
    }

    const string Sample = "The quick brown fox 0123456789 — Ãéìòù ¶ €$ {}[]() Lorem ipsum dolor sit amet, colori fedeli.";

    static void Set(byte[] bgra, int width, int x, int y, (byte R, byte G, byte B) c)
    {
        int o = (y * width + x) * 4;
        bgra[o] = c.B; bgra[o + 1] = c.G; bgra[o + 2] = c.R;
    }

    static (byte, byte, byte) Hue(float t)
    {
        float h = t * 6;
        float x = 1 - Math.Abs(h % 2 - 1);
        var (r, g, b) = (int)h switch
        {
            0 => (1f, x, 0f), 1 => (x, 1f, 0f), 2 => (0f, 1f, x),
            3 => (0f, x, 1f), 4 => (x, 0f, 1f), _ => (1f, 0f, x),
        };
        return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    static uint Rgb(byte r, byte g, byte b) => (uint)(r | g << 8 | b << 16);

    static void Fill(IntPtr dc, int x, int y, int w, int h, uint color)
    {
        IntPtr brush = CreateSolidBrush(color);
        var rect = new RECT { left = x, top = y, right = x + w, bottom = y + h };
        FillRect(dc, ref rect, brush);
        DeleteObject(brush);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int left, top, right, bottom; }

    [LibraryImport("gdi32.dll")] private static partial IntPtr CreateCompatibleDC(IntPtr hdc);
    [LibraryImport("gdi32.dll")] private static partial IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [LibraryImport("gdi32.dll")] private static partial IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(IntPtr obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteDC(IntPtr hdc);
    [LibraryImport("gdi32.dll")] private static partial int SetBkMode(IntPtr hdc, int mode);
    [LibraryImport("gdi32.dll")] private static partial uint SetTextColor(IntPtr hdc, uint color);
    [LibraryImport("gdi32.dll")] private static partial IntPtr CreateSolidBrush(uint color);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GdiFlush();
    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TextOutW(IntPtr hdc, int x, int y, string text, int length);
    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision,
        uint quality, uint pitchAndFamily, string face);
    [LibraryImport("user32.dll")] private static partial int FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);
}

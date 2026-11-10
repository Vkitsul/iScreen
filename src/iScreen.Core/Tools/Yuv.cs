namespace IScreen.Host.Tools;

/// <summary>CPU reference for the client-side reconstruction (mirrors the Metal shader).</summary>
public static unsafe class Yuv
{
    /// <summary>Decoded YUV420P planes of the encoded frame -> RGBA8 of the source size.</summary>
    public static void ToRgba(byte* yP, int ys, byte* uP, int us, byte* vP, int vs, FrameLayout L, byte[] rgba)
    {
        int W = L.Width, H = L.Height, A = L.AuxOffset;
        Parallel.For(0, H, y =>
        {
            for (int x = 0; x < W; x++)
            {
                float Y = yP[y * ys + x] / 255f, U, V;
                if (L.Mode == StreamMode.Yuv444Packed)
                {
                    // Cr is the aux luma; Cb's four polyphase components sit in the chroma planes.
                    V = yP[(A + y) * ys + x] / 255f;
                    int row = (y & 1) == 0 ? y / 2 : A / 2 + y / 2;
                    U = ((x & 1) == 0 ? uP[row * us + x / 2] : vP[row * vs + x / 2]) / 255f;
                }
                else
                {
                    // Bilinear, centre-sited chroma (what a GPU sampler does with normalized coords).
                    U = Bilinear(uP, us, W / 2, H / 2, (x - 0.5f) / 2, (y - 0.5f) / 2);
                    V = Bilinear(vP, vs, W / 2, H / 2, (x - 0.5f) / 2, (y - 0.5f) / 2);
                }
                float cb = U - 0.5f, cr = V - 0.5f;
                float R = Y + 1.5748f * cr;
                float G = Y - 0.187324f * cb - 0.468124f * cr;
                float B = Y + 1.8556f * cb;
                int o = (y * W + x) * 4;
                rgba[o] = ToByte(R);
                rgba[o + 1] = ToByte(G);
                rgba[o + 2] = ToByte(B);
                rgba[o + 3] = 255;
            }
        });
    }

    static float Bilinear(byte* p, int stride, int w, int h, float fx, float fy)
    {
        fx = Math.Clamp(fx, 0, w - 1);
        fy = Math.Clamp(fy, 0, h - 1);
        int x0 = (int)fx, y0 = (int)fy;
        int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float ax = fx - x0, ay = fy - y0;
        float top = p[y0 * stride + x0] * (1 - ax) + p[y0 * stride + x1] * ax;
        float bottom = p[y1 * stride + x0] * (1 - ax) + p[y1 * stride + x1] * ax;
        return (top * (1 - ay) + bottom * ay) / 255f;
    }

    static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    /// <summary>PSNR (dB) and max abs error between a BGRA reference and an RGBA result, inside a rectangle.</summary>
    public static (double Psnr, int MaxErr) Compare(byte[] bgraRef, byte[] rgba, int width, int x0, int y0, int w, int h)
    {
        double se = 0;
        int maxErr = 0;
        for (int y = y0; y < y0 + h; y++)
        for (int x = x0; x < x0 + w; x++)
        {
            int o = (y * width + x) * 4;
            for (int c = 0; c < 3; c++)
            {
                int d = bgraRef[o + 2 - c] - rgba[o + c];
                se += d * d;
                if (Math.Abs(d) > maxErr) maxErr = Math.Abs(d);
            }
        }
        double mse = se / (w * (double)h * 3);
        return (mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse), maxErr);
    }
}

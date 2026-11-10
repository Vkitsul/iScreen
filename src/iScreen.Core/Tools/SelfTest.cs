using System.Diagnostics;
using IScreen.Host.Capture;
using IScreen.Host.Encode;
using IScreen.Host.Gpu;
using Vortice.Direct3D11;
using Vortice.DXGI;
using AVPixelFormat = FFmpeg.AutoGen.AVPixelFormat;

namespace IScreen.Host.Tools;

/// <summary>
/// Capture/pattern -> GPU convert -> AMF encode -> software decode -> CPU unpack, then compares with the
/// source. Checks the colour pipeline and the 4:4:4 packing without any client.
/// </summary>
public static unsafe class SelfTest
{
    sealed record ModeResult(StreamMode Mode, byte[] Rgba, List<(string, double, int)> Scores,
        double AvgMs, double MaxMs, int FirstBytes, double RestAvgBytes, int Keyframes, int LaggedPackets,
        (double Psnr, int MaxErr) FirstFrame);

    static bool pace;
    static int scrollStep;

    public static int Run(int monitorIndex, bool fromScreen, int frames, string? saveDir, EncoderSettings settings, bool paced = false)
    {
        pace = paced;
        scrollStep = int.TryParse(Environment.GetEnvironmentVariable("ISCREEN_SCROLL"), out int s) ? s : 0;
        var monitors = Monitors.Enumerate();
        try
        {
            if (monitorIndex < 0 || monitorIndex >= monitors.Count)
            {
                Console.WriteLine($"No monitor {monitorIndex}; run 'list'.");
                return 1;
            }
            var (info, adapter, output) = monitors[monitorIndex];
            Console.WriteLine($"Monitor: {info}");
            var (device, context) = GpuDevice.Create(adapter);

            byte[] reference;
            int width, height;
            TestPattern.Region[] regions;
            ID3D11Texture2D sourceTexture;

            if (fromScreen)
            {
                using var capture = new DesktopCapture(device, context, output);
                var sw = Stopwatch.StartNew();
                while (capture.Acquire(100, out _) != DesktopCapture.Status.NewFrame || !capture.HasFrame)
                    if (sw.ElapsedMilliseconds > 3000) throw new TimeoutException("No frame from Desktop Duplication");
                width = capture.Width;
                height = capture.Height;
                sourceTexture = CopyTexture(device, context, capture.FrameTexture!);
                reference = Readback(device, context, sourceTexture, width, height);
                regions = [new("whole frame", 0, 0, width, height)];
            }
            else
            {
                width = info.Width;
                height = info.Height;
                (reference, regions) = TestPattern.Create(width, height);
                sourceTexture = Upload(device, reference, width, height);
            }
            using var srv = device.CreateShaderResourceView(sourceTexture);

            Console.WriteLine($"Source {width}x{height}, {frames} frames per mode, {settings.Codec} {settings.BitrateKbps / 1000} Mbps rc={settings.RateControl} quality={settings.Quality}");
            var sleepWatch = Stopwatch.StartNew();
            for (int i = 0; i < 20; i++) Thread.Sleep(1);
            Console.WriteLine($"Sleep(1) takes {sleepWatch.Elapsed.TotalMilliseconds / 20:0.00} ms in this process");
            var results = new List<ModeResult>();
            foreach (var mode in new[] { StreamMode.Yuv420, StreamMode.Yuv444Packed })
                results.Add(RunMode(device, context, srv, reference, width, height, regions, mode, frames, settings));

            Console.WriteLine();
            Console.WriteLine($"{"region",-28} {"4:2:0 PSNR",11} {"4:4:4 PSNR",11} {"maxErr 420/444",15}");
            for (int i = 0; i < regions.Length; i++)
            {
                var a = results[0].Scores[i];
                var b = results[1].Scores[i];
                Console.WriteLine($"{a.Item1,-28} {a.Item2,9:0.00}dB {b.Item2,9:0.00}dB {a.Item3,8}/{b.Item3}");
            }

            if (saveDir is not null)
            {
                Directory.CreateDirectory(saveDir);
                SaveComparison(Path.Combine(saveDir, "compare.png"), reference, results[0].Rgba, results[1].Rgba,
                    width, height, regions.Where(r => r.Name != "whole frame").Take(3).DefaultIfEmpty(regions[0]).ToArray());
                Console.WriteLine($"Saved {Path.Combine(saveDir, "compare.png")} (per crop: original / 4:2:0 / 4:4:4, 2x zoom)");
            }

            srv.Dispose();
            sourceTexture.Dispose();
            context.Dispose();
            device.Dispose();
            return 0;
        }
        finally
        {
            Monitors.DisposeAll(monitors);
        }
    }

    static ModeResult RunMode(ID3D11Device device, ID3D11DeviceContext context, ID3D11ShaderResourceView srv,
        byte[] reference, int width, int height, TestPattern.Region[] regions, StreamMode mode, int frames,
        EncoderSettings settings)
    {
        var layout = FrameLayout.Create(width, height, mode);
        Console.WriteLine();
        Console.WriteLine($"== {mode}: encoded frame {layout.EncWidth}x{layout.EncHeight}" +
                          (mode == StreamMode.Yuv444Packed ? $", aux offset {layout.AuxOffset}" : ""));

        using var converter = new FrameConverter(device, context);
        using var encoder = FrameEncoders.Create(device, layout, settings with { Verbose = true });
        Console.WriteLine($"  applied: {string.Join(" ", encoder.AppliedOptions)}");
        using var decoder = new SoftwareDecoder();

        var rgba = new byte[width * height * 4];
        var packets = new List<EncodedPacket>();
        var times = new List<double>();
        int decoded = 0, keyframes = 0, lagged = 0, firstBytes = 0;
        long restBytes = 0;
        (double, int) first = default;

        void Consume(int frameIndex)
        {
            foreach (var p in packets)
            {
                if (frameIndex >= 0 && p.Pts != frameIndex) lagged++;
                if (p.Key) keyframes++;
                if (p.Pts == 0 || p.Key)
                    Console.WriteLine($"  frame {p.Pts}: NAL types {string.Join(",", HevcEncoder.NalTypes(p.Span))} (19/20 = IDR, 21 = CRA)");
                if (p.Pts == 0) firstBytes = p.Length; else restBytes += p.Length;
                decoder.Decode(p.Span, f =>
                {
                    if (f->format != (int)AVPixelFormat.AV_PIX_FMT_YUV420P && f->format != (int)AVPixelFormat.AV_PIX_FMT_YUVJ420P)
                        throw new InvalidOperationException($"Unexpected decoded format {(AVPixelFormat)f->format}");
                    Yuv.ToRgba(f->data[0], f->linesize[0], f->data[1], f->linesize[1], f->data[2], f->linesize[2], layout, rgba);
                    if (decoded++ == 0)
                    {
                        first = Yuv.Compare(reference, rgba, width, 0, 0, width, height);
                        Console.WriteLine($"  bitstream VUI: range={f->color_range} matrix={f->colorspace} primaries={f->color_primaries} trc={f->color_trc}");
                    }
                });
                p.Return();
            }
            packets.Clear();
        }

        double convertMs = 0;
        for (int i = 0; i < frames; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            IntPtr texture = encoder.NextTexture();
            converter.Convert(srv, texture, 0, layout, i * scrollStep);
            if (i > 0) convertMs += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            encoder.Encode(false, packets);
            times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            Consume(i);
            if (settings.Fps > 0 && pace) Thread.Sleep(1000 / settings.Fps);
        }
        Console.WriteLine($"  convert (CPU submit + flush) avg {convertMs / Math.Max(1, frames - 1):0.00} ms");
        // Both encoders return each frame before the next is submitted: nothing left to drain.
        Consume(-1);

        var scores = regions.Select(r =>
        {
            var (psnr, maxErr) = Yuv.Compare(reference, rgba, width, r.X, r.Y, r.W, r.H);
            return (r.Name, psnr, maxErr);
        }).ToList();

        var steady = times.Skip(1).DefaultIfEmpty(0).ToList();
        Console.WriteLine($"  frames decoded {decoded}/{frames}, keyframes {keyframes}, packets not matching their frame: {lagged}");
        Console.WriteLine($"  submit->packet: first {times[0]:0.0} ms, then avg {steady.Average():0.00} ms, max {steady.Max():0.00} ms");
        Console.WriteLine($"  IDR {firstBytes / 1024.0:0} KiB, following frames avg {restBytes / Math.Max(1.0, frames - 1) / 1024.0:0.0} KiB");
        Console.WriteLine($"  first frame PSNR {first.Item1:0.00} dB, after {frames} frames {scores[^1].psnr:0.00} dB");

        return new ModeResult(mode, rgba, scores, steady.Average(), steady.Max(), firstBytes,
            restBytes / Math.Max(1.0, frames - 1), keyframes, lagged, first);
    }

    static ID3D11Texture2D Upload(ID3D11Device device, byte[] bgra, int width, int height)
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
        };
        fixed (byte* p = bgra)
            return device.CreateTexture2D(desc, [new SubresourceData((IntPtr)p, (uint)width * 4)]);
    }

    static ID3D11Texture2D CopyTexture(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D src)
    {
        var desc = src.Description;
        desc.BindFlags = BindFlags.ShaderResource;
        var copy = device.CreateTexture2D(desc);
        context.CopyResource(copy, src);
        return copy;
    }

    static byte[] Readback(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D src, int width, int height)
    {
        var desc = src.Description;
        desc.Usage = ResourceUsage.Staging;
        desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        using var staging = device.CreateTexture2D(desc);
        context.CopyResource(staging, src);
        var map = context.Map(staging, 0, MapMode.Read);
        var result = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            new ReadOnlySpan<byte>((byte*)map.DataPointer + y * map.RowPitch, width * 4)
                .CopyTo(result.AsSpan(y * width * 4));
        context.Unmap(staging, 0);
        return result;
    }

    static void SaveComparison(string path, byte[] reference, byte[] rgba420, byte[] rgba444, int width, int height,
        TestPattern.Region[] crops)
    {
        const int Zoom = 2, Gap = 6;
        int cw = Math.Min(560, crops.Min(c => c.W)), ch = Math.Min(110, crops.Min(c => c.H));
        int outW = cw * Zoom, panelH = ch * Zoom;
        int outH = crops.Length * (3 * panelH + 2 * Gap) + (crops.Length - 1) * Gap * 3;
        var img = new byte[outW * outH * 4];
        for (int i = 0; i < img.Length; i += 4) { img[i] = 128; img[i + 1] = 128; img[i + 2] = 128; img[i + 3] = 255; }

        int oy = 0;
        foreach (var crop in crops)
        {
            for (int panel = 0; panel < 3; panel++)
            {
                for (int y = 0; y < panelH; y++)
                for (int x = 0; x < outW; x++)
                {
                    int sx = crop.X + x / Zoom, sy = crop.Y + y / Zoom;
                    int s = (sy * width + sx) * 4, d = ((oy + y) * outW + x) * 4;
                    if (panel == 0) { img[d] = reference[s + 2]; img[d + 1] = reference[s + 1]; img[d + 2] = reference[s]; }
                    else
                    {
                        var src = panel == 1 ? rgba420 : rgba444;
                        img[d] = src[s]; img[d + 1] = src[s + 1]; img[d + 2] = src[s + 2];
                    }
                }
                oy += panelH + Gap;
            }
            oy += Gap * 2;
        }
        Png.Write(path, img, outW, Math.Min(outH, oy));
    }
}

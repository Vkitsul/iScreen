using FFmpeg.AutoGen;
using IScreen.Host.Encode;

namespace IScreen.Host.Tools;

/// <summary>FFmpeg software HEVC decoder, used by the self-test and the Windows test client.</summary>
public sealed unsafe class SoftwareDecoder : IDisposable
{
    AVCodecContext* _ctx;
    AVPacket* _pkt;
    AVFrame* _frame;

    public delegate void FrameHandler(AVFrame* frame);

    public SoftwareDecoder()
    {
        HevcEncoder.LoadFFmpeg();
        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_HEVC);
        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        _ctx->thread_count = 1; // frame threading would add a frame of delay
        _ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        int r = ffmpeg.avcodec_open2(_ctx, codec, null);
        if (r < 0) throw new InvalidOperationException("decoder open failed: " + HevcEncoder.Error(r));
        _pkt = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
    }

    /// <summary>Decodes one access unit; returns false if the decoder rejected it.</summary>
    public bool Decode(ReadOnlySpan<byte> data, FrameHandler onFrame)
    {
        fixed (byte* p = data)
        {
            _pkt->data = p;
            _pkt->size = data.Length;
            int r = ffmpeg.avcodec_send_packet(_ctx, _pkt);
            _pkt->data = null;
            _pkt->size = 0;
            if (r < 0) return false;
        }
        while (ffmpeg.avcodec_receive_frame(_ctx, _frame) == 0)
        {
            onFrame(_frame);
            ffmpeg.av_frame_unref(_frame);
        }
        return true;
    }

    public void Dispose()
    {
        if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; }
        if (_ctx != null) { var c = _ctx; ffmpeg.avcodec_free_context(&c); _ctx = null; }
    }
}

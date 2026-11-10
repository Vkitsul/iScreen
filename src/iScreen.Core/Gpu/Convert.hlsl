// RGB -> YCbCr (BT.709, full range) written into the two planes of an NV12 texture.
// Mode 0: plain 4:2:0 (chroma = 2x2 average, centre-sited).
// Mode 1: 4:4:4 packed into a 4:2:0 frame twice as tall (layout in docs/ARCHITETTURA.md):
//   luma   [0, H)         Y
//   luma   [A, A+H)       Cr, full resolution
//   chroma [0, H/2)       (Cb[2i, 2j],   Cb[2i+1, 2j])
//   chroma [A/2, A/2+H/2) (Cb[2i, 2j+1], Cb[2i+1, 2j+1])
// Every component then moves with the same motion vector as the picture, so scrolling stays cheap.

Texture2D<float4> Src : register(t0);

cbuffer Params : register(b0)
{
    uint Width;     // source width
    uint Height;    // source height
    uint AuxOffset; // first luma row of the aux half (A); unused in mode 0
    uint Mode;
    uint ScrollY;   // test pattern only: rows to scroll the source by (wraps)
    uint3 Pad;
};

static const float3 KY = float3(0.2126, 0.7152, 0.0722);

float3 Rgb(int2 p)
{
    p = clamp(p, int2(0, 0), int2((int)Width - 1, (int)Height - 1));
    p.y = (p.y + (int)ScrollY) % (int)Height;
    return Src.Load(int3(p, 0)).rgb;
}

float LumaOf(float3 c) { return dot(KY, c); }

float2 ChromaOf(float3 c)
{
    float y = LumaOf(c);
    return float2((c.b - y) / 1.8556 + 0.5, (c.r - y) / 1.5748 + 0.5);
}

float Cb(int x, int y) { return ChromaOf(Rgb(int2(x, y))).x; }
float Cr(int x, int y) { return ChromaOf(Rgb(int2(x, y))).y; }

float4 VSMain(uint id : SV_VertexID) : SV_Position
{
    float2 uv = float2((id << 1) & 2, id & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

// Bound to an R8_UNORM view of plane 0; viewport covers the whole encoded luma plane.
float PSLuma(float4 pos : SV_Position) : SV_Target
{
    int x = (int)pos.x;
    int y = (int)pos.y;
    int H = (int)Height;
    if (Mode == 0 || y < H)
        return LumaOf(Rgb(int2(x, y)));

    int A = (int)AuxOffset;
    if (y < A)
    {
        // Gap between halves: replicate neighbours so in-loop filters see smooth content.
        return y < H + (A - H) / 2 ? LumaOf(Rgb(int2(x, H - 1))) : Cr(x, 0);
    }
    return Cr(x, min(y - A, H - 1));
}

// Bound to an R8G8_UNORM view of plane 1; viewport is half the encoded size.
float2 PSChroma(float4 pos : SV_Position) : SV_Target
{
    int i = (int)pos.x;
    int j = (int)pos.y;

    if (Mode == 0)
    {
        int2 p = int2(2 * i, 2 * j);
        float2 c = ChromaOf(Rgb(p)) + ChromaOf(Rgb(p + int2(1, 0)))
                 + ChromaOf(Rgb(p + int2(0, 1))) + ChromaOf(Rgb(p + int2(1, 1)));
        return c * 0.25;
    }

    int halfH = (int)Height / 2;
    int halfA = (int)AuxOffset / 2;
    int row; // source row of the Cb samples stored in this chroma row
    if (j < halfH)
        row = 2 * j;
    else if (j < halfA)
        row = j < halfH + (halfA - halfH) / 2 ? 2 * (halfH - 1) : 1;
    else
        row = 2 * min(j - halfA, halfH - 1) + 1;

    return float2(Cb(2 * i, row), Cb(2 * i + 1, row));
}

using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace IScreen.Host.Gpu;

/// <summary>Converts a BGRA desktop texture into an NV12 texture (plain 4:2:0 or packed 4:4:4).</summary>
public sealed class FrameConverter : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct Params
    {
        public uint Width, Height, AuxOffset, Mode, ScrollY, Pad0, Pad1, Pad2;
    }

    readonly ID3D11Device _device;
    readonly ID3D11DeviceContext _context;
    readonly ID3D11VertexShader _vs;
    readonly ID3D11PixelShader _psLuma;
    readonly ID3D11PixelShader _psChroma;
    readonly ID3D11Buffer _params;
    readonly Dictionary<(IntPtr, uint), (ID3D11RenderTargetView Y, ID3D11RenderTargetView UV)> _targets = new();
    Params _current;

    public FrameConverter(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;

        using var stream = typeof(FrameConverter).Assembly.GetManifestResourceStream("Convert.hlsl")!;
        string source = new StreamReader(stream).ReadToEnd();
        _vs = device.CreateVertexShader(Compile(source, "VSMain", "vs_5_0").Span);
        _psLuma = device.CreatePixelShader(Compile(source, "PSLuma", "ps_5_0").Span);
        _psChroma = device.CreatePixelShader(Compile(source, "PSChroma", "ps_5_0").Span);
        _params = device.CreateBuffer(new BufferDescription(32, BindFlags.ConstantBuffer, ResourceUsage.Default));
    }

    static ReadOnlyMemory<byte> Compile(string source, string entry, string profile)
    {
        var hr = Compiler.Compile(source, entry, "Convert.hlsl", profile, out Blob? blob, out Blob? errors);
        if (hr.Failure || blob is null)
            throw new InvalidOperationException($"Shader {entry} failed: {errors?.AsString()}");
        var bytes = blob.AsBytes();
        blob.Dispose();
        errors?.Dispose();
        return bytes;
    }

    /// <summary>Renders <paramref name="source"/> into array slice <paramref name="slice"/> of an NV12 texture.</summary>
    public void Convert(ID3D11ShaderResourceView source, IntPtr nv12Texture, uint slice, FrameLayout layout, int scrollY = 0)
    {
        var (rtvY, rtvUV) = GetTargets(nv12Texture, slice);

        var p = new Params
        {
            Width = (uint)layout.Width,
            Height = (uint)layout.Height,
            AuxOffset = (uint)layout.AuxOffset,
            Mode = (uint)layout.Mode,
            ScrollY = (uint)(scrollY % layout.Height),
        };
        if (!p.Equals(_current))
        {
            _context.UpdateSubresource(in p, _params);
            _current = p;
        }

        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetInputLayout(null);
        _context.VSSetShader(_vs);
        _context.PSSetShaderResource(0, source);
        _context.PSSetConstantBuffer(0, _params);
        _context.RSSetState(null);
        _context.OMSetBlendState(null);

        _context.OMSetRenderTargets(rtvY);
        _context.RSSetViewport(0, 0, layout.EncWidth, layout.EncHeight);
        _context.PSSetShader(_psLuma);
        _context.Draw(3, 0);

        _context.OMSetRenderTargets(rtvUV);
        _context.RSSetViewport(0, 0, layout.EncWidth / 2, layout.EncHeight / 2);
        _context.PSSetShader(_psChroma);
        _context.Draw(3, 0);

        _context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        _context.PSSetShaderResource(0, null!);
        _context.Flush();
    }

    (ID3D11RenderTargetView, ID3D11RenderTargetView) GetTargets(IntPtr texturePtr, uint slice)
    {
        if (_targets.TryGetValue((texturePtr, slice), out var t))
            return t;

        // Wrapping a raw pointer doesn't take a reference; take one so Dispose is balanced.
        var texture = new ID3D11Texture2D(texturePtr);
        texture.AddRef();
        _textures.Add(texture);

        bool isArray = texture.Description.ArraySize > 1;
        RenderTargetViewDescription Desc(Format f) => isArray
            ? new RenderTargetViewDescription(RenderTargetViewDimension.Texture2DArray, f, 0, slice, 1)
            : new RenderTargetViewDescription(RenderTargetViewDimension.Texture2D, f);

        t = (_device.CreateRenderTargetView(texture, Desc(Format.R8_UNorm)),
             _device.CreateRenderTargetView(texture, Desc(Format.R8G8_UNorm)));
        _targets[(texturePtr, slice)] = t;
        return t;
    }

    readonly List<ID3D11Texture2D> _textures = new();

    /// <summary>Drops the cached views (and our references) of an encoder's textures that are going away.</summary>
    public void ClearTargets()
    {
        foreach (var (y, uv) in _targets.Values)
        {
            y.Dispose();
            uv.Dispose();
        }
        _targets.Clear();
        foreach (var t in _textures) t.Dispose();
        _textures.Clear();
    }

    public void Dispose()
    {
        ClearTargets();
        _params.Dispose();
        _psChroma.Dispose();
        _psLuma.Dispose();
        _vs.Dispose();
    }
}

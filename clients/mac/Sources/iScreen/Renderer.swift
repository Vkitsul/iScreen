import CoreVideo
import Metal
import QuartzCore

/// Rebuilds RGB from the decoded NV12 picture (4:4:4 unpacking or 4:2:0 upsampling) and shows it.
/// Pass 1 (compute) writes a full-resolution RGB texture; pass 2 draws it into the layer, 1:1 when
/// the window is at "actual size", otherwise filtered.
final class Renderer {
    private struct Params {
        var width: UInt32
        var height: UInt32
        var aux: UInt32
        var mode: UInt32
        var videoRange: UInt32
    }

    let layer: CAMetalLayer
    private let device: MTLDevice
    private let queue: MTLCommandQueue
    private let reconstruct: MTLComputePipelineState
    private let blit: MTLRenderPipelineState
    private let linear: MTLSamplerState
    private let nearest: MTLSamplerState
    private var cache: CVMetalTextureCache?
    private var rgb: MTLTexture?
    private let lock = NSLock()

    /// Called on an arbitrary thread when a frame reaches the screen (present time, µs).
    var onPresented: ((ReassembledFrame, Int64) -> Void)?

    init(layer: CAMetalLayer) throws {
        guard let device = MTLCreateSystemDefaultDevice(), let queue = device.makeCommandQueue() else {
            throw NSError(domain: "iScreen", code: 1, userInfo: [NSLocalizedDescriptionKey: "Metal non disponibile"])
        }
        self.layer = layer
        self.device = device
        self.queue = queue
        layer.device = device
        layer.pixelFormat = .bgra8Unorm
        layer.framebufferOnly = true
        layer.maximumDrawableCount = 3
        layer.displaySyncEnabled = false // present as soon as a frame is decoded

        let library = try device.makeLibrary(source: Self.shaderSource, options: nil)
        reconstruct = try device.makeComputePipelineState(function: library.makeFunction(name: "reconstruct")!)
        let desc = MTLRenderPipelineDescriptor()
        desc.vertexFunction = library.makeFunction(name: "blitVertex")
        desc.fragmentFunction = library.makeFunction(name: "blitFragment")
        desc.colorAttachments[0].pixelFormat = layer.pixelFormat
        blit = try device.makeRenderPipelineState(descriptor: desc)

        let s = MTLSamplerDescriptor()
        s.minFilter = .linear
        s.magFilter = .linear
        s.sAddressMode = .clampToEdge
        s.tAddressMode = .clampToEdge
        linear = device.makeSamplerState(descriptor: s)!
        s.minFilter = .nearest
        s.magFilter = .nearest
        nearest = device.makeSamplerState(descriptor: s)!

        CVMetalTextureCacheCreate(kCFAllocatorDefault, nil, device, nil, &cache)
    }

    func render(_ pixelBuffer: CVPixelBuffer, frame: ReassembledFrame, info: SessionInfo) {
        lock.lock()
        defer { lock.unlock() }
        guard let cache else { return }

        var lumaRef: CVMetalTexture?, chromaRef: CVMetalTexture?
        CVMetalTextureCacheCreateTextureFromImage(
            kCFAllocatorDefault, cache, pixelBuffer, nil, .r8Unorm,
            CVPixelBufferGetWidthOfPlane(pixelBuffer, 0), CVPixelBufferGetHeightOfPlane(pixelBuffer, 0), 0, &lumaRef)
        CVMetalTextureCacheCreateTextureFromImage(
            kCFAllocatorDefault, cache, pixelBuffer, nil, .rg8Unorm,
            CVPixelBufferGetWidthOfPlane(pixelBuffer, 1), CVPixelBufferGetHeightOfPlane(pixelBuffer, 1), 1, &chromaRef)
        guard let lumaRef, let chromaRef,
              let luma = CVMetalTextureGetTexture(lumaRef), let chroma = CVMetalTextureGetTexture(chromaRef),
              let cmd = queue.makeCommandBuffer() else { return }

        if rgb == nil || rgb!.width != info.width || rgb!.height != info.height {
            let d = MTLTextureDescriptor.texture2DDescriptor(
                pixelFormat: .rgba16Float, width: info.width, height: info.height, mipmapped: false)
            d.usage = [.shaderRead, .shaderWrite]
            d.storageMode = .private
            rgb = device.makeTexture(descriptor: d)
        }
        guard let rgb, let compute = cmd.makeComputeCommandEncoder() else { return }

        let videoRange = CVPixelBufferGetPixelFormatType(pixelBuffer) == kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
        var params = Params(width: UInt32(info.width), height: UInt32(info.height), aux: UInt32(info.auxOffset),
                            mode: UInt32(info.mode), videoRange: videoRange ? 1 : 0)
        compute.setComputePipelineState(reconstruct)
        compute.setTexture(luma, index: 0)
        compute.setTexture(chroma, index: 1)
        compute.setTexture(rgb, index: 2)
        compute.setBytes(&params, length: MemoryLayout<Params>.stride, index: 0)
        compute.dispatchThreadgroups(
            MTLSize(width: (info.width + 15) / 16, height: (info.height + 15) / 16, depth: 1),
            threadsPerThreadgroup: MTLSize(width: 16, height: 16, depth: 1))
        compute.endEncoding()

        draw(into: cmd, frame: frame)
        cmd.addCompletedHandler { _ in
            _ = lumaRef // keep the CoreVideo textures alive until the GPU is done
            _ = chromaRef
        }
        cmd.commit()
    }

    /// Re-presents the last image (window resized, colour space changed).
    func redraw() {
        lock.lock()
        defer { lock.unlock() }
        guard rgb != nil, let cmd = queue.makeCommandBuffer() else { return }
        draw(into: cmd, frame: nil)
        cmd.commit()
    }

    private func draw(into cmd: MTLCommandBuffer, frame: ReassembledFrame?) {
        guard let rgb, let drawable = layer.nextDrawable() else { return }
        let pass = MTLRenderPassDescriptor()
        pass.colorAttachments[0].texture = drawable.texture
        pass.colorAttachments[0].loadAction = .clear
        pass.colorAttachments[0].clearColor = MTLClearColor(red: 0, green: 0, blue: 0, alpha: 1)
        pass.colorAttachments[0].storeAction = .store
        guard let enc = cmd.makeRenderCommandEncoder(descriptor: pass) else { return }

        let target = CGSize(width: drawable.texture.width, height: drawable.texture.height)
        let rect = Self.fitRect(content: CGSize(width: rgb.width, height: rgb.height), in: target)
        enc.setViewport(MTLViewport(originX: rect.minX, originY: rect.minY,
                                    width: rect.width, height: rect.height, znear: 0, zfar: 1))
        enc.setRenderPipelineState(blit)
        enc.setFragmentTexture(rgb, index: 0)
        let oneToOne = Int(rect.width) == rgb.width && Int(rect.height) == rgb.height
        enc.setFragmentSamplerState(oneToOne ? nearest : linear, index: 0)
        enc.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: 3)
        enc.endEncoding()

        if let frame, let onPresented {
            drawable.addPresentedHandler { d in
                if d.presentedTime > 0 { onPresented(frame, Int64(d.presentedTime * 1_000_000)) }
            }
        }
        cmd.present(drawable)
    }

    /// Aspect-fit rectangle in pixels; snaps to exactly 1:1 when within 1% so text stays pixel-sharp,
    /// and fills the whole target when the proportions differ by less than 3% (no visible stretch,
    /// no thin black bars — e.g. a 1920×1200 PC on a 16:10-ish MacBook screen).
    static func fitRect(content: CGSize, in target: CGSize) -> CGRect {
        guard content.width > 0, content.height > 0 else { return CGRect(origin: .zero, size: target) }
        var scale = min(target.width / content.width, target.height / content.height)
        if abs(scale - 1) < 0.01 && content.width <= target.width && content.height <= target.height { scale = 1 }
        let aspectDifference = abs((content.width / content.height) / (target.width / target.height) - 1)
        if scale != 1 && aspectDifference < 0.03 { return CGRect(origin: .zero, size: target) }
        let w = (content.width * scale).rounded(), h = (content.height * scale).rounded()
        return CGRect(x: ((target.width - w) / 2).rounded(.down), y: ((target.height - h) / 2).rounded(.down),
                      width: w, height: h)
    }

    // Mirrors Convert.hlsl on the host. See docs/ARCHITETTURA.md for the 4:4:4 layout.
    private static let shaderSource = """
    #include <metal_stdlib>
    using namespace metal;

    struct Params { uint width; uint height; uint aux; uint mode; uint videoRange; };

    static float3 toRgb(float y, float cb, float cr, bool videoRange) {
        if (videoRange) {
            y = (y * 255.0 - 16.0) / 219.0;
            cb = (cb * 255.0 - 128.0) / 224.0 + 0.5;
            cr = (cr * 255.0 - 128.0) / 224.0 + 0.5;
        }
        cb -= 0.5;
        cr -= 0.5;
        return saturate(float3(y + 1.5748 * cr, y - 0.187324 * cb - 0.468124 * cr, y + 1.8556 * cb));
    }

    kernel void reconstruct(texture2d<float, access::read> luma [[texture(0)]],
                            texture2d<float, access::read> chroma [[texture(1)]],
                            texture2d<float, access::write> rgbOut [[texture(2)]],
                            constant Params& p [[buffer(0)]],
                            uint2 gid [[thread_position_in_grid]]) {
        if (gid.x >= p.width || gid.y >= p.height) return;
        uint x = gid.x, y = gid.y;
        float Y = luma.read(uint2(x, y)).r;
        float cb, cr;
        if (p.mode == 1) {
            // Cr is the aux luma; Cb's four polyphase components sit in the chroma planes.
            cr = luma.read(uint2(x, p.aux + y)).r;
            uint row = (y & 1) == 0 ? y / 2 : p.aux / 2 + y / 2;
            float2 c = chroma.read(uint2(x / 2, row)).rg;
            cb = (x & 1) == 0 ? c.r : c.g;
        } else {
            // 4:2:0: bilinear, centre-sited chroma.
            float2 size = float2(chroma.get_width(), chroma.get_height());
            float2 f = clamp((float2(x, y) - 0.5) * 0.5, float2(0.0), size - 1.0);
            uint2 p0 = uint2(floor(f));
            uint2 p1 = uint2(min(float2(p0) + 1.0, size - 1.0));
            float2 a = f - float2(p0);
            float2 c00 = chroma.read(p0).rg;
            float2 c10 = chroma.read(uint2(p1.x, p0.y)).rg;
            float2 c01 = chroma.read(uint2(p0.x, p1.y)).rg;
            float2 c11 = chroma.read(p1).rg;
            float2 c = mix(mix(c00, c10, a.x), mix(c01, c11, a.x), a.y);
            cb = c.r;
            cr = c.g;
        }
        rgbOut.write(float4(toRgb(Y, cb, cr, p.videoRange != 0), 1.0), gid);
    }

    struct VOut { float4 position [[position]]; float2 uv; };

    vertex VOut blitVertex(uint id [[vertex_id]]) {
        float2 uv = float2(float((id << 1) & 2), float(id & 2));
        VOut o;
        o.position = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
        o.uv = uv;
        return o;
    }

    fragment float4 blitFragment(VOut v [[stage_in]], texture2d<float> tex [[texture(0)]], sampler s [[sampler(0)]]) {
        return tex.sample(s, v.uv);
    }
    """
}

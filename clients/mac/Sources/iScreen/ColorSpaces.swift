import CoreGraphics
import simd

enum ColorMode: Int, CaseIterable {
    case hostMonitor = 0   // reproduce the PC monitor (its primaries + gamma 2.2)
    case sRGB = 1          // treat the desktop as standard sRGB
    case passthrough = 2   // no colour management: values go straight to the Mac panel

    var label: String {
        switch self {
        case .hostMonitor: return "Come il monitor del PC"
        case .sRGB: return "sRGB standard"
        case .passthrough: return "Nessuna conversione"
        }
    }
}

enum ColorSpaces {
    /// Colour space to tag the CAMetalLayer with; ColorSync converts it to the Mac display.
    static func make(_ mode: ColorMode, info: SessionInfo) -> CGColorSpace? {
        switch mode {
        case .sRGB: return CGColorSpace(name: CGColorSpace.sRGB)
        case .passthrough: return nil
        case .hostMonitor:
            return calibrated(primaries: info.primaries, gamma: info.gamma) ?? CGColorSpace(name: CGColorSpace.sRGB)
        }
    }

    /// Calibrated RGB from CIE xy primaries/white (PDF CalRGB semantics: matrix rows are the
    /// XYZ of R, G, B scaled so that R+G+B = white with Y = 1).
    static func calibrated(primaries p: [Float], gamma: Float) -> CGColorSpace? {
        guard p.count == 8, p.allSatisfy({ $0 > 0 && $0 < 1 }) else { return nil }
        func xyz(_ x: Float, _ y: Float) -> SIMD3<Double> {
            SIMD3(Double(x / y), 1, Double((1 - x - y) / y))
        }
        let r = xyz(p[0], p[1]), g = xyz(p[2], p[3]), b = xyz(p[4], p[5]), w = xyz(p[6], p[7])
        let m = simd_double3x3(columns: (r, g, b))
        guard abs(m.determinant) > 1e-9 else { return nil }
        let s = m.inverse * w
        let R = r * s.x, G = g * s.y, B = b * s.z

        let white: [CGFloat] = [w.x, w.y, w.z]
        let black: [CGFloat] = [0, 0, 0]
        let gam = CGFloat(gamma > 0.5 ? gamma : 2.2)
        let gammas: [CGFloat] = [gam, gam, gam]
        let matrix: [CGFloat] = [R.x, R.y, R.z, G.x, G.y, G.z, B.x, B.y, B.z]
        return CGColorSpace(calibratedRGBWhitePoint: white, blackPoint: black, gamma: gammas, matrix: matrix)
    }
}

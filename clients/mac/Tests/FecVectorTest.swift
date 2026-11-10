// Cross-check of ReedSolomon.swift against the PC (iscreen-host fectest --vector FILE).
// swiftc -O -parse-as-library Tests/FecVectorTest.swift Sources/iScreen/ReedSolomon.swift -o /tmp/fectest
import Foundation

@main
enum FecVectorTest {
    static func main() {
        let lines = (try? String(contentsOfFile: CommandLine.arguments[1], encoding: .utf8))?
            .split(whereSeparator: \.isNewline).map(String.init) ?? []
        let shards = lines.map { hex in
            stride(from: 0, to: hex.count, by: 2).map { i -> UInt8 in
                let start = hex.index(hex.startIndex, offsetBy: i)
                return UInt8(hex[start..<hex.index(start, offsetBy: 2)], radix: 16)!
            }
        }
        let k = 7, p = 3
        guard shards.count == k + p else { print("bad vector file"); exit(1) }
        let size = shards[0].count

        let original = UnsafeMutablePointer<UInt8>.allocate(capacity: (k + p) * size)
        let work = UnsafeMutablePointer<UInt8>.allocate(capacity: (k + p) * size)
        for (i, s) in shards.enumerated() { (original + i * size).update(from: s, count: size) }
        work.update(from: original, count: (k + p) * size)

        // 1. Same parity bytes as C#.
        let recomputed = UnsafeMutablePointer<UInt8>.allocate(capacity: p * size)
        ReedSolomon.encode(data: (0..<k).map { UnsafePointer(original + $0 * size) },
                           parity: (0..<p).map { recomputed + $0 * size }, size: size)
        let parityMatches = memcmp(recomputed, original + k * size, p * size) == 0
        print("parity identical to the PC: \(parityMatches)")

        // 2. Lose three data shards (as many as the parity) and rebuild them.
        var haveData = [Bool](repeating: true, count: k)
        for lost in [1, 4, 6] {
            haveData[lost] = false
            memset(work + lost * size, 0, size)
        }
        let ok = ReedSolomon.reconstruct(data: (0..<k).map { work + $0 * size }, haveData: haveData,
                                         parity: (0..<p).map { UnsafePointer(work + (k + $0) * size) },
                                         haveParity: [Bool](repeating: true, count: p), size: size)
        let rebuilt = ok && memcmp(work, original, k * size) == 0
        print("3 lost shards rebuilt exactly: \(rebuilt)")

        // 3. One loss too many must be refused.
        haveData[0] = false
        let refused = !ReedSolomon.reconstruct(data: (0..<k).map { work + $0 * size }, haveData: haveData,
                                               parity: (0..<p).map { UnsafePointer(work + (k + $0) * size) },
                                               haveParity: [Bool](repeating: true, count: p), size: size)
        print("too many losses refused: \(refused)")

        let pass = parityMatches && rebuilt && refused
        print(pass ? "SWIFT FEC OK" : "SWIFT FEC FAILED")
        exit(pass ? 0 : 1)
    }
}

import Foundation

/// Systematic Reed-Solomon erasure code over GF(2^8) with a Cauchy matrix, as on the PC
/// (ReedSolomon.cs): parity row j, data column i -> C[j,i] = 1 / ((k + j) ^ i).
enum ReedSolomon {
    private static let expTable: [UInt8] = {
        var e = [UInt8](repeating: 0, count: 512)
        var x = 1
        for i in 0..<255 {
            e[i] = UInt8(x)
            x <<= 1
            if x & 0x100 != 0 { x ^= 0x11D } // x^8 + x^4 + x^3 + x^2 + 1
        }
        for i in 255..<512 { e[i] = e[i - 255] }
        return e
    }()

    private static let logTable: [UInt8] = {
        var l = [UInt8](repeating: 0, count: 256)
        for i in 0..<255 { l[Int(expTable[i])] = UInt8(i) }
        return l
    }()

    /// mulTable[a * 256 + b] = a * b
    private static let mulTable: [UInt8] = {
        var t = [UInt8](repeating: 0, count: 65_536)
        for a in 1..<256 {
            for b in 1..<256 { t[a * 256 + b] = expTable[Int(logTable[a]) + Int(logTable[b])] }
        }
        return t
    }()

    private static func mul(_ a: UInt8, _ b: UInt8) -> UInt8 { mulTable[Int(a) * 256 + Int(b)] }
    private static func inv(_ a: UInt8) -> UInt8 { expTable[255 - Int(logTable[Int(a)])] }
    private static func coefficient(_ k: Int, _ row: Int, _ column: Int) -> UInt8 { inv(UInt8((k + row) ^ column)) }

    /// dst ^= coef * src, bytewise.
    private static func mulAdd(_ coef: UInt8, _ src: UnsafePointer<UInt8>, _ dst: UnsafeMutablePointer<UInt8>, _ n: Int) {
        if coef == 0 { return }
        if coef == 1 {
            for i in 0..<n { dst[i] ^= src[i] }
            return
        }
        mulTable.withUnsafeBufferPointer { table in
            let row = table.baseAddress! + Int(coef) * 256
            for i in 0..<n { dst[i] ^= row[Int(src[i])] }
        }
    }

    /// Parity from data (used by the self-check against the PC's output).
    static func encode(data: [UnsafePointer<UInt8>], parity: [UnsafeMutablePointer<UInt8>], size: Int) {
        for (j, p) in parity.enumerated() {
            memset(p, 0, size)
            for (i, d) in data.enumerated() { mulAdd(coefficient(data.count, j, i), d, p, size) }
        }
    }

    /// Rebuilds the missing data shards in place; false if fewer than k shards arrived.
    static func reconstruct(data: [UnsafeMutablePointer<UInt8>], haveData: [Bool],
                            parity: [UnsafePointer<UInt8>], haveParity: [Bool], size: Int) -> Bool {
        let k = data.count
        let missing = (0..<k).filter { !haveData[$0] }
        if missing.isEmpty { return true }
        var rows: [Int] = []
        for j in 0..<parity.count where haveParity[j] && rows.count < missing.count { rows.append(j) }
        guard rows.count == missing.count else { return false }
        let e = missing.count

        // Syndromes: what the missing shards add up to in each parity row we use.
        var syndromes: [[UInt8]] = []
        for r in 0..<e {
            var s = [UInt8](UnsafeBufferPointer(start: parity[rows[r]], count: size))
            s.withUnsafeMutableBufferPointer { sp in
                for i in 0..<k where haveData[i] {
                    mulAdd(coefficient(k, rows[r], i), UnsafePointer(data[i]), sp.baseAddress!, size)
                }
            }
            syndromes.append(s)
        }

        // A[r][c] = C[rows[r], missing[c]]: a Cauchy submatrix, always invertible.
        var a = [[UInt8]](repeating: [UInt8](repeating: 0, count: e), count: e)
        for r in 0..<e {
            for c in 0..<e { a[r][c] = coefficient(k, rows[r], missing[c]) }
        }
        let inverse = invert(a)

        for c in 0..<e {
            let d = data[missing[c]]
            memset(d, 0, size)
            for r in 0..<e {
                syndromes[r].withUnsafeBufferPointer { mulAdd(inverse[c][r], $0.baseAddress!, d, size) }
            }
        }
        return true
    }

    /// Gauss-Jordan inversion in GF(2^8).
    private static func invert(_ m: [[UInt8]]) -> [[UInt8]] {
        let n = m.count
        var a = m
        var r = [[UInt8]](repeating: [UInt8](repeating: 0, count: n), count: n)
        for i in 0..<n { r[i][i] = 1 }
        for col in 0..<n {
            var pivot = col
            while a[pivot][col] == 0 { pivot += 1 }
            if pivot != col {
                a.swapAt(pivot, col)
                r.swapAt(pivot, col)
            }
            let scale = inv(a[col][col])
            for c in 0..<n {
                a[col][c] = mul(a[col][c], scale)
                r[col][c] = mul(r[col][c], scale)
            }
            for row in 0..<n where row != col && a[row][col] != 0 {
                let f = a[row][col]
                for c in 0..<n {
                    a[row][c] ^= mul(f, a[col][c])
                    r[row][c] ^= mul(f, r[col][c])
                }
            }
        }
        return r
    }
}

/// Shard grouping, as FecLayout.cs: data shard i is in block i % blocks; each block gets
/// ceil(k_b * percent / 100) parity shards (at least one), numbered after all data shards.
struct FecLayout {
    let dataShards: Int
    let blocks: Int
    private var blockData: [Int] = []
    private var parityStart: [Int] = []
    private var parityCounts: [Int] = []
    private(set) var parityShards = 0

    init(dataShards: Int, percent: Int, maxBlockData: Int) {
        self.dataShards = dataShards
        blocks = percent <= 0 ? 0 : max(1, (dataShards + maxBlockData - 1) / maxBlockData)
        var next = dataShards
        for b in 0..<blocks {
            let k = dataShards / blocks + (b < dataShards % blocks ? 1 : 0)
            let p = max(1, (k * percent + 99) / 100)
            blockData.append(k)
            parityStart.append(next)
            parityCounts.append(p)
            next += p
        }
        parityShards = next - dataShards
    }

    func blockOfData(_ shard: Int) -> Int { shard % blocks }
    func dataInBlock(_ block: Int) -> Int { blockData[block] }
    func dataShard(_ block: Int, _ n: Int) -> Int { block + n * blocks }
    func parityCount(_ block: Int) -> Int { parityCounts[block] }
    func parityShard(_ block: Int, _ j: Int) -> Int { parityStart[block] + j }

    func parityBlock(_ shard: Int) -> Int {
        for b in 0..<blocks where shard < parityStart[b] + parityCounts[b] { return b }
        return blocks - 1
    }
}

namespace IScreen.Host.Net;

/// <summary>
/// Systematic Reed-Solomon erasure code over GF(2^8) with a Cauchy matrix: k data shards plus p
/// parity shards, any k of the k+p rebuild the data. Parity row j, data column i:
/// C[j,i] = 1 / (x_j ^ y_i) with y_i = i and x_j = k + j (disjoint, so every square submatrix is
/// invertible). Mirrored by ReedSolomon.swift on the Mac.
/// </summary>
public static unsafe class ReedSolomon
{
    static readonly byte[] Exp = new byte[512];
    static readonly byte[] Log = new byte[256];
    static readonly byte[] MulTable = new byte[256 * 256]; // MulTable[a * 256 + b] = a * b

    static ReedSolomon()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0) x ^= 0x11D; // x^8 + x^4 + x^3 + x^2 + 1
        }
        for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
        for (int a = 1; a < 256; a++)
        for (int b = 1; b < 256; b++)
            MulTable[a * 256 + b] = Exp[Log[a] + Log[b]];
    }

    static byte Mul(byte a, byte b) => MulTable[a * 256 + b];
    static byte Inv(byte a) => Exp[255 - Log[a]];
    static byte Coefficient(int dataShards, int parityRow, int dataColumn) =>
        Inv((byte)((dataShards + parityRow) ^ dataColumn));

    /// <summary>dst ^= coef * src, bytewise.</summary>
    static void MulAdd(byte coef, byte* src, byte* dst, int length)
    {
        if (coef == 0) return;
        fixed (byte* table = &MulTable[coef * 256])
        {
            if (coef == 1)
            {
                for (int i = 0; i < length; i++) dst[i] ^= src[i];
                return;
            }
            for (int i = 0; i < length; i++) dst[i] ^= table[src[i]];
        }
    }

    /// <summary>Fills the parity shards from the data shards (all shards the same length).</summary>
    public static void Encode(IReadOnlyList<Memory<byte>> data, IReadOnlyList<Memory<byte>> parity)
    {
        int k = data.Count, shardSize = data[0].Length;
        for (int j = 0; j < parity.Count; j++)
        {
            fixed (byte* p = parity[j].Span)
            {
                new Span<byte>(p, shardSize).Clear();
                for (int i = 0; i < k; i++)
                    fixed (byte* d = data[i].Span)
                        MulAdd(Coefficient(k, j, i), d, p, shardSize);
            }
        }
    }

    /// <summary>
    /// Rebuilds missing data shards in place. <paramref name="data"/>[i] / <paramref name="parity"/>[j]
    /// are full-size buffers; <paramref name="haveData"/> / <paramref name="haveParity"/> say which arrived.
    /// Returns false if fewer than k shards are available.
    /// </summary>
    public static bool Reconstruct(IReadOnlyList<Memory<byte>> data, bool[] haveData, IReadOnlyList<Memory<byte>> parity, bool[] haveParity)
    {
        int k = data.Count, shardSize = data[0].Length;
        var missing = new List<int>();
        for (int i = 0; i < k; i++) if (!haveData[i]) missing.Add(i);
        if (missing.Count == 0) return true;
        var rows = new List<int>();
        for (int j = 0; j < parity.Count && rows.Count < missing.Count; j++) if (haveParity[j]) rows.Add(j);
        if (rows.Count < missing.Count) return false;
        int e = missing.Count;

        // Syndromes: s_r = P_j ^ sum over received data of C[j,i] * D_i (what the missing shards add up to).
        var syndromes = new byte[e][];
        for (int r = 0; r < e; r++)
        {
            syndromes[r] = parity[rows[r]].ToArray();
            fixed (byte* s = syndromes[r])
                for (int i = 0; i < k; i++)
                    if (haveData[i])
                        fixed (byte* d = data[i].Span)
                            MulAdd(Coefficient(k, rows[r], i), d, s, shardSize);
        }

        // Solve A x = s with A[r,c] = C[rows[r], missing[c]] (a Cauchy submatrix: always invertible).
        var a = new byte[e, e];
        for (int r = 0; r < e; r++)
        for (int c = 0; c < e; c++)
            a[r, c] = Coefficient(k, rows[r], missing[c]);
        var inverse = Invert(a, e);

        for (int c = 0; c < e; c++)
        {
            fixed (byte* d = data[missing[c]].Span)
            {
                new Span<byte>(d, shardSize).Clear();
                for (int r = 0; r < e; r++)
                    fixed (byte* s = syndromes[r])
                        MulAdd(inverse[c, r], s, d, shardSize);
            }
            haveData[missing[c]] = true;
        }
        return true;
    }

    /// <summary>Gauss-Jordan inversion in GF(2^8).</summary>
    static byte[,] Invert(byte[,] m, int n)
    {
        var a = (byte[,])m.Clone();
        var inv = new byte[n, n];
        for (int i = 0; i < n; i++) inv[i, i] = 1;
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            while (a[pivot, col] == 0) pivot++; // exists: the matrix is invertible
            if (pivot != col)
                for (int c = 0; c < n; c++)
                {
                    (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
                    (inv[col, c], inv[pivot, c]) = (inv[pivot, c], inv[col, c]);
                }
            byte scale = Inv(a[col, col]);
            for (int c = 0; c < n; c++)
            {
                a[col, c] = Mul(a[col, c], scale);
                inv[col, c] = Mul(inv[col, c], scale);
            }
            for (int r = 0; r < n; r++)
            {
                if (r == col || a[r, col] == 0) continue;
                byte f = a[r, col];
                for (int c = 0; c < n; c++)
                {
                    a[r, c] ^= Mul(f, a[col, c]);
                    inv[r, c] ^= Mul(f, inv[col, c]);
                }
            }
        }
        return inv;
    }
}

/// <summary>
/// How a frame's shards are grouped for FEC. Data shard i belongs to block i % blocks (interleaved, so
/// a burst of consecutive losses is spread over several blocks); each block gets
/// ceil(k_b * percent / 100) parity shards (at least one), numbered after all data shards.
/// </summary>
public sealed class FecLayout
{
    public readonly int DataShards, ParityShards, Blocks;
    readonly int[] _blockData, _parityStart, _parityCount;

    public FecLayout(int dataShards, int percent, int maxBlockData)
    {
        DataShards = dataShards;
        Blocks = percent <= 0 ? 0 : Math.Max(1, (dataShards + maxBlockData - 1) / maxBlockData);
        _blockData = new int[Blocks];
        _parityStart = new int[Blocks];
        _parityCount = new int[Blocks];
        int next = dataShards;
        for (int b = 0; b < Blocks; b++)
        {
            _blockData[b] = dataShards / Blocks + (b < dataShards % Blocks ? 1 : 0);
            _parityCount[b] = Math.Max(1, (_blockData[b] * percent + 99) / 100);
            _parityStart[b] = next;
            next += _parityCount[b];
        }
        ParityShards = next - dataShards;
    }

    public int BlockOfData(int shard) => shard % Blocks;
    public int DataInBlock(int block) => _blockData[block];
    /// <summary>Data shard index of the n-th data shard of a block.</summary>
    public int DataShard(int block, int n) => block + n * Blocks;
    public int ParityCount(int block) => _parityCount[block];
    public int ParityShard(int block, int j) => _parityStart[block] + j;

    /// <summary>(block, row) of a parity shard index.</summary>
    public (int Block, int Row) ParityPosition(int shard)
    {
        for (int b = 0; b < Blocks; b++)
            if (shard < _parityStart[b] + _parityCount[b]) return (b, shard - _parityStart[b]);
        throw new ArgumentOutOfRangeException(nameof(shard));
    }
}

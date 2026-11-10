using System.Diagnostics;
using IScreen.Host.Net;

namespace IScreen.Host.Tools;

/// <summary>Reed-Solomon checks: random erasures, timing, and a vector for the Swift implementation.</summary>
public static class FecTest
{
    public static int Run(string? vectorPath)
    {
        var random = new Random(42);
        int trials = 0, failures = 0;
        for (int t = 0; t < 3000; t++)
        {
            int k = random.Next(1, 33), p = random.Next(1, 9), size = random.Next(1, 200);
            var data = Enumerable.Range(0, k).Select(_ => RandomBytes(random, size)).ToArray();
            var parity = Enumerable.Range(0, p).Select(_ => new byte[size]).ToArray();
            ReedSolomon.Encode(Mem(data), Mem(parity));
            var original = data.Select(d => (byte[])d.Clone()).ToArray();

            // Lose up to p shards at random, data or parity.
            var haveData = Enumerable.Repeat(true, k).ToArray();
            var haveParity = Enumerable.Repeat(true, p).ToArray();
            int lost = random.Next(0, p + 1);
            for (int n = 0; n < lost; n++)
            {
                int s = random.Next(k + p);
                if (s < k) { haveData[s] = false; Array.Clear(data[s]); }
                else haveParity[s - k] = false;
            }
            trials++;
            if (!ReedSolomon.Reconstruct(Mem(data), haveData, Mem(parity), haveParity) ||
                !data.Zip(original).All(x => x.First.AsSpan().SequenceEqual(x.Second)))
                failures++;
        }
        Console.WriteLine($"random erasures: {trials - failures}/{trials} blocks rebuilt exactly");

        // One more loss than parity must be reported as unrecoverable.
        {
            var data = Enumerable.Range(0, 4).Select(_ => RandomBytes(random, 16)).ToArray();
            var parity = new[] { new byte[16] };
            ReedSolomon.Encode(Mem(data), Mem(parity));
            bool ok = !ReedSolomon.Reconstruct(Mem(data), [false, false, true, true], Mem(parity), [true]);
            Console.WriteLine($"too many losses detected: {ok}");
            if (!ok) failures++;
        }

        // Cost on a big keyframe: 300 KB = 219 shards, 20% parity in interleaved blocks of up to 32.
        {
            var layout = new FecLayout(219, 20, 32);
            const int Size = Protocol.MaxPayload;
            var shards = Enumerable.Range(0, 219).Select(_ => RandomBytes(random, Size)).ToArray();
            var sw = Stopwatch.StartNew();
            const int Runs = 20;
            for (int run = 0; run < Runs; run++)
                for (int b = 0; b < layout.Blocks; b++)
                {
                    var blockData = Enumerable.Range(0, layout.DataInBlock(b)).Select(n => shards[layout.DataShard(b, n)]).ToArray();
                    var blockParity = Enumerable.Range(0, layout.ParityCount(b)).Select(_ => new byte[Size]).ToArray();
                    ReedSolomon.Encode(Mem(blockData), Mem(blockParity));
                }
            Console.WriteLine($"300 KB keyframe: {layout.Blocks} blocks, {layout.ParityShards} parity shards, " +
                              $"encode {sw.Elapsed.TotalMilliseconds / Runs:0.00} ms");
        }

        if (vectorPath is not null)
        {
            // Deterministic block for ReedSolomon.swift: k=7, p=3, 100-byte shards.
            const int K = 7, P = 3, S = 100;
            var data = Enumerable.Range(0, K).Select(i => Enumerable.Range(0, S).Select(b => (byte)(i * 31 + b * 7 + 3)).ToArray()).ToArray();
            var parity = Enumerable.Range(0, P).Select(_ => new byte[S]).ToArray();
            ReedSolomon.Encode(Mem(data), Mem(parity));
            File.WriteAllLines(vectorPath, data.Concat(parity).Select(Convert.ToHexString));
            Console.WriteLine($"wrote {K}+{P} shards to {vectorPath}");
        }

        Console.WriteLine(failures == 0 ? "FEC OK" : $"FEC FAILED ({failures})");
        return failures == 0 ? 0 : 1;
    }

    static Memory<byte>[] Mem(byte[][] shards) => shards.Select(s => (Memory<byte>)s).ToArray();

    static byte[] RandomBytes(Random random, int n)
    {
        var b = new byte[n];
        random.NextBytes(b);
        return b;
    }
}

// Differential test for DestinationWalker.
//
// The destination walk decides which files Cleanup offers to DELETE, so the
// three optimisations applied to it (one directory enumeration instead of two,
// parallel across directories, no per-file string allocation) are held to
// producing IDENTICAL verdicts -- not similar ones, not the same counts.
//
// OriginalWalk.cs holds the pre-optimisation algorithm verbatim from git. Every
// tree below is walked by both and the result SETS are compared exactly.

using System.Diagnostics;
using System.IO;
using LithicBackup.Services;

namespace DestWalkTest;

internal static class Program
{
    static int _failures;

    static void Check(bool cond, string msg)
    {
        Console.WriteLine((cond ? "  ✓ " : "  ✗ FAIL: ") + msg);
        if (!cond) _failures++;
    }

    // The real reconstruction is a private view-model method; for differential
    // purposes both walkers must simply be given the SAME function.
    static string? Reconstruct(string discRel)
        => discRel.Length > 2 && discRel[1] == '\\' && char.IsLetter(discRel[0])
            ? discRel[0] + ":" + discRel[1..]
            : null;

    static int Main()
    {
        Console.WriteLine("=== 1. DiscPathKey: composed span key == key of the concatenation ===");
        KeyEquivalence();

        Console.WriteLine();
        Console.WriteLine("=== 2. differential walk over generated trees ===");
        string root = Path.Combine(Path.GetTempPath(), "lithic_destwalk_" + Guid.NewGuid().ToString("N"));
        try
        {
            var rng = new Random(20260922);
            for (int shape = 0; shape < 6; shape++)
            {
                string tree = Path.Combine(root, "tree" + shape);
                var (lookup, files) = BuildTree(tree, rng, shape);
                Differential($"shape {shape} ({files:N0} files)", tree, lookup);
            }

            Console.WriteLine();
            Console.WriteLine("=== 3. performance on a wide tree ===");
            string big = Path.Combine(root, "big");
            var (bigLookup, bigFiles) = BuildTree(big, new Random(7), shape: 99);
            Benchmark(big, bigLookup, bigFiles);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------
    static void KeyEquivalence()
    {
        (string Dir, string Name, string Suffix)[] cases =
        {
            ("",                 "file.txt",        ""),
            ("D",                "file.txt",        ""),
            (@"D\sub",           "file.txt",        ".fileref"),
            (@"D\sub\deeper",    "MiXeD Case.TXT",  ".dedup"),
            (@"D/forward/slash", "name.bin",        ""),          // separator folding
            (@"C_prev\x",        "f.v3",            ".fileref"),
            ("",                 "UPPER.TXT",       ".dedup"),
            (new string('d', 200), new string('n', 200), ".fileref"),   // past the stack budget
        };

        int bad = 0;
        foreach (var (dir, name, suffix) in cases)
        {
            string joined = (dir.Length == 0 ? name : dir + "\\" + name) + suffix;
            if (DiscPathKey.From(joined) != DiscPathKey.From(dir.AsSpan(), name.AsSpan(), suffix.AsSpan()))
            {
                bad++;
                Console.WriteLine($"     differs for [{dir}] [{name}] [{suffix}]");
            }
        }
        Check(bad == 0, $"{cases.Length} composed keys match the concatenated form (incl. >320 chars)");

        // And the case/separator folding the safety argument depends on.
        Check(DiscPathKey.From(@"d\a.txt".AsSpan()) == DiscPathKey.From(@"D\A.TXT".AsSpan()),
            "case-insensitive");
        Check(DiscPathKey.From("d/a.txt".AsSpan()) == DiscPathKey.From(@"d\a.txt".AsSpan()),
            "forward slash folds to backslash");
        Check(DiscPathKey.From(@"d\a.txt".AsSpan()) != DiscPathKey.From(@"d\b.txt".AsSpan()),
            "different paths give different keys");
    }

    // ---------------------------------------------------------------
    /// <summary>
    /// Build a destination tree plus the catalog lookup for it, mixing every
    /// case the walker distinguishes: tracked, tracked-but-all-deleted,
    /// untracked, .fileref/.dedup manifests materialised to plain files, .lbtmp
    /// leftovers, and the _blocks/_filestore stores that must be skipped whole.
    /// </summary>
    static (Dictionary<UInt128, DestPathState> Lookup, int Files) BuildTree(
        string root, Random rng, int shape)
    {
        Directory.CreateDirectory(root);
        var lookup = new Dictionary<UInt128, DestPathState>();
        int files = 0;

        // Correctness shapes stay small and varied — they exist to hit every
        // branch, not to stress anything. The benchmark shape is wide rather than
        // deep, because directories are the unit of parallelism.
        int dirs   = shape == 99 ? 0 : 2 + rng.Next(0, 3);
        int depth  = shape == 99 ? 2 : 1 + (shape % 3);
        int perDir = shape == 99 ? 12 : 1 + rng.Next(0, 8);

        void Emit(string relDir, int remaining)
        {
            string abs = relDir.Length == 0 ? root : Path.Combine(root, relDir);
            Directory.CreateDirectory(abs);

            for (int i = 0; i < perDir; i++)
            {
                string name = $"f{i}_{rng.Next(1000)}.dat";
                int kind = rng.Next(0, 10);

                if (kind == 8) name += ".lbtmp";                 // must be ignored

                File.WriteAllText(Path.Combine(abs, name), "x");
                files++;

                string rel = relDir.Length == 0 ? name : relDir + "\\" + name;

                switch (kind)
                {
                    case 0: case 1: case 2: case 3:              // tracked and live
                        lookup[DiscPathKey.From(rel)] = new DestPathState(true, null);
                        break;
                    case 4:                                       // all records deleted
                        lookup[DiscPathKey.From(rel)] = new DestPathState(false, @"C:\src\" + name);
                        break;
                    case 5:                                       // materialised .fileref
                        lookup[DiscPathKey.From(rel + ".fileref")] = new DestPathState(true, null);
                        break;
                    case 6:                                       // materialised .dedup, deleted
                        lookup[DiscPathKey.From(rel + ".dedup")] = new DestPathState(false, @"C:\src\" + name);
                        break;
                    default:                                      // untracked
                        break;
                }
            }

            // Stores that must be skipped entirely, contents and all.
            if (relDir.Length == 0)
            {
                foreach (var store in new[] { "_blocks", "_filestore" })
                {
                    var sd = Path.Combine(abs, store, "aa");
                    Directory.CreateDirectory(sd);
                    File.WriteAllText(Path.Combine(sd, "chunk.bin"), "y");
                }
            }

            if (remaining <= 0) return;
            // 60 x 20 = 1,200 directories, 12 files each ≈ 14,400 files.
            int n = shape == 99 ? (relDir.Length == 0 ? 60 : 20) : dirs;
            for (int d = 0; d < n; d++)
            {
                string sub = relDir.Length == 0 ? $"d{d}" : relDir + "\\" + $"d{d}";
                Emit(sub, remaining - 1);
            }
        }

        Emit("", depth);
        return (lookup, files);
    }

    // ---------------------------------------------------------------
    static void Differential(string label, string tree, Dictionary<UInt128, DestPathState> lookup)
    {
        var sink = new Progress<string>(_ => { });

        var (oUn, oDel, oSkip, oScan) =
            OriginalWalk.WalkOriginal(tree, lookup, Reconstruct, sink);

        var neu = DestinationWalker.Walk(tree, lookup, Reconstruct, sink);

        static string Key((string DiscRel, long Size, string? SourcePath) t)
            => $"{t.DiscRel}|{t.Size}|{t.SourcePath}";

        var oUnSet  = oUn.Select(Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var nUnSet  = neu.Untracked.Select(Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var oDelSet = oDel.Select(Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var nDelSet = neu.CatalogDeleted.Select(Key).OrderBy(x => x, StringComparer.Ordinal).ToList();

        bool unSame  = oUnSet.SequenceEqual(nUnSet, StringComparer.Ordinal);
        bool delSame = oDelSet.SequenceEqual(nDelSet, StringComparer.Ordinal);

        Check(unSame,  $"{label}: untracked identical ({oUnSet.Count:N0} entries)");
        Check(delSame, $"{label}: catalog-deleted identical ({oDelSet.Count:N0} entries)");
        Check(oScan == neu.FilesScanned,
            $"{label}: files scanned identical ({oScan:N0} vs {neu.FilesScanned:N0})");
        Check(oSkip == neu.DirectoriesSkipped,
            $"{label}: directories skipped identical ({oSkip} vs {neu.DirectoriesSkipped})");

        if (!unSame)
            foreach (var d in oUnSet.Except(nUnSet).Concat(nUnSet.Except(oUnSet)).Take(5))
                Console.WriteLine("       diff: " + d);
    }

    // ---------------------------------------------------------------
    static void Benchmark(string tree, Dictionary<UInt128, DestPathState> lookup, int files)
    {
        var sink = new Progress<string>(_ => { });

        // Warm the OS directory cache for both, so this measures the algorithms
        // rather than whichever ran first.
        OriginalWalk.WalkOriginal(tree, lookup, Reconstruct, sink);
        DestinationWalker.Walk(tree, lookup, Reconstruct, sink);

        var sw = Stopwatch.StartNew();
        var (_, _, _, oScan) = OriginalWalk.WalkOriginal(tree, lookup, Reconstruct, sink);
        sw.Stop();
        var oldTime = sw.Elapsed;

        sw.Restart();
        var neu = DestinationWalker.Walk(tree, lookup, Reconstruct, sink);
        sw.Stop();
        var newTime = sw.Elapsed;

        // Single-threaded new walker, to separate "one enumeration instead of
        // two + no per-file string" from "parallel".
        sw.Restart();
        var seq = DestinationWalker.Walk(tree, lookup, Reconstruct, sink, maxParallelism: 1);
        sw.Stop();
        var seqTime = sw.Elapsed;

        Console.WriteLine($"  tree: {oScan:N0} files");
        Console.WriteLine($"  OLD (2 sweeps/dir, 1 thread) : {oldTime.TotalSeconds:N3}s");
        Console.WriteLine($"  NEW (1 sweep/dir,  1 thread) : {seqTime.TotalSeconds:N3}s"
                          + $"  =>  {oldTime.TotalSeconds / seqTime.TotalSeconds:N2}x");
        Console.WriteLine($"  NEW (1 sweep/dir, 16 threads): {newTime.TotalSeconds:N3}s"
                          + $"  =>  {oldTime.TotalSeconds / newTime.TotalSeconds:N2}x");

        Check(neu.FilesScanned == oScan && seq.FilesScanned == oScan,
            "all three passes scanned the same number of files");
    }
}

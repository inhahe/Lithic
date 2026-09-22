// Equivalence + performance test for DirectoryPrefixSet.
//
// Four classification phases used to filter every catalog row against every
// orphaned directory:
//
//     orphanedDirPaths.Any(p => IsPathUnderRoot(f.SourcePath, p))
//
// O(files x directories), with a string allocated inside every comparison.
// Measured live on a real set: "Detecting excess versions" advanced at 21
// files/sec against 2,854,931 rows -- roughly 36 hours for that one phase, on a
// single thread, with the catalog already in memory.
//
// DirectoryPrefixSet answers the same question by walking a path's own ancestors
// against a hash set, so cost tracks path DEPTH rather than directory COUNT.
//
// Rewriting a hot predicate is only safe if it means exactly the same thing, so
// correctness is tested first and hardest: the new answer must equal the old
// answer for every case, including the separator-boundary traps that the linear
// version was specifically written to get right.

using System.Diagnostics;
using LithicBackup.ViewModels;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "  ✓ " : "  ✗ FAIL: ") + msg);
    if (!cond) failures++;
}

static bool Linear(string path, IReadOnlyList<string> roots)
{
    foreach (var r in roots)
        if (DirectoryPrefixSet.IsPathUnderRoot(path, r))
            return true;
    return false;
}

Console.WriteLine("=== 1. hand-picked cases, including separator boundaries ===");

var roots = new List<string>
{
    @"C:\data\project",
    @"C:\fo",                 // prefix of C:\foo but NOT a parent of it
    @"D:\media\",             // trailing separator
    @"E:\",                   // drive root
};
var set = new DirectoryPrefixSet(roots);

(string Path, bool Expected, string Why)[] cases =
{
    (@"C:\data\project",            true,  "path equals a root"),
    (@"C:\data\project\a.txt",      true,  "direct child"),
    (@"C:\data\project\x\y\z.bin",  true,  "deep descendant"),
    (@"c:\DATA\Project\a.txt",      true,  "case-insensitive"),
    (@"C:\data\projectile\a.txt",   false, "sibling sharing a name prefix is NOT inside"),
    (@"C:\foo\a.txt",               false, @"C:\fo is a string prefix but not a parent"),
    (@"C:\fo\a.txt",                true,  @"C:\fo really is a parent here"),
    (@"D:\media\clip.mp4",          true,  "root stored with a trailing separator"),
    // Deliberately false, and deliberately asserted: IsPathUnderRoot compares
    // the "path IS the root" case with string equality, so a root written
    // "D:\media\" does not contain the path "D:\media". That is a quirk of the
    // original rule rather than an intent, but a performance rewrite is the wrong
    // place to change behaviour -- so the quirk is preserved and pinned here.
    (@"D:\media",                   false, "root with a trailing separator does NOT match the bare path (preserved quirk)"),
    (@"D:\mediafile.txt",           false, "trailing-separator root must not match a sibling"),
    (@"E:\anything\at\all.txt",     true,  "drive-root ancestor"),
    (@"F:\elsewhere\a.txt",         false, "unrelated volume"),
};

foreach (var (path, expected, why) in cases)
{
    bool got = set.ContainsPathUnder(path);
    bool old = Linear(path, roots);
    Check(got == expected && old == expected, $"{why}  [{path}]");
}

Console.WriteLine();
Console.WriteLine("=== 2. fuzz: new answer must equal old answer, always ===");

var rng = new Random(12345);
string[] segs = { "a", "ab", "abc", "data", "dat", "project", "projectile", "x", "X", "fo", "foo", "media" };
string RandPath()
{
    var n = rng.Next(1, 7);
    var sb = new System.Text.StringBuilder(rng.Next(0, 2) == 0 ? @"C:" : @"D:");
    for (int i = 0; i < n; i++) { sb.Append('\\'); sb.Append(segs[rng.Next(segs.Length)]); }
    if (rng.Next(0, 8) == 0) sb.Append('\\');          // stray trailing separator
    return sb.ToString();
}

int mismatches = 0, positives = 0;
for (int trial = 0; trial < 300; trial++)
{
    var fuzzRoots = new List<string>();
    for (int i = 0; i < rng.Next(1, 12); i++) fuzzRoots.Add(RandPath());
    var fuzzSet = new DirectoryPrefixSet(fuzzRoots);

    for (int j = 0; j < 200; j++)
    {
        var path = RandPath();
        bool a = Linear(path, fuzzRoots);
        bool b = fuzzSet.ContainsPathUnder(path);
        if (a) positives++;
        if (a != b)
        {
            mismatches++;
            if (mismatches <= 3)
                Console.WriteLine($"     mismatch: path={path} old={a} new={b} roots=[{string.Join(", ", fuzzRoots)}]");
        }
    }
}
Check(mismatches == 0, $"60,000 randomised cases agree ({positives:N0} were inside a root)");

Console.WriteLine();
Console.WriteLine("=== 3. memoisation must not change the answer ===");
// Rows arrive ordered by SourcePath, so the set memoises the last directory.
// Interleaving directories deliberately defeats that and must still be correct.
var memRoots = new List<string> { @"C:\keep\this", @"C:\and\that" };
var memSet = new DirectoryPrefixSet(memRoots);
int memBad = 0;
for (int i = 0; i < 20000; i++)
{
    var path = (i % 3) switch
    {
        0 => $@"C:\keep\this\f{i}.txt",
        1 => $@"C:\other\dir\f{i}.txt",
        _ => $@"C:\and\that\deep\f{i}.txt",
    };
    if (memSet.ContainsPathUnder(path) != Linear(path, memRoots)) memBad++;
}
Check(memBad == 0, "20,000 interleaved directories still agree with the linear rule");

Console.WriteLine();
Console.WriteLine("=== 4. performance, at the shape that caused the stall ===");

// A catalog's worth of paths across many directories, and a large removed-dir
// list -- the situation the live scan was in.
const int Dirs = 40_000;
const int Files = 200_000;

var bigRoots = new List<string>(Dirs);
for (int i = 0; i < Dirs; i++) bigRoots.Add($@"C:\removed\set{i % 400}\dir{i}");

var paths = new List<string>(Files);
for (int i = 0; i < Files; i++)
{
    // Ordered by path, as the real query returns them, and mostly NOT inside a
    // removed directory -- the expensive case, because a miss costs a full scan.
    int d = i / 20;
    paths.Add($@"C:\live\set{d % 400}\dir{d}\file{i}.dat");
}
paths.Sort(StringComparer.OrdinalIgnoreCase);

var sw = Stopwatch.StartNew();
int hitsOld = 0;
int probe = Math.Min(Files, 2_000);        // the old way cannot do more in sane time
for (int i = 0; i < probe; i++) if (Linear(paths[i], bigRoots)) hitsOld++;
sw.Stop();
double perFileOld = sw.Elapsed.TotalSeconds / probe;
Console.WriteLine($"  OLD  {probe:N0} files vs {Dirs:N0} dirs: {sw.Elapsed.TotalSeconds:N2}s"
                  + $"  =>  {1/perFileOld:N0} files/sec");

var bigSet = new DirectoryPrefixSet(bigRoots);
sw.Restart();
int hitsNew = 0;
for (int i = 0; i < Files; i++) if (bigSet.ContainsPathUnder(paths[i])) hitsNew++;
sw.Stop();
double perFileNew = sw.Elapsed.TotalSeconds / Files;
Console.WriteLine($"  NEW  {Files:N0} files vs {Dirs:N0} dirs: {sw.Elapsed.TotalSeconds:N2}s"
                  + $"  =>  {1/perFileNew:N0} files/sec");
Console.WriteLine($"  speedup: {perFileOld/perFileNew:N0}x");

// Same inputs, same answers.
int hitsNewProbe = 0;
for (int i = 0; i < probe; i++) if (bigSet.ContainsPathUnder(paths[i])) hitsNewProbe++;
Check(hitsOld == hitsNewProbe, $"both agree on the benchmark inputs ({hitsOld} inside)");
Check(perFileNew < perFileOld, "the new path is faster");

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

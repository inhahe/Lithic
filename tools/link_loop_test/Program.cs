// Regression test: directory links that point back into the walk's own path.
//
// The scan, the editor's size walks and the Worker's continuous backup follow
// junctions and directory symlinks. A link to the folder holding it, or to one
// above it, made each of them recurse until the path was too long for Windows
// (C:\a\b\loop\b\loop\b\...), backing the same files up under ever longer paths
// on the way. Such a link is now not followed (LinkLoopGuard); every other link
// still is, so each file is found once under the path the walk reached it by.
//
// The tree, under a temp folder T (junctions need no privilege):
//   a\b\f.txt
//   a\b\loop   -> a        back to the folder above the one holding it
//   a\up       -> T        to above where the walk starts
//   a\drive    -> T's drive root
//   a\ext      -> outside  elsewhere: followed (outside\g.txt)
//   a\fwd      -> a\b      below itself: followed, once (f.txt again)
//   a\mutual   -> m        followed (m\h.txt) ...
//   m\back     -> a        ... and back through it: not
//
// Each walk stops at a deadline or a few thousand files, so the code before
// the fix fails here instead of running for hours and filling memory.

using System.Diagnostics;
using LithicBackup.Core;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;
using LithicBackup.Infrastructure.FileSystem;

int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")}: {what}");
    if (!ok) failures++;
}

string t = Path.Combine(Path.GetTempPath(), "lithic_linkloop_" + Guid.NewGuid().ToString("N"));
string a = Path.Combine(t, "a"), b = Path.Combine(a, "b"), outside = Path.Combine(t, "outside"), m = Path.Combine(t, "m");
foreach (var d in new[] { b, outside, m })
    Directory.CreateDirectory(d);
File.WriteAllText(Path.Combine(b, "f.txt"), "f");
File.WriteAllText(Path.Combine(outside, "g.txt"), "g");
File.WriteAllText(Path.Combine(m, "h.txt"), "h");

void Junction(string link, string target)
{
    var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
    p.WaitForExit();
    if (p.ExitCode != 0 || !Directory.Exists(link))
        throw new Exception($"could not create the junction {link} -> {target}");
}
Junction(Path.Combine(b, "loop"), a);
Junction(Path.Combine(a, "up"), t);
Junction(Path.Combine(a, "drive"), Path.GetPathRoot(t)!);
Junction(Path.Combine(a, "ext"), outside);
Junction(Path.Combine(a, "fwd"), b);
Junction(Path.Combine(a, "mutual"), m);
Junction(Path.Combine(m, "back"), a);

static HashSet<string> Set(IEnumerable<string> paths) => new(paths, StringComparer.OrdinalIgnoreCase);
string P(params string[] parts) => Path.Combine([t, .. parts]);
string Show(IEnumerable<string> paths) => string.Join(", ", paths.Take(6).Select(p => p[(t.Length + 1)..])) + (paths.Count() > 6 ? $", ... ({paths.Count()})" : "");

try
{
    var fromA = Set([P("a", "b", "f.txt"), P("a", "ext", "g.txt"), P("a", "fwd", "f.txt"), P("a", "mutual", "h.txt")]);

    Console.WriteLine("=== 1. The scan of a folder holding loops ===");
    var scanner = new FileScanner(null!);   // ScanAsync does not use the catalog
    async Task<HashSet<string>> ScanAsync(string source)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var limit = new StopAfter(5000, cts);
        var files = await Task.Run(() => scanner.ScanAsync(
            [new SourceSelection { Path = source, IsDirectory = true, IsSelected = true }], limit, cts.Token));
        return Set(files.Select(f => f.FullPath));
    }
    var scanned = await ScanAsync(a);
    Check(scanned.SetEquals(fromA), $"each file once, links followed except into themselves (got {Show(scanned)})");

    Console.WriteLine();
    Console.WriteLine("=== 2. The scan of a loop link itself, selected as a source ===");
    // Its target is backed up once under it. a\b, above the start, is on the
    // walk's path, so fwd (to a\b) is a loop from here, though not from a.
    var fromLoop = Set([P("a", "b", "loop", "b", "f.txt"), P("a", "b", "loop", "ext", "g.txt"), P("a", "b", "loop", "mutual", "h.txt")]);
    var scannedLoop = await ScanAsync(Path.Combine(b, "loop"));
    Check(scannedLoop.SetEquals(fromLoop), $"its target once, nothing again through it (got {Show(scannedLoop)})");

    Console.WriteLine();
    Console.WriteLine("=== 3. The Worker's walk of a folder that appeared ===");
    // Its own small tree: .NET's enumeration queues folders breadth first, and
    // through the drive-root link a walk that loops would fill memory before
    // reaching any limit. Here one loop beside one plain link:
    //   w\b\f.txt, w\b\loop -> w, w\ext -> outside (g.txt)
    string w = P("w"), wb = P("w", "b");
    Directory.CreateDirectory(wb);
    File.WriteAllText(Path.Combine(wb, "f.txt"), "f");
    Junction(Path.Combine(wb, "loop"), w);
    Junction(Path.Combine(w, "ext"), outside);
    List<string> Walk(Func<string, bool>? prune)
    {
        var found = new List<string>();
        var sw = Stopwatch.StartNew();
        foreach (string f in LinkLoopGuard.EnumerateFiles(w, prune))
        {
            found.Add(f);
            if (found.Count > 1000 || sw.Elapsed > TimeSpan.FromSeconds(20))
                break;
        }
        return found;
    }
    var listed = Walk(null);
    Check(Set(listed).SetEquals([P("w", "b", "f.txt"), P("w", "ext", "g.txt")]) && listed.Count == 2,
        $"each file once, the loop not followed (got {Show(listed)})");
    var pruned = Walk(p => p.EndsWith(@"\ext", StringComparison.OrdinalIgnoreCase));
    Check(Set(pruned).SetEquals([P("w", "b", "f.txt")]), $"and a pruned folder is not entered (got {Show(pruned)})");
}
finally
{
    // Junctions first: deleting one removes the link, never its target.
    foreach (var j in new[] { P("a", "b", "loop"), P("a", "up"), P("a", "drive"), P("a", "ext"), P("a", "fwd"), P("a", "mutual"),
                              P("m", "back"), P("w", "b", "loop"), P("w", "ext") })
        try { Directory.Delete(j); } catch { }
    try { Directory.Delete(t, recursive: true); } catch { }
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

/// <summary>Cancels a scan once it has found more files than a correct one could.</summary>
sealed class StopAfter(int files, CancellationTokenSource cts) : IProgress<ScanProgress>
{
    public void Report(ScanProgress value)
    {
        if (value.FilesFound > files)
            cts.Cancel();
    }
}

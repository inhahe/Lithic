// Cleanup file rows used to carry their annotation inside the display name:
//
//     "config.yaml (backed up 2026-08-31 20:42)"
//
// which reads as though the file were really called that. The annotation is now
// a separate property, rendered in a dimmer colour. Two things must not have
// changed as a side effect:
//
//   1. WHAT IS SHOWN. name + annotation must still compose to the old string,
//      character for character -- only the colouring is new.
//   2. THE ORDER OF ROWS. The tree sorted by DisplayName, and the annotation was
//      part of it: several versions of one file share a name and were ordered by
//      the appended timestamp. The sort is now (DisplayName, Annotation), which
//      has to reproduce that.

using LithicBackup.ViewModels;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "  ✓ " : "  ✗ FAIL: ") + msg);
    if (!cond) failures++;
}

// How a row is rendered: the Name run followed by the AnnotationDisplay run.
static string Rendered(OrphanedFileInfo f)
{
    var node = new OrphanedNodeViewModel(f.DisplayName, f.Path, isDirectory: false)
    {
        Annotation = f.Annotation,
    };
    return node.Name + node.AnnotationDisplay;
}

Console.WriteLine("=== 1. what is shown is unchanged ===");

var when = new DateTime(2026, 8, 31, 20, 42, 0, DateTimeKind.Utc).ToLocalTime();

(string File, string? Annot, string Old)[] rows =
{
    ("config.yaml", $"(backed up {when:yyyy-MM-dd HH:mm})",
        $"config.yaml (backed up {when:yyyy-MM-dd HH:mm})"),
    ("notes.txt", $"(seeded {when:yyyy-MM-dd HH:mm})",
        $"notes.txt (seeded {when:yyyy-MM-dd HH:mm})"),
    ("plain.bin", null, "plain.bin"),
    ("has space.doc", null, "has space.doc"),
};

foreach (var (file, annot, old) in rows)
{
    var info = new OrphanedFileInfo(file, @"C:\src\" + file, 123, annot);
    Check(Rendered(info) == old, $"renders as before: \"{old}\"");
}

Check(new OrphanedNodeViewModel("f", "p", false) { Annotation = null }.AnnotationDisplay == "",
    "no annotation renders nothing (not a stray trailing space)");
Check(new OrphanedNodeViewModel("f", "p", false) { Annotation = "" }.AnnotationDisplay == "",
    "empty annotation renders nothing");

Console.WriteLine();
Console.WriteLine("=== 2. row order is unchanged ===");

// Realistic mix: several versions of a few files, plus neighbours whose names
// share a prefix -- the case where moving text out of the sort key could bite.
var rng = new Random(4242);
var names = new[] { "a.txt", "a.txt.bak", "a.txtx", "config.yaml", "config.yaml.old", "z.bin" };
int mismatches = 0;

for (int trial = 0; trial < 500; trial++)
{
    var items = new List<OrphanedFileInfo>();
    foreach (var n in names)
    {
        int versions = rng.Next(1, 4);
        for (int v = 0; v < versions; v++)
        {
            var t = new DateTime(2026, 1, 1).AddMinutes(rng.Next(0, 500000));
            string annot = rng.Next(0, 4) == 0 ? "" : $"(backed up {t:yyyy-MM-dd HH:mm})";
            items.Add(new OrphanedFileInfo(n, @"C:\s\" + n, 1,
                annot.Length == 0 ? null : annot));
        }
    }

    // Shuffle so neither ordering can be accidentally inherited from insertion.
    for (int i = items.Count - 1; i > 0; i--)
    {
        int j = rng.Next(i + 1);
        (items[i], items[j]) = (items[j], items[i]);
    }

    // OLD: a single key, annotation concatenated into the name.
    var oldOrder = items
        .OrderBy(f => f.DisplayName + (f.Annotation is null ? "" : " " + f.Annotation),
                 StringComparer.OrdinalIgnoreCase)
        .Select(Rendered)
        .ToList();

    // NEW: two keys.
    var newOrder = items
        .OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(f => f.Annotation, StringComparer.OrdinalIgnoreCase)
        .Select(Rendered)
        .ToList();

    if (!oldOrder.SequenceEqual(newOrder, StringComparer.Ordinal))
    {
        mismatches++;
        if (mismatches <= 2)
            for (int i = 0; i < oldOrder.Count; i++)
                if (oldOrder[i] != newOrder[i])
                {
                    Console.WriteLine($"     first difference at {i}:");
                    Console.WriteLine($"       old: {oldOrder[i]}");
                    Console.WriteLine($"       new: {newOrder[i]}");
                    break;
                }
    }
}

Check(mismatches == 0, $"500 shuffled row sets order identically under both keys");

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

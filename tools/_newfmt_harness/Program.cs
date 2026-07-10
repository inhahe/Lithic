// Minimal headless harness: produce a NEW-format directory backup of a source
// tree using the real LithicBackup services, so we can test lithic_restore.py
// against genuine current-format output.
//
//   newfmt_harness <sourceDir> <targetDir> <tempCatalogPath>
//
// File-level dedup ON (emits .fileref for byte-identical duplicates), block
// dedup OFF (no _blocks/.dedup), which is exactly the NEW format.

using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;
using LithicBackup.Infrastructure.Data;
using LithicBackup.Infrastructure.FileSystem;
using LithicBackup.Services;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: newfmt_harness <sourceDir> <targetDir> <tempCatalogPath>");
    return 2;
}

string source = Path.GetFullPath(args[0]);
string target = Path.GetFullPath(args[1]);
string catalogPath = Path.GetFullPath(args[2]);

Console.WriteLine($"Source : {source}");
Console.WriteLine($"Target : {target}");
Console.WriteLine($"Catalog: {catalogPath}");

var catalog = new SqliteCatalogRepository(catalogPath);
var scanner = new FileScanner(catalog);
var retention = new VersionRetentionService(catalog);
// No dedup engine passed => block-level dedup disabled => no _blocks/.dedup.
var svc = new DirectoryBackupService(catalog, scanner, retention);

var job = new BackupJob
{
    BackupSetId = null,
    Sources = { new SourceSelection { Path = source, IsDirectory = true, IsSelected = true } },
    EnableFileDeduplication = true,   // emit .fileref for exact duplicates
    EnableDeduplication = false,      // no block dedup
    TargetDirectory = target,
    ZipMode = ZipMode.None,
};

var progress = new Progress<BackupProgress>(p =>
{
    if (!string.IsNullOrEmpty(p.StatusMessage))
        Console.WriteLine($"  .. {p.StatusMessage}");
});

var result = await svc.ExecuteAsync(job, target, null, progress, CancellationToken.None);

Console.WriteLine($"Success={result.Success} Bytes={result.BytesWritten} Failed={result.FailedFiles.Count}");
foreach (var f in result.FailedFiles)
    Console.WriteLine($"  FAIL {f.Path}: {f.Error}");

return result.Success ? 0 : 1;

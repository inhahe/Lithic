using System.Security.Cryptography;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.Services;

/// <summary>
/// Repairs a directory-mode backup set's catalog so its rows match what is
/// actually on the destination. Two conservative, hash-verified repairs:
///
///   1. Flip stale <c>.fileref</c> rows to plain. A <c>.fileref</c> row says
///      "no bytes at DiscPath — the content lives as a plain copy elsewhere."
///      An older build could materialise that reference into a real, suffix-less
///      plain file without flipping the row. When a plain file exists at the
///      stripped path AND its SHA-256 matches the row's recorded hash, the row
///      is rewritten to a plain copy (<c>IsFileRef=false</c>,
///      <c>DiscPath=&lt;stripped&gt;</c>) and the redundant manifest is removed.
///
///   2. Prune genuinely-missing rows. An active row whose content cannot be
///      found on the destination in ANY form (plain file absent; <c>.dedup</c>
///      manifest absent; <c>.fileref</c> manifest absent AND no active plain copy
///      of its hash resolvable) is marked deleted so it stops inflating coverage
///      and reappearing in the cleanup scan. Pruning NEVER runs when the
///      destination directory is absent or empty — a disconnected drive must not
///      look like a mass deletion.
///
/// <see cref="AnalyzeAsync"/> is a pure dry run: it mutates nothing and returns a
/// report. <see cref="ApplyAsync"/> performs the vetted changes, re-checking each
/// one so the destination changing between the two calls can't cause data loss.
/// </summary>
public sealed class CatalogReconcileService
{
    private readonly ICatalogRepository _catalog;

    public CatalogReconcileService(ICatalogRepository catalog) => _catalog = catalog;

    /// <summary>
    /// Dry run. Loads the set's catalog and the destination tree and returns the
    /// list of stale <c>.fileref</c> rows that should be flipped to plain and the
    /// active rows whose content is missing and should be pruned. Mutates nothing.
    /// </summary>
    public async Task<ReconcileReport> AnalyzeAsync(
        int backupSetId, string targetDirectory,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var report = new ReconcileReport();

        progress?.Report("Loading catalog...");
        var all = await _catalog.GetAllFilesForBackupSetAsync(backupSetId, ct);
        report.RecordsExamined = all.Count;

        // Guard: a disconnected/empty destination must never look like mass
        // deletion. Flips only fire on files that DO exist, so they're always
        // safe; prunes are suppressed unless the destination is present.
        report.TargetPresent = DirectoryHasEntries(targetDirectory);

        var active = all.Where(r => !r.IsDeleted).ToList();
        int total = active.Count;

        // hash -> resolved active plain path (mirrors VerifyService's cache).
        var resolvedPlain = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // Records chosen for flipping — the prune pass treats these as "will be
        // present" rather than missing.
        var flipIds = new HashSet<long>();

        // --- Pass 1: flip stale filerefs. ---
        int processed = 0;
        foreach (var r in active)
        {
            ct.ThrowIfCancellationRequested();
            Report(progress, ref processed, total, "Checking for materialised references");

            if (!r.IsFileRef || r.IsZipped || r.IsSplit)
                continue;

            string stripped = StripFileRefSuffix(r.DiscPath);
            if (string.Equals(stripped, r.DiscPath, StringComparison.Ordinal))
                continue; // nothing to strip — not a .fileref path

            string strippedAbs = Path.Combine(targetDirectory, stripped);
            // Cheap gate: a genuine reference has its manifest at DiscPath, NOT a
            // plain file at the stripped path, so this is false for healthy rows.
            if (!File.Exists(strippedAbs))
                continue;

            // A plain file physically sits where the reference resolves. Only flip
            // when its bytes actually ARE the referenced content.
            string? actual = await ComputeFileHashAsync(strippedAbs, ct);
            if (actual is null || !HashEquals(actual, r.Hash))
                continue;

            report.Flips.Add(new FlipCandidate
            {
                Record = r,
                OldDiscPath = r.DiscPath,
                NewDiscPath = stripped,
                SizeBytes = r.SizeBytes,
            });
            flipIds.Add(r.Id);
        }

        // --- Pass 2: prune genuinely-missing rows (only if target present). ---
        if (report.TargetPresent)
        {
            processed = 0;
            foreach (var r in active)
            {
                ct.ThrowIfCancellationRequested();
                Report(progress, ref processed, total, "Checking for missing content");

                if (r.IsZipped || r.IsSplit)
                    continue; // disc-format rows aren't part of a directory destination
                if (flipIds.Contains(r.Id))
                    continue; // becomes a present plain copy

                string? reason = null;
                if (r.IsFileRef)
                {
                    if (File.Exists(Path.Combine(targetDirectory, r.DiscPath)))
                        continue; // manifest present
                    if (!resolvedPlain.TryGetValue(r.Hash, out string? plain))
                    {
                        plain = await ResolvePlainContentPathAsync(backupSetId, r.Hash, targetDirectory, ct);
                        resolvedPlain[r.Hash] = plain;
                    }
                    if (plain is not null && File.Exists(plain))
                        continue; // content still resolvable via another plain copy
                    reason = "file reference: manifest missing and no plain copy of its content remains";
                }
                else if (r.IsDeduped)
                {
                    if (File.Exists(Path.Combine(targetDirectory, r.DiscPath)))
                        continue;
                    reason = "dedup manifest missing from destination";
                }
                else
                {
                    if (File.Exists(Path.Combine(targetDirectory, r.DiscPath)))
                        continue;
                    reason = "plain file missing from destination";
                }

                report.Prunes.Add(new PruneCandidate
                {
                    Record = r,
                    DiscPath = r.DiscPath,
                    Reason = reason,
                    SizeBytes = r.SizeBytes,
                });
            }
        }

        progress?.Report("Analysis complete.");
        return report;
    }

    /// <summary>
    /// Apply a report produced by <see cref="AnalyzeAsync"/>. Every change is
    /// re-verified against the current destination before it is committed, so a
    /// file reappearing (or a drive reconnecting) between analyze and apply can
    /// only cause a change to be skipped, never data to be lost.
    /// </summary>
    public async Task<ReconcileApplyResult> ApplyAsync(
        int backupSetId, ReconcileReport report, string targetDirectory,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        int flipped = 0, pruned = 0, skipped = 0;

        int i = 0;
        foreach (var f in report.Flips)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Flipping references {++i:N0}/{report.Flips.Count:N0}...");

            string strippedAbs = Path.Combine(targetDirectory, f.NewDiscPath);
            if (!File.Exists(strippedAbs)) { skipped++; continue; } // plain vanished — leave the row alone

            f.Record.IsFileRef = false;
            f.Record.DiscPath = f.NewDiscPath;
            await _catalog.UpdateFileRecordAsync(f.Record, ct);
            flipped++;

            // Remove the now-redundant .fileref manifest, if it's still there.
            try
            {
                string oldAbs = Path.Combine(targetDirectory, f.OldDiscPath);
                if (!string.Equals(oldAbs, strippedAbs, StringComparison.OrdinalIgnoreCase))
                    ForceDeleteFile(oldAbs);
            }
            catch { /* a leftover manifest is harmless; the row is already correct */ }
        }

        if (report.TargetPresent)
        {
            i = 0;
            foreach (var p in report.Prunes)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Pruning missing rows {++i:N0}/{report.Prunes.Count:N0}...");

                // Re-verify the content is STILL missing before marking deleted.
                var r = p.Record;
                if (File.Exists(Path.Combine(targetDirectory, r.DiscPath))) { skipped++; continue; }
                if (r.IsFileRef)
                {
                    var plain = await ResolvePlainContentPathAsync(backupSetId, r.Hash, targetDirectory, ct);
                    if (plain is not null && File.Exists(plain)) { skipped++; continue; }
                }

                r.IsDeleted = true;
                await _catalog.UpdateFileRecordAsync(r, ct);
                pruned++;
            }
        }

        progress?.Report("Reconcile complete.");
        return new ReconcileApplyResult { Flipped = flipped, Pruned = pruned, Skipped = skipped };
    }

    // ------------------------------------------------------------------

    private async Task<string?> ResolvePlainContentPathAsync(
        int backupSetId, string hash, string targetDirectory, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(hash))
            return null;
        var candidates = await _catalog.GetActiveRecordsByHashAsync(backupSetId, hash, ct);
        var plain = candidates.FirstOrDefault(r => !r.IsFileRef && !r.IsDeduped);
        return plain is null ? null : Path.Combine(targetDirectory, plain.DiscPath);
    }

    private static async Task<string?> ComputeFileHashAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1 << 20, useAsync: true);
            byte[] hash = await SHA256.HashDataAsync(stream, ct);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static string StripFileRefSuffix(string discPath)
        => discPath.EndsWith(".fileref", StringComparison.OrdinalIgnoreCase)
            ? discPath[..^".fileref".Length]
            : discPath;

    private static bool HashEquals(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool DirectoryHasEntries(string dir)
    {
        try
        {
            return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();
        }
        catch
        {
            return false;
        }
    }

    private static void ForceDeleteFile(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) return;
        if (fi.IsReadOnly) fi.IsReadOnly = false;
        fi.Delete();
    }

    private static void Report(IProgress<string>? p, ref int processed, int total, string phase)
    {
        processed++;
        if (p is null) return;
        if (processed % 500 == 0 || processed == total)
            p.Report($"{phase}: {processed:N0}/{total:N0}");
    }
}

/// <summary>Dry-run result from <see cref="CatalogReconcileService.AnalyzeAsync"/>.</summary>
public sealed class ReconcileReport
{
    public int RecordsExamined { get; set; }

    /// <summary>
    /// Whether the destination directory was present and non-empty. When false,
    /// prune candidates are suppressed (only flips, which are always safe, are
    /// reported) so a disconnected drive can't trigger mass deletion.
    /// </summary>
    public bool TargetPresent { get; set; }

    public List<FlipCandidate> Flips { get; } = new();
    public List<PruneCandidate> Prunes { get; } = new();

    public bool HasChanges => Flips.Count > 0 || Prunes.Count > 0;
}

/// <summary>A stale <c>.fileref</c> row that should be rewritten to a plain copy.</summary>
public sealed class FlipCandidate
{
    public required FileRecord Record { get; init; }
    public required string OldDiscPath { get; init; }
    public required string NewDiscPath { get; init; }
    public long SizeBytes { get; init; }
}

/// <summary>An active row whose content is missing and should be marked deleted.</summary>
public sealed class PruneCandidate
{
    public required FileRecord Record { get; init; }
    public required string DiscPath { get; init; }
    public required string Reason { get; init; }
    public long SizeBytes { get; init; }
}

/// <summary>Outcome of <see cref="CatalogReconcileService.ApplyAsync"/>.</summary>
public sealed class ReconcileApplyResult
{
    public int Flipped { get; set; }
    public int Pruned { get; set; }
    public int Skipped { get; set; }
}

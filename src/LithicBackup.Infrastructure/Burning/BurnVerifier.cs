using System.Diagnostics;
using System.Security.Cryptography;
using LithicBackup.Core.Exceptions;
using LithicBackup.Core.Interfaces;

namespace LithicBackup.Infrastructure.Burning;

/// <summary>
/// Post-burn read-back verification: re-reads every file that was just written
/// to a disc and confirms its content matches the source that was staged for
/// the burn. Catches a disc that wrote successfully but recorded corrupt or
/// truncated data (a bad blank, a dirty laser, a flaky drive).
///
/// <para>
/// The check is filesystem-agnostic: for each burned item it locates the file by
/// its disc-relative path on the mounted disc and compares SHA-256 hashes against
/// the original source bytes. A missing file, a size mismatch, or a hash mismatch
/// is a verification failure. Any failures throw a <see cref="BurnException"/>
/// summarising them. Working from the item list (rather than walking the disc)
/// means it verifies correctly whether the source was a temp staging copy or an
/// in-place original held under a read lock.
/// </para>
/// </summary>
internal static class BurnVerifier
{
    /// <summary>
    /// Verify that every file in <paramref name="items"/> exists on the disc
    /// mounted at <paramref name="discRootPath"/> with identical content.
    /// </summary>
    /// <param name="totalBytes">Total burn size, used only for progress percentage.</param>
    /// <param name="stopwatch">Burn stopwatch, used only for elapsed reporting.</param>
    /// <exception cref="BurnException">Thrown if any file fails to verify.</exception>
    public static async Task VerifyAsync(
        IReadOnlyList<BurnItem> items,
        string discRootPath,
        long totalBytes,
        Stopwatch stopwatch,
        IProgress<BurnProgress>? progress,
        CancellationToken ct)
    {
        if (!Directory.Exists(discRootPath))
            throw new BurnException(
                $"Verification failed: the burned disc is not readable at {discRootPath}.");

        var failures = new List<string>();
        long bytesVerified = 0;
        long denom = totalBytes > 0 ? totalBytes : 1;

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            string rel = item.DiscRelativePath;
            string sourcePath = item.SourceAbsolutePath;
            string discPath = Path.Combine(discRootPath, rel);
            var sourceInfo = new FileInfo(sourcePath);

            progress?.Report(new BurnProgress
            {
                CurrentFile = $"Verifying: {Path.GetFileName(sourcePath)}",
                BytesWritten = Math.Min(bytesVerified, totalBytes),
                TotalBytes = totalBytes,
                Percentage = Math.Min((double)bytesVerified / denom * 100, 100),
                Elapsed = stopwatch.Elapsed,
            });

            if (!File.Exists(discPath))
            {
                failures.Add($"{rel}: missing on disc");
                bytesVerified += sourceInfo.Length;
                continue;
            }

            var discInfo = new FileInfo(discPath);
            if (discInfo.Length != sourceInfo.Length)
            {
                failures.Add(
                    $"{rel}: size mismatch (source {sourceInfo.Length:N0} B, disc {discInfo.Length:N0} B)");
                bytesVerified += sourceInfo.Length;
                continue;
            }

            string sourceHash = await HashAsync(sourcePath, ct);
            string discHash = await HashAsync(discPath, ct);
            if (!string.Equals(sourceHash, discHash, StringComparison.OrdinalIgnoreCase))
                failures.Add($"{rel}: content mismatch (disc copy differs from source)");

            bytesVerified += sourceInfo.Length;

            // Cap the failure list so a wholesale-bad disc doesn't build a
            // megabyte-long message; the count below still reflects the total.
            if (failures.Count >= 50)
            {
                failures.Add("... (further mismatches not listed)");
                break;
            }
        }

        progress?.Report(new BurnProgress
        {
            CurrentFile = failures.Count == 0 ? "Verification complete" : "Verification failed",
            BytesWritten = totalBytes,
            TotalBytes = totalBytes,
            Percentage = 100,
            Elapsed = stopwatch.Elapsed,
        });

        if (failures.Count > 0)
        {
            throw new BurnException(
                "Post-burn verification failed — the disc does not match the source:\n"
                + string.Join("\n", failures));
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

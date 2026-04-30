using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.Services;

/// <summary>
/// Decides whether to write a new disc, append via multisession, or erase and rewrite.
/// </summary>
public class DiscSessionStrategy : IDiscSessionStrategy
{
    private readonly IDiscBurner _burner;
    private readonly ICatalogRepository _catalog;

    public DiscSessionStrategy(IDiscBurner burner, ICatalogRepository catalog)
    {
        _burner = burner;
        _catalog = catalog;
    }

    public async Task<SessionDecision> DecideAsync(
        string recorderId, int backupSetId, CancellationToken ct = default)
    {
        var media = await _burner.GetMediaInfoAsync(recorderId, ct);

        // Blank disc -> just write.
        if (media.IsBlank)
        {
            return new SessionDecision
            {
                Action = SessionAction.WriteNewDisc,
                Reason = "Disc is blank.",
            };
        }

        // Has free space and supports multisession -> append.
        if (media.FreeSpaceBytes > 0)
        {
            bool canMultisession = await _burner.CanMultisessionAsync(recorderId, ct);
            if (canMultisession)
            {
                return new SessionDecision
                {
                    Action = SessionAction.AppendMultisession,
                    Reason = $"Disc has {media.FreeSpaceBytes:N0} bytes free and supports multisession.",
                };
            }
        }

        // Full and rewritable -> erase and rewrite if under max rewrite count.
        if (media.IsRewritable)
        {
            int maxRewrites = GetMaxRewriteCount(media.MediaType);

            // Find the disc record in the catalog that matches this backup set.
            var discs = await _catalog.GetDiscsForBackupSetAsync(backupSetId, ct);
            var existingDisc = discs
                .Where(d => d.Status == BurnSessionStatus.Completed && !d.IsBad)
                .OrderByDescending(d => d.SequenceNumber)
                .FirstOrDefault();

            if (existingDisc is not null && existingDisc.RewriteCount < maxRewrites)
            {
                return new SessionDecision
                {
                    Action = SessionAction.EraseAndRewrite,
                    ExistingDiscId = existingDisc.Id,
                    Reason = $"Disc is full and rewritable (rewrite {existingDisc.RewriteCount + 1}/{maxRewrites}).",
                };
            }
        }

        // Default: user must insert a new disc.
        return new SessionDecision
        {
            Action = SessionAction.WriteNewDisc,
            Reason = "Disc is full or not rewritable; insert a new disc.",
        };
    }

    /// <summary>
    /// Maximum number of erase/rewrite cycles for each media type.
    /// </summary>
    private static int GetMaxRewriteCount(MediaType mediaType) => mediaType switch
    {
        MediaType.CD => 100,
        MediaType.DVD => 1000,
        MediaType.BluRay => 10000,
        MediaType.MDisc => 0, // M-Disc is write-once
        _ => 100,
    };
}

using LithicBackup.Core.Models;

namespace LithicBackup.Core.Interfaces;

/// <summary>
/// Decides whether to write a new disc, append via multisession, or erase and rewrite.
/// </summary>
public interface IDiscSessionStrategy
{
    Task<SessionDecision> DecideAsync(string recorderId, int backupSetId, CancellationToken ct = default);
}

public enum SessionAction
{
    WriteNewDisc,
    AppendMultisession,
    EraseAndRewrite,
}

public class SessionDecision
{
    public SessionAction Action { get; init; }
    public int? ExistingDiscId { get; init; }
    public string Reason { get; init; } = "";
}

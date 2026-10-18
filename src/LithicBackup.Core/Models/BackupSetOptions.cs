namespace LithicBackup.Core.Models;

/// <summary>
/// A backup set's id, name and options, without its selection tree: what a caller
/// that polls the sets for their destinations needs. A type of its own, so it can
/// never be handed to a writer of whole sets (design.md: "A trimmed projection
/// must not reach a whole-row writer").
/// </summary>
/// <param name="Id">The set's id.</param>
/// <param name="Name">The set's name.</param>
/// <param name="JobOptions">The set's saved options, or null for a set that has none.</param>
public sealed record BackupSetOptions(int Id, string Name, JobOptions? JobOptions);

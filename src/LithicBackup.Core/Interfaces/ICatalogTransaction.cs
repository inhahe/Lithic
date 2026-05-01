namespace LithicBackup.Core.Interfaces;

/// <summary>
/// A catalog transaction that must be explicitly completed before disposal.
/// Disposing without calling <see cref="Complete"/> rolls back uncommitted changes.
/// </summary>
public interface ICatalogTransaction : IDisposable
{
    /// <summary>
    /// Marks the transaction as successfully completed.
    /// The next call to <see cref="IDisposable.Dispose"/> will commit.
    /// </summary>
    void Complete();
}

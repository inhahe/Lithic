namespace LithicBackup.Core.Models;

/// <summary>
/// Action to take when a file fails to back up.
/// </summary>
public enum BurnFailureAction
{
    /// <summary>Skip this file and continue.</summary>
    Skip,

    /// <summary>Retry this file.</summary>
    Retry,

    /// <summary>Zip this file (may resolve path/name incompatibilities) and retry.</summary>
    Zip,

    /// <summary>Skip all remaining failures on this disc.</summary>
    SkipAllForDisc,

    /// <summary>Zip all remaining failures on this disc (resolves filesystem incompatibilities).</summary>
    ZipAllForDisc,

    /// <summary>Skip all remaining failures permanently.</summary>
    SkipAllPermanently,

    /// <summary>Abort the entire backup operation.</summary>
    Abort,
}

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

    /// <summary>Skip all remaining failures on this disc and try them on the next disc.</summary>
    SkipAllForDisc,

    /// <summary>Skip all remaining failures permanently.</summary>
    SkipAllPermanently,
}

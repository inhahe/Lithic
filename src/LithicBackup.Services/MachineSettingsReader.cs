namespace LithicBackup.Services;

/// <summary>
/// The machine settings as last read: read again whenever the file has been
/// written since, and kept as they were while it cannot be read, because a broken
/// file is not the defaults. The Worker reads through one, and so does the app
/// when it builds a job, so each sees a save as soon as it is made and neither
/// falls back to the defaults because one read failed.
/// </summary>
/// <remarks>
/// Not thread-safe: one per thread that builds jobs (the Worker's poll loop, the
/// app's UI thread).
/// </remarks>
public sealed class MachineSettingsReader
{
    private readonly string? _path;
    private bool _haveRead;

    /// <param name="path">The machine settings file; null for the real one.</param>
    public MachineSettingsReader(string? path = null) => _path = path;

    /// <summary>The settings in force: the last that were read, the defaults before any.</summary>
    public MachineSettings Current { get; private set; } = new();

    /// <summary>
    /// The file's write time, taken just before <see cref="Current"/> was read, or
    /// null before the first read or when it could not be told. A save that lands
    /// between the two makes it older than the content, never newer, so a
    /// comparison with it can only read again needlessly, not miss a save.
    /// </summary>
    public DateTime? CurrentWrittenUtc { get; private set; }

    /// <summary>Whether the file could not be read at the last look.</summary>
    public bool Unreadable { get; private set; }

    /// <summary>
    /// Read the file again if it has been written since the last read, or could
    /// not be read then. One file-time query when nothing has changed.
    /// </summary>
    /// <returns>True when the file has just become unreadable, so that a caller
    /// can say so once rather than at every look.</returns>
    public bool Refresh()
    {
        var written = MachineSettings.LastWriteTimeUtc(_path);
        if (_haveRead && !Unreadable && written is not null && written == CurrentWrittenUtc)
            return false;

        if (MachineSettings.TryLoad(out var settings, _path) == MachineSettings.LoadResult.Unreadable)
        {
            bool newly = !Unreadable;
            Unreadable = true;
            return newly;
        }

        Current = settings;
        CurrentWrittenUtc = written;
        _haveRead = true;
        Unreadable = false;
        return false;
    }
}

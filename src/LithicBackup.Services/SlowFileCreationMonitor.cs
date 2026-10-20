using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LithicBackup.Services;

/// <summary>
/// Notices when creating files on the destination is pathologically slow, and
/// says once, in words the user can act on, why it may be.
///
/// <para>Measured, on a real backup: a folder on the destination held 1.4
/// million files whose names all began "unknown artist - ", on a drive with
/// 8.3 short-name creation on. Windows looks for an unused short alias for every
/// new name, and in a folder that full that took 779 ms per file - against 7 ms
/// in an empty folder on the same drive, and 4.2 ms in the same folder once
/// short names were turned off. The backup copied 1.8 GB in nine hours with
/// nothing on screen to say why, and the estimate it showed was 62 days. The
/// cost is in creating names, not in moving bytes, so it is timed exactly there:
/// opening the temp file and promoting it into place.</para>
/// </summary>
public sealed class SlowFileCreationMonitor
{
    /// <summary>Files averaged over before deciding: one slow file proves nothing.</summary>
    public const int Window = 32;

    /// <summary>
    /// The average per file that counts as pathological. Creating and renaming a
    /// file takes single-digit milliseconds on an SSD and tens on a busy hard
    /// disk; a quarter of a second, sustained, is something wrong.
    /// </summary>
    public static readonly TimeSpan Threshold = TimeSpan.FromMilliseconds(250);

    private readonly Action<string> _onSlow;
    private readonly Func<string, bool?> _shortNamesEnabled;
    private readonly Queue<(double Ms, string Directory)> _recent = new();
    private readonly object _lock = new();
    private double _sumMs;
    private bool _raised;

    /// <param name="onSlow">Called once, with the message to show and log.</param>
    /// <param name="shortNamesEnabled">
    /// Whether a path's volume makes 8.3 short names (null when it cannot be
    /// told); <see cref="ShortNameSetting.IsCreationEnabled"/> unless a test
    /// says otherwise.
    /// </param>
    public SlowFileCreationMonitor(Action<string> onSlow, Func<string, bool?>? shortNamesEnabled = null)
    {
        _onSlow = onSlow;
        _shortNamesEnabled = shortNamesEnabled ?? ShortNameSetting.IsCreationEnabled;
    }

    /// <summary>Whether the warning has been given.</summary>
    public bool HasWarned
    {
        get { lock (_lock) return _raised; }
    }

    /// <summary>
    /// Record what creating one file at <paramref name="destPath"/> cost: opening
    /// its temp file plus promoting it into place.
    /// </summary>
    public void Record(string destPath, TimeSpan elapsed)
    {
        string? directory;
        double average;
        lock (_lock)
        {
            if (_raised)
                return;
            _recent.Enqueue((elapsed.TotalMilliseconds, Path.GetDirectoryName(destPath) ?? destPath));
            _sumMs += elapsed.TotalMilliseconds;
            if (_recent.Count > Window)
                _sumMs -= _recent.Dequeue().Ms;
            if (_recent.Count < Window || _sumMs / Window < Threshold.TotalMilliseconds)
                return;

            _raised = true;
            average = _sumMs / Window;
            // Where the time went: the folder that cost the most in the window.
            directory = _recent.GroupBy(r => r.Directory, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Sum(r => r.Ms))
                .First().Key;
        }

        bool? shortNames;
        try { shortNames = _shortNamesEnabled(directory); }
        catch { shortNames = null; }
        _onSlow(Describe(directory, TimeSpan.FromMilliseconds(average), shortNames));
    }

    /// <summary>The warning, for a folder where creating a file takes <paramref name="perFile"/>.</summary>
    public static string Describe(string directory, TimeSpan perFile, bool? shortNamesEnabled)
    {
        string root = Path.GetPathRoot(directory) ?? directory;
        string drive = root.Length >= 2 && root[1] == ':' ? root[..2] : root.TrimEnd('\\');
        string head = $"Creating files on {drive} is taking about {perFile.TotalMilliseconds:N0} ms each "
                      + $"(most of that in \"{directory}\").";
        return shortNamesEnabled == true
            ? head + $" Windows makes an 8.3 short name for every new file on {drive}, which gets very slow "
                   + "in a folder that holds a great many files. Turning that off can make this a hundred "
                   + $"times faster: run \"fsutil 8dot3name set {drive} 1\" in an administrator terminal. "
                   + "Files already there keep their short names; nothing else changes."
            : head + " The drive or its connection may be slow, or something on this computer (an antivirus "
                   + "scanner, say) may be checking every new file.";
    }
}

/// <summary>Whether a volume makes 8.3 short names for new files.</summary>
public static class ShortNameSetting
{
    /// <summary>
    /// True when the volume holding <paramref name="path"/> (an existing file or
    /// folder) creates 8.3 short names, false when it does not, null when it
    /// cannot be told. Asked through a handle to the folder itself, which needs no
    /// administrator rights - <c>fsutil 8dot3name query</c> opens the volume, and
    /// is refused unelevated on fixed disks. Checked against fsutil where it
    /// answers, and against the short name Windows actually gives a new long-named
    /// file (<c>tools\targeted_progress_test</c> §18).
    /// </summary>
    public static bool? IsCreationEnabled(string path)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            using var handle = CreateFileW(path, 0, FileShareAll, IntPtr.Zero, OpenExisting,
                BackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid)
                return null;
            var query = new PersistentVolumeInformation { Version = 1, FlagMask = ShortNameCreationDisabled };
            var answer = new PersistentVolumeInformation();
            if (!DeviceIoControl(handle, FsctlQueryPersistentVolumeState,
                    ref query, Marshal.SizeOf<PersistentVolumeInformation>(),
                    ref answer, Marshal.SizeOf<PersistentVolumeInformation>(), out _, IntPtr.Zero))
                return null;
            return (answer.VolumeFlags & ShortNameCreationDisabled) == 0;
        }
        catch
        {
            return null;
        }
    }

    private const uint FsctlQueryPersistentVolumeState = 0x0009023C;
    private const uint ShortNameCreationDisabled = 0x1;   // PERSISTENT_VOLUME_STATE_SHORT_NAME_CREATION_DISABLED
    private const uint FileShareAll = 7;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;      // needed to open a directory

    [StructLayout(LayoutKind.Sequential)]
    private struct PersistentVolumeInformation
    {
        public uint VolumeFlags;
        public uint FlagMask;
        public uint Version;
        public uint Reserved;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint code,
        ref PersistentVolumeInformation input, int inputSize,
        ref PersistentVolumeInformation output, int outputSize,
        out int returned, IntPtr overlapped);
}

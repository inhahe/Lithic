using System.Security.Cryptography;
using System.Text;
using LithicBackup.Infrastructure.FileSystem;

namespace LithicBackup.Services;

/// <summary>
/// Makes what a run wrote to its destination durable before the catalog records
/// it. A write is "done" once its bytes are in the Windows cache; a drive that
/// drops off before the lazy writer puts them on the disk loses them, and a
/// catalog that had already recorded them says they are backed up when they are
/// not.
/// </summary>
public interface IDestinationFlush
{
    /// <summary>
    /// Make durable what a run wrote under <paramref name="destination"/> since its
    /// last commit: <paramref name="written"/> at least.
    /// </summary>
    /// <param name="destination">The run's destination folder.</param>
    /// <param name="written">
    /// The files and folders the run wrote or renamed (to) there since its last
    /// commit, by full path; not empty. One may be gone since (renamed again
    /// within the same batch).
    /// </param>
    /// <exception cref="IOException">
    /// It could not - the drive is gone, or failed. What was written must not be
    /// recorded; the next run copies it again.
    /// </exception>
    Task FlushAsync(string destination, IReadOnlyCollection<string> written, CancellationToken ct);
}

/// <summary>
/// The flush every run uses. Each volume written to is flushed whole, in one go:
/// by this process when it may (the service; an elevated app), else by the
/// service on its behalf (<see cref="VolumeFlushPipe"/>). What is on no volume
/// that can be flushed whole (a network share), or on one neither may flush (no
/// service running), is flushed file by file, one device flush each.
/// </summary>
public sealed class DestinationFlush : IDestinationFlush
{
    /// <summary>
    /// After the service has not answered, how long it is taken to be absent: the
    /// pipe's connect timeout is not paid on every commit of a run while the
    /// service is stopped.
    /// </summary>
    private static readonly TimeSpan ServiceAbsentFor = TimeSpan.FromMinutes(1);

    private readonly string _pipeName;

    /// <summary>UTC ticks until which the service is taken to be absent.</summary>
    private long _serviceAbsentUntil;

    public DestinationFlush() : this(VolumeFlushPipe.Name) { }

    /// <param name="pipeName">The service's pipe; another than the service's only in tests.</param>
    public DestinationFlush(string pipeName) => _pipeName = pipeName;

    public async Task FlushAsync(string destination, IReadOnlyCollection<string> written, CancellationToken ct)
    {
        var (byVolume, eachFile) = await Task.Run(() => PlaceOnVolumes(written), ct).ConfigureAwait(false);
        foreach (var (volume, paths) in byVolume)
        {
            if (!await TryFlushVolumeAsync(volume, ct).ConfigureAwait(false))
                eachFile.AddRange(paths);
        }
        if (eachFile.Count > 0)
            await Task.Run(() => FlushEach(destination, eachFile, ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <paramref name="written"/> grouped by the volume each is on, found from the
    /// folder it was written in (a junction inside the destination can lead to
    /// another volume), and those on none.
    /// </summary>
    private static (Dictionary<string, List<string>> ByVolume, List<string> OnNone) PlaceOnVolumes(
        IReadOnlyCollection<string> written)
    {
        var byVolume = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var onNone = new List<string>();
        foreach (var inFolder in written.Distinct(StringComparer.OrdinalIgnoreCase)
                     .GroupBy(path => Path.GetDirectoryName(path) ?? path, StringComparer.OrdinalIgnoreCase))
        {
            string? volume = DurableFlush.VolumeOf(inFolder.Key, out _);
            if (volume is null)
                onNone.AddRange(inFolder);
            else if (byVolume.TryGetValue(volume, out var paths))
                paths.AddRange(inFolder);
            else
                byVolume[volume] = inFolder.ToList();
        }
        return (byVolume, onNone);
    }

    /// <summary>Flush <paramref name="volume"/> here or through the service; false when neither did.</summary>
    private async Task<bool> TryFlushVolumeAsync(string volume, CancellationToken ct)
    {
        var here = await Task.Run(() => DurableFlush.TryFlushVolume(volume, out _), ct).ConfigureAwait(false);
        if (here == DurableFlush.Outcome.Flushed)
            return true;
        if (here != DurableFlush.Outcome.NotPermitted
            || DateTime.UtcNow.Ticks < Interlocked.Read(ref _serviceAbsentUntil))
            return false;

        var (outcome, _) = await VolumeFlushPipe.RequestAsync(volume, ct, _pipeName).ConfigureAwait(false);
        if (outcome is null)
            Interlocked.Exchange(ref _serviceAbsentUntil, (DateTime.UtcNow + ServiceAbsentFor).Ticks);
        return outcome == DurableFlush.Outcome.Flushed;
    }

    /// <summary>
    /// Flush each of <paramref name="written"/>, one device flush each: slow for
    /// many small files, so only what no volume flush covered. A path gone since
    /// it was written is passed over while its folder is there (a copy renamed
    /// into the history within the batch: its new name is in the list too). Gone
    /// with its folder, or with the destination, it went with its drive, and so
    /// may everything written to it.
    /// </summary>
    private static void FlushEach(string destination, IReadOnlyCollection<string> written, CancellationToken ct)
    {
        if (!Directory.Exists(destination))
            throw new IOException($"The backup's destination {destination} is gone: its writes are not durable.");
        foreach (string path in written)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                string? folder = Path.GetDirectoryName(path);
                if (folder is not null && Directory.Exists(folder))
                    continue;
                throw new IOException($"The backup's copy {path} is gone, and its folder with it: its writes are not durable.");
            }
            if (!DurableFlush.TryFlushPath(path, out string? why))
                throw new IOException($"The backup's copy {path} could not be made durable: {why}");
        }
    }
}

/// <summary>
/// The files and folders a run has written or renamed (to) on its destination
/// since its last durable point, noted where each write happens and made
/// durable before each commit records them (<see cref="MakeDurableAsync"/>).
/// Held for the run's flow (an AsyncLocal), so concurrent runs keep their own
/// and the many places that write need no parameter for it.
/// </summary>
/// <remarks>
/// It also watches every volume the run writes to (<see cref="VolumeWatch"/>, held
/// on the volume's root, which no one can delete, or on the folder written in when
/// the root may not be opened): the destination's from <see cref="Begin"/>,
/// another's (through a junction inside the destination) from the first write on
/// it. A watch that has seen its volume go is renewed at the
/// first write after a durable point, when nothing the run wrote was at risk; one
/// that cannot be held then fails the commit, which cannot tell what the volume
/// lost.
/// </remarks>
internal sealed class DestinationWrites : IDisposable
{
    private static readonly AsyncLocal<DestinationWrites?> Current = new();

    private readonly string _destination;
    private readonly SuspectWrites? _suspect;
    private readonly object _gate = new();

    /// <summary>What was noted since the last durable point.</summary>
    private readonly List<string> _pending = [];

    /// <summary>
    /// The watch of each volume written to, by volume name - or, for the
    /// destination on a file system without volume names (a share), by its
    /// path - with the root and the folder it may hold. Null while neither could
    /// be held.
    /// </summary>
    private readonly Dictionary<string, (string Root, string Folder, VolumeWatch? Watch)> _watches =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Folders written in since the last durable point whose volume has been
    /// looked up, so each is looked up once a batch.
    /// </summary>
    private readonly HashSet<string> _placedFolders = new(StringComparer.OrdinalIgnoreCase);

    private DestinationWrites(string destination, SuspectWrites? suspect)
    {
        _destination = destination;
        _suspect = suspect;
        string root = DurableFlush.VolumeOf(destination, out _) ?? destination;
        _watches[root] = (root, destination, Hold(root, destination));
    }

    /// <summary>
    /// A watch on <paramref name="root"/>, or on <paramref name="folder"/> when
    /// the root may not be opened (an ACL that admits users to a folder only).
    /// </summary>
    private static VolumeWatch? Hold(string root, string folder) => VolumeWatch.Open(root) ?? VolumeWatch.Open(folder);

    /// <summary>
    /// Start noting for a run writing under <paramref name="destination"/> (an
    /// existing folder), in the calling flow and everything it calls or starts.
    /// Not async on purpose: the value is set in the caller's flow.
    /// </summary>
    /// <param name="suspect">
    /// The destination's writes that earlier runs never made durable: what this
    /// run makes durable is no longer among them.
    /// </param>
    public static DestinationWrites Begin(string destination, SuspectWrites? suspect = null)
    {
        var writes = new DestinationWrites(destination, suspect);
        Current.Value = writes;
        return writes;
    }

    /// <summary>
    /// Note <paramref name="path"/>, a file or folder the current run has just
    /// written or renamed something to on its destination. Nothing outside a run.
    /// </summary>
    public static void Note(string path) => Current.Value?.Add(path);

    /// <summary>
    /// Make durable now, not at the next commit, what the current run has noted:
    /// before something that cannot be taken back, such as deleting the bytes
    /// just copied elsewhere.
    /// </summary>
    /// <exception cref="IOException">As <see cref="MakeDurableAsync"/>: what was to be deleted must stay.</exception>
    /// <exception cref="InvalidOperationException">Outside a run: nothing noted the write.</exception>
    public static Task MakeDurableNowAsync(IDestinationFlush flush, CancellationToken ct) =>
        (Current.Value ?? throw new InvalidOperationException("A write to a destination outside a run: nothing noted it."))
            .MakeDurableAsync(flush, ct);

    /// <summary>What is noted and not yet durable.</summary>
    public List<string> Pending
    {
        get
        {
            lock (_gate)
                return [.. _pending];
        }
    }

    private void Add(string path)
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
                RenewLostWatches();
            _pending.Add(path);
            string? folder = Path.GetDirectoryName(path);
            if (folder is not null && _placedFolders.Add(folder))
                WatchVolumeOf(folder);
        }
    }

    /// <summary>Watch the volume <paramref name="folder"/> is on, when it has a name and no watch yet.</summary>
    private void WatchVolumeOf(string folder)
    {
        string? volume = DurableFlush.VolumeOf(folder, out _);
        if (volume is not null && !_watches.ContainsKey(volume))
            _watches[volume] = (volume, folder, Hold(volume, folder));
    }

    /// <summary>
    /// Renew each watch whose volume went since it was opened, or that could not
    /// be held: called when nothing written is pending, so the loss cost nothing.
    /// </summary>
    private void RenewLostWatches()
    {
        foreach (var (key, (root, folder, watch)) in _watches.ToList())
        {
            if (watch is not null && !watch.WasLost(out _))
                continue;
            watch?.Dispose();
            _watches[key] = (root, folder, Hold(root, folder));
        }
    }

    /// <summary>
    /// Make durable what was noted since the last durable point, before a commit
    /// records it.
    /// </summary>
    /// <exception cref="IOException">
    /// It could not be; or a volume written to went away at some point since it
    /// was first watched, taking with it whatever was in the cache then, though it
    /// may be back; or one could not be watched. Either way the commit must not
    /// happen. What was noted stays noted, for the next attempt.
    /// </exception>
    public async Task MakeDurableAsync(IDestinationFlush flush, CancellationToken ct)
    {
        string[] written;
        lock (_gate)
            written = [.. _pending];
        if (written.Length == 0)
            return;
        await flush.FlushAsync(_destination, written, ct).ConfigureAwait(false);
        lock (_gate)
        {
            foreach (var (_, folder, watch) in _watches.Values)
            {
                string? why = null;
                if (watch is null || watch.WasLost(out why))
                    throw new IOException(
                        $"The drive holding {folder} went away during the backup ({why ?? "it could not be held"}). "
                        + "What was written to it since the last save of the catalog may be lost, so it is not recorded.");
            }
            // Only Add adds, at the end: the first ones are these.
            _pending.RemoveRange(0, written.Length);
            if (_pending.Count == 0)
                _placedFolders.Clear();
        }
        _suspect?.Durable(written);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var (_, _, watch) in _watches.Values)
                watch?.Dispose();
            _watches.Clear();
        }
        if (Current.Value == this)
            Current.Value = null;
    }
}

/// <summary>
/// The writes to a destination that a run never made durable: its last batch
/// could not be flushed (the drive had gone), so the batch was not recorded,
/// but what it wrote may still be there - its names on the disk, its data
/// perhaps not. A later run must neither record such a copy as a version of its
/// own (the "catalog behind the drive" case of the move into <c>_prev</c>) nor
/// take such a block as stored. A path stops being suspect once a run has
/// written it again and made that durable.
/// </summary>
/// <remarks>
/// Kept per destination folder, in the catalog's folder (on a drive that did not
/// go), one full path a line; shared by every set backing up there, as the block
/// store is. A run reads it when it starts and writes back what it changed when
/// it ends, merged under an exclusive open with what other runs wrote meanwhile.
/// The newest <see cref="MaxPaths"/> are kept.
/// </remarks>
internal sealed class SuspectWrites
{
    private const int MaxPaths = 100_000;

    private readonly string _file;
    private readonly string _destination;
    private readonly object _gate = new();
    private readonly HashSet<string> _paths;
    private readonly HashSet<string> _added = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _cleared = new(StringComparer.OrdinalIgnoreCase);

    private SuspectWrites(string file, string destination, HashSet<string> paths)
    {
        _file = file;
        _destination = destination;
        _paths = paths;
    }

    /// <summary>
    /// The suspect writes recorded for <paramref name="destination"/>; none when
    /// there are none, or they cannot be read.
    /// </summary>
    /// <param name="catalogFolder">The catalog's folder (<c>ICatalogRepository.DataDirectory</c>).</param>
    public static SuspectWrites Load(string catalogFolder, string destination)
    {
        string key = Path.GetFullPath(destination).TrimEnd('\\').ToUpperInvariant();
        string name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16] + ".txt";
        string file = Path.Combine(catalogFolder, "unflushed", name);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Another run may be writing it back; it holds it a moment.
        WithRetry(() =>
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            paths.UnionWith(ReadPaths(stream));
        });
        return new SuspectWrites(file, destination, paths);
    }

    public bool IsEmpty
    {
        get
        {
            lock (_gate)
                return _paths.Count == 0;
        }
    }

    /// <summary>Whether <paramref name="path"/> was written by a batch never made durable.</summary>
    public bool Contains(string path)
    {
        lock (_gate)
            return _paths.Count > 0 && _paths.Contains(Path.GetFullPath(path));
    }

    /// <summary>These were not made durable: suspect from now on.</summary>
    public void Add(IEnumerable<string> paths)
    {
        lock (_gate)
        {
            foreach (string path in paths.Select(Path.GetFullPath))
            {
                _paths.Add(path);
                _added.Add(path);
                _cleared.Remove(path);
            }
        }
    }

    /// <summary>These were written again and made durable: no longer suspect.</summary>
    public void Durable(IEnumerable<string> paths)
    {
        lock (_gate)
        {
            if (_paths.Count == 0)
                return;
            foreach (string path in paths.Select(Path.GetFullPath))
            {
                if (_paths.Remove(path))
                    _cleared.Add(path);
                _added.Remove(path);
            }
        }
    }

    /// <summary>
    /// Write back what this run changed, merged with what is there now. Best
    /// effort: an addition that cannot be recorded leaves its writes to be taken
    /// as they are, as before there was a record.
    /// </summary>
    public void Save()
    {
        string[] added, cleared;
        lock (_gate)
        {
            if (_added.Count == 0 && _cleared.Count == 0)
                return;
            added = [.. _added];
            cleared = [.. _cleared];
        }
        bool saved = WithRetry(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            using var stream = new FileStream(_file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var paths = ReadPaths(stream).ToList();
            var gone = new HashSet<string>(cleared, StringComparer.OrdinalIgnoreCase);
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var kept = paths.Where(p => !gone.Contains(p) && have.Add(p)).ToList();
            kept.AddRange(added.Where(have.Add));
            stream.Position = 0;
            stream.SetLength(0);
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 65536, leaveOpen: true))
            {
                writer.WriteLine($"# Writes to {_destination} that were never made durable.");
                foreach (string path in kept.Skip(Math.Max(0, kept.Count - MaxPaths)))
                    writer.WriteLine(path);
            }
            stream.Flush(flushToDisk: true);
        });
        if (saved)
        {
            lock (_gate)
            {
                _added.ExceptWith(added);
                _cleared.ExceptWith(cleared);
            }
        }
    }

    private static IEnumerable<string> ReadPaths(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 65536, leaveOpen: true);
        while (reader.ReadLine() is { } line)
            if (line.Length > 0 && !line.StartsWith('#'))
                yield return line;
    }

    /// <summary>
    /// Do <paramref name="action"/> on the file, waiting out another run that holds
    /// it (for a second at most); false when it could not be done. A file that is
    /// not there is nothing to read, and not a failure.
    /// </summary>
    private static bool WithRetry(Action action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return true;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(50);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}

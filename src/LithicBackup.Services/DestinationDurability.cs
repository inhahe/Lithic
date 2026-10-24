using System.Collections.Concurrent;
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
    /// it was written is passed over (a copy renamed into the history within the
    /// batch: its new name is in the list too), but only while the destination
    /// itself is there: when it is not, the drive is gone, and so may be
    /// everything written to it.
    /// </summary>
    private static void FlushEach(string destination, IReadOnlyCollection<string> written, CancellationToken ct)
    {
        if (!Directory.Exists(destination))
            throw new IOException($"The backup's destination {destination} is gone: its writes are not durable.");
        foreach (string path in written)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(path) && !Directory.Exists(path))
                continue;
            if (!DurableFlush.TryFlushPath(path, out string? why))
                throw new IOException($"The backup's copy {path} could not be made durable: {why}");
        }
    }
}

/// <summary>
/// The files and folders a run has written or renamed (to) on its destination
/// since its last catalog commit, noted where each write happens and made
/// durable at each commit (<see cref="MakeDurableAsync"/>). Held for the run's
/// flow (an AsyncLocal), so concurrent runs keep their own and the many places
/// that write need no parameter for it.
/// </summary>
internal sealed class DestinationWrites : IDisposable
{
    private static readonly AsyncLocal<DestinationWrites?> Current = new();

    private readonly string _destination;
    private readonly VolumeWatch? _watch;
    private readonly ConcurrentQueue<string> _pending = new();

    private DestinationWrites(string destination)
    {
        _destination = destination;
        _watch = VolumeWatch.Open(destination);
    }

    /// <summary>
    /// Start noting for a run writing under <paramref name="destination"/> (an
    /// existing folder), in the calling flow and everything it calls or starts.
    /// Not async on purpose: the value is set in the caller's flow.
    /// </summary>
    public static DestinationWrites Begin(string destination)
    {
        var writes = new DestinationWrites(destination);
        Current.Value = writes;
        return writes;
    }

    /// <summary>
    /// Note <paramref name="path"/>, a file or folder the current run has just
    /// written or renamed something to on its destination. Nothing outside a run.
    /// </summary>
    public static void Note(string path) => Current.Value?._pending.Enqueue(path);

    /// <summary>
    /// Make durable now, not at the next commit, what the current run has noted,
    /// <paramref name="path"/> among it: before something that cannot be taken
    /// back, such as deleting the bytes <paramref name="path"/> was copied from.
    /// Outside a run, <paramref name="path"/> alone.
    /// </summary>
    /// <exception cref="IOException">As <see cref="MakeDurableAsync"/>: the bytes it was copied from must stay.</exception>
    public static Task MakeDurableNowAsync(IDestinationFlush flush, string destination, string path, CancellationToken ct) =>
        Current.Value is { } writes
            ? writes.MakeDurableAsync(flush, ct)
            : flush.FlushAsync(destination, [path], ct);

    /// <summary>
    /// Make durable what was noted since the last time this succeeded, before a
    /// commit records it.
    /// </summary>
    /// <exception cref="IOException">
    /// It could not be; or the destination's volume went away at some point since
    /// <see cref="Begin"/>, taking with it whatever was in the cache then, though
    /// it may be back. Either way the commit must not happen. What was noted stays
    /// noted, for the next attempt.
    /// </exception>
    public async Task MakeDurableAsync(IDestinationFlush flush, CancellationToken ct)
    {
        var written = _pending.ToArray();
        if (written.Length == 0)
            return;
        await flush.FlushAsync(_destination, written, ct).ConfigureAwait(false);
        if (_watch is not null && _watch.WasLost(out string? why))
            throw new IOException(
                $"The backup's destination {_destination} went away during the backup ({why}). "
                + "What was written to it since the last save of the catalog may be lost, so it is not recorded.");
        // Only Note adds, at the end, and only this takes: the first ones are these.
        for (int i = 0; i < written.Length; i++)
            _pending.TryDequeue(out _);
    }

    public void Dispose()
    {
        _watch?.Dispose();
        if (Current.Value == this)
            Current.Value = null;
    }
}

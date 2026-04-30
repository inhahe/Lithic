using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using LithicBackup.Core.Exceptions;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.Infrastructure.Burning;

/// <summary>
/// <see cref="IDiscBurner"/> implementation using IMAPI2 COM interop.
///
/// All COM property/method access (except IDiscMaster2) uses <c>dynamic</c>
/// late-binding to avoid vtable ordering issues inherent in manual COM interop.
/// Long-running operations run on dedicated STA threads.
/// </summary>
public class Imapi2DiscBurner : IDiscBurner
{
    // -------------------------------------------------------------------
    // GetRecorderIds
    // -------------------------------------------------------------------

    public IReadOnlyList<string> GetRecorderIds()
    {
        try
        {
            var master = (IDiscMaster2)new MsftDiscMaster2();
            var ids = new List<string>();
            for (int i = 0; i < master.Count; i++)
                ids.Add(master[i]);
            return ids;
        }
        catch (COMException)
        {
            return [];
        }
    }

    // -------------------------------------------------------------------
    // GetMediaInfoAsync
    // -------------------------------------------------------------------

    public Task<MediaInfo> GetMediaInfoAsync(string recorderId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return StaThread.RunAsync(() =>
        {
            dynamic recorder = new MsftDiscRecorder2();
            recorder.InitializeDiscRecorder(recorderId);

            string recorderName;
            try
            {
                string vendor = recorder.VendorId ?? "";
                string product = recorder.ProductId ?? "";
                recorderName = $"{vendor.Trim()} {product.Trim()}".Trim();
            }
            catch
            {
                recorderName = recorderId;
            }

            dynamic format2Data = new MsftDiscFormat2Data();

            // Check if the recorder is supported at all.
            if (!(bool)format2Data.IsRecorderSupported((object)recorder))
            {
                return new MediaInfo
                {
                    MediaType = MediaType.Unknown,
                    RecorderName = recorderName,
                };
            }

            format2Data.Recorder = (object)recorder;

            // Check if usable media is inserted.
            bool mediaSupported;
            try
            {
                mediaSupported = (bool)format2Data.IsCurrentMediaSupported((object)recorder);
            }
            catch (COMException)
            {
                // No media inserted or unreadable media.
                return new MediaInfo
                {
                    MediaType = MediaType.Unknown,
                    RecorderName = recorderName,
                };
            }

            if (!mediaSupported)
            {
                return new MediaInfo
                {
                    MediaType = MediaType.Unknown,
                    RecorderName = recorderName,
                };
            }

            var physicalType = (ImapiMediaPhysicalType)(int)format2Data.CurrentPhysicalMediaType;
            int totalSectors = (int)format2Data.TotalSectorsOnMedia;
            int freeSectors = (int)format2Data.FreeSectorsOnMedia;

            bool isBlank;
            try { isBlank = (bool)format2Data.MediaPhysicallyBlank; }
            catch { isBlank = freeSectors == totalSectors; }

            // Estimate session count from sector addresses.
            int sessionCount = 0;
            if (!isBlank)
            {
                try
                {
                    int prevStart = (int)format2Data.StartAddressOfPreviousSession;
                    sessionCount = prevStart > 0 ? 1 : 0;
                    // Rough estimate — IMAPI2 doesn't directly expose session count.
                    // A more precise count would need IDiscFormat2Data's session info.
                    if (totalSectors > freeSectors && freeSectors > 0)
                        sessionCount = Math.Max(sessionCount, 1);
                }
                catch { sessionCount = isBlank ? 0 : 1; }
            }

            return new MediaInfo
            {
                MediaType = MapMediaType(physicalType),
                IsBlank = isBlank,
                IsRewritable = IsRewritable(physicalType),
                TotalCapacityBytes = (long)totalSectors * Imapi2Constants.SectorSize,
                FreeSpaceBytes = (long)freeSectors * Imapi2Constants.SectorSize,
                SessionCount = sessionCount,
                RecorderName = recorderName,
            };
        });
    }

    // -------------------------------------------------------------------
    // BurnAsync
    // -------------------------------------------------------------------

    public Task BurnAsync(
        string recorderId,
        string sourceDirectory,
        BurnOptions options,
        IProgress<BurnProgress>? progress = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!Directory.Exists(sourceDirectory))
            throw new ArgumentException($"Source directory does not exist: {sourceDirectory}");

        return StaThread.RunAsync(() =>
        {
            var stopwatch = Stopwatch.StartNew();

            // 1. Initialize the recorder.
            dynamic recorder = new MsftDiscRecorder2();
            recorder.InitializeDiscRecorder(recorderId);

            // 2. Build the file system image.
            dynamic fsi = new MsftFileSystemImage();
            fsi.FileSystemsToCreate = (int)MapFsiFileSystems(options.FilesystemType);
            fsi.VolumeName = "DISCBURN";

            // Set media capacity from the device so the image doesn't exceed it.
            try { fsi.SetMaxMediaBlocksFromDevice((object)recorder); }
            catch (COMException) { /* Some devices don't support this; image will be
                                      validated at burn time instead. */ }

            // Add all files from the staging directory.
            dynamic root = fsi.Root;
            root.AddTree(sourceDirectory, false);

            // Create the result image (IStream).
            ct.ThrowIfCancellationRequested();
            dynamic resultImage = fsi.CreateResultImage();
            dynamic imageStream = resultImage.ImageStream;
            long totalBytes = (long)(int)resultImage.TotalBlocks * (int)resultImage.BlockSize;

            // 3. Set up the disc writer.
            dynamic format2Data = new MsftDiscFormat2Data();
            format2Data.Recorder = (object)recorder;
            format2Data.ClientName = "LithicBackup";
            format2Data.ForceMediaToBeClosed = !options.Multisession;

            // Set write speed (-1 = max speed in IMAPI2).
            try { format2Data.SetWriteSpeed(-1, false); }
            catch (COMException) { /* Device may not support speed control. */ }

            // 4. Hook up progress events via IConnectionPointContainer.
            IConnectionPoint? connectionPoint = null;
            int cookie = 0;
            BurnDataEventSink? eventSink = null;

            try
            {
                if (progress is not null)
                {
                    var container = (IConnectionPointContainer)format2Data;
                    var eventsGuid = Imapi2Guids.DDiscFormat2DataEvents;
                    container.FindConnectionPoint(ref eventsGuid, out connectionPoint!);

                    eventSink = new BurnDataEventSink(progress, stopwatch, totalBytes, ct);
                    connectionPoint.Advise(eventSink, out cookie);
                }

                // 5. Burn.
                ct.ThrowIfCancellationRequested();
                format2Data.Write((object)imageStream);
            }
            catch (COMException ex) when (ex.HResult == unchecked((int)0xC0AA0002))
            {
                // E_IMAPI_REQUEST_CANCELLED — mapped from CancellationToken.
                ct.ThrowIfCancellationRequested();
                throw;
            }
            finally
            {
                // Unhook events.
                if (connectionPoint is not null && cookie != 0)
                {
                    try { connectionPoint.Unadvise(cookie); } catch { }
                }
            }

            // 6. Report final progress.
            stopwatch.Stop();
            progress?.Report(new BurnProgress
            {
                CurrentFile = "Complete",
                BytesWritten = totalBytes,
                TotalBytes = totalBytes,
                Percentage = 100.0,
                Elapsed = stopwatch.Elapsed,
                EstimatedRemaining = TimeSpan.Zero,
            });
        });
    }

    // -------------------------------------------------------------------
    // EraseAsync
    // -------------------------------------------------------------------

    public Task EraseAsync(string recorderId, bool fullErase = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return StaThread.RunAsync(() =>
        {
            dynamic recorder = new MsftDiscRecorder2();
            recorder.InitializeDiscRecorder(recorderId);

            dynamic eraser = new MsftDiscFormat2Erase();
            eraser.Recorder = (object)recorder;
            eraser.FullErase = fullErase;
            eraser.ClientName = "LithicBackup";

            // Erase is synchronous and can take several minutes for a full erase.
            eraser.EraseMedia();
        });
    }

    // -------------------------------------------------------------------
    // CanMultisessionAsync
    // -------------------------------------------------------------------

    public Task<bool> CanMultisessionAsync(string recorderId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return StaThread.RunAsync(() =>
        {
            dynamic recorder = new MsftDiscRecorder2();
            recorder.InitializeDiscRecorder(recorderId);

            dynamic format2Data = new MsftDiscFormat2Data();

            if (!(bool)format2Data.IsRecorderSupported((object)recorder))
                return false;

            format2Data.Recorder = (object)recorder;

            try
            {
                if (!(bool)format2Data.IsCurrentMediaSupported((object)recorder))
                    return false;

                // Check if there's free space and the media supports additional sessions.
                int freeSectors = (int)format2Data.FreeSectorsOnMedia;
                if (freeSectors <= 0)
                    return false;

                bool isBlank = (bool)format2Data.MediaPhysicallyBlank;
                if (isBlank)
                    return true; // Blank media can always start a multisession.

                // Non-blank media: check if the disc was left open for more sessions.
                // If NextWritableAddress is valid, multisession is possible.
                int nextWritable = (int)format2Data.NextWritableAddress;
                return nextWritable > 0;
            }
            catch (COMException)
            {
                return false;
            }
        });
    }

    // -------------------------------------------------------------------
    // Media type mapping
    // -------------------------------------------------------------------

    private static MediaType MapMediaType(ImapiMediaPhysicalType physicalType) => physicalType switch
    {
        ImapiMediaPhysicalType.CdRom or
        ImapiMediaPhysicalType.CdR or
        ImapiMediaPhysicalType.CdRw
            => MediaType.CD,

        ImapiMediaPhysicalType.DvdRom or
        ImapiMediaPhysicalType.DvdRam or
        ImapiMediaPhysicalType.DvdPlusR or
        ImapiMediaPhysicalType.DvdPlusRw or
        ImapiMediaPhysicalType.DvdPlusRDualLayer or
        ImapiMediaPhysicalType.DvdDashR or
        ImapiMediaPhysicalType.DvdDashRw or
        ImapiMediaPhysicalType.DvdDashRDualLayer or
        ImapiMediaPhysicalType.DvdPlusRwDualLayer or
        ImapiMediaPhysicalType.HdDvdRom or
        ImapiMediaPhysicalType.HdDvdR or
        ImapiMediaPhysicalType.HdDvdRam
            => MediaType.DVD,

        ImapiMediaPhysicalType.BdRom or
        ImapiMediaPhysicalType.BdR or
        ImapiMediaPhysicalType.BdRe
            => MediaType.BluRay,

        // Note: M-Disc is reported as standard BD-R or DVD+R by IMAPI2.
        // Detection requires checking the disc's media ID string, which
        // is not directly exposed. For now, M-Disc is reported as its
        // underlying media type.

        _ => MediaType.Unknown,
    };

    private static bool IsRewritable(ImapiMediaPhysicalType physicalType) => physicalType switch
    {
        ImapiMediaPhysicalType.CdRw or
        ImapiMediaPhysicalType.DvdRam or
        ImapiMediaPhysicalType.DvdPlusRw or
        ImapiMediaPhysicalType.DvdDashRw or
        ImapiMediaPhysicalType.DvdPlusRwDualLayer or
        ImapiMediaPhysicalType.HdDvdRam or
        ImapiMediaPhysicalType.BdRe
            => true,
        _ => false,
    };

    private static FsiFileSystems MapFsiFileSystems(FilesystemType type) => type switch
    {
        FilesystemType.ISO9660 => FsiFileSystems.ISO9660,
        FilesystemType.Joliet => FsiFileSystems.Joliet | FsiFileSystems.ISO9660,
        FilesystemType.UDF => FsiFileSystems.UDF,
        _ => FsiFileSystems.UDF,
    };

    // -------------------------------------------------------------------
    // Event sinks
    // -------------------------------------------------------------------

    /// <summary>
    /// Receives burn progress callbacks from IMAPI2 via the
    /// DDiscFormat2DataEvents connection point.
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    [Guid("4B115B81-6B1A-4E5C-B6DC-DAF9B4E5A7A3")]
    internal class BurnDataEventSink : DDiscFormat2DataEvents
    {
        private readonly IProgress<BurnProgress> _progress;
        private readonly Stopwatch _stopwatch;
        private readonly long _totalBytes;
        private readonly CancellationToken _ct;

        public BurnDataEventSink(
            IProgress<BurnProgress> progress,
            Stopwatch stopwatch,
            long totalBytes,
            CancellationToken ct)
        {
            _progress = progress;
            _stopwatch = stopwatch;
            _totalBytes = totalBytes;
            _ct = ct;
        }

        public void Update(object sender, object args)
        {
            // If cancellation was requested, cancel the write operation.
            // IMAPI2 checks for this via the event handler returning E_ABORT,
            // but we can also call CancelWrite on the sender.
            if (_ct.IsCancellationRequested)
            {
                try
                {
                    dynamic s = sender;
                    s.CancelWrite();
                }
                catch { }
                return;
            }

            try
            {
                dynamic p = args;
                int elapsedSeconds = (int)p.ElapsedTime;
                int remainingSeconds = (int)p.EstimatedRemainingTime;
                int sectorCount = (int)p.SectorCount;
                int lastWrittenLba = (int)p.LastWrittenLba;
                int startLba = (int)p.StartLba;

                int sectorsWritten = lastWrittenLba - startLba;
                long bytesWritten = (long)sectorsWritten * Imapi2Constants.SectorSize;
                double percentage = sectorCount > 0
                    ? (double)sectorsWritten / sectorCount * 100.0
                    : 0;

                _progress.Report(new BurnProgress
                {
                    CurrentFile = $"Sector {lastWrittenLba:N0}",
                    BytesWritten = bytesWritten,
                    TotalBytes = _totalBytes,
                    Percentage = Math.Min(percentage, 100.0),
                    Elapsed = _stopwatch.Elapsed,
                    EstimatedRemaining = remainingSeconds > 0
                        ? TimeSpan.FromSeconds(remainingSeconds)
                        : null,
                });
            }
            catch
            {
                // Don't let a progress reporting error kill the burn.
            }
        }
    }
}

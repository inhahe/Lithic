using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace LithicBackup.Infrastructure.Burning;

// -----------------------------------------------------------------------
// IMAPI2 COM interop declarations.
//
// Strategy: CoClasses are [ComImport] with correct CLSIDs so we can
// instantiate them with `new`. All property/method access on the resulting
// objects goes through `dynamic` (IDispatch late-binding) to avoid vtable
// ordering issues. Only IDiscMaster2 uses a strongly-typed interface
// because its layout is simple and known to work.
//
// Progress events use IConnectionPointContainer with event sink classes
// that implement the dispinterfaces.
// -----------------------------------------------------------------------

// --- CoClasses (CLSIDs from imapi2.idl / imapi2fs.idl) ---

[ComImport, Guid("E2B4A659-7CB1-4A36-9B04-2AB90F6B9026")]
internal class MsftDiscMaster2 { }

[ComImport, Guid("2735412A-7F64-5B0F-8F00-5D77AFBE261E")]
internal class MsftDiscRecorder2 { }

[ComImport, Guid("27354130-7F64-5B0F-8F00-5D77AFBE261E")]
internal class MsftDiscFormat2Data { }

[ComImport, Guid("2735412B-7F64-5B0F-8F00-5D77AFBE261E")]
internal class MsftDiscFormat2Erase { }

[ComImport, Guid("2C941FE1-975B-59BE-A960-9A2A262853A5")]
internal class MsftFileSystemImage { }

// --- IDiscMaster2 (simple enough to use strongly-typed) ---

[ComImport, Guid("27354210-7F64-5B0F-8F00-5D77AFBE261E")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IDiscMaster2
{
    [DispId(0)]
    string this[int index] { get; }

    [DispId(1)]
    int Count { get; }

    [DispId(2)]
    bool IsSupportedEnvironment { get; }
}

// --- Progress event dispinterfaces ---

/// <summary>
/// Connection point interface for IDiscFormat2Data burn progress.
/// IMAPI2 calls Update() on our sink via IDispatch::Invoke during Write().
/// </summary>
[ComImport, Guid("2735413C-7F64-5B0F-8F00-5D77AFBE261E")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface DDiscFormat2DataEvents
{
    [DispId(0x200)]
    void Update(
        [MarshalAs(UnmanagedType.IDispatch)] object sender,
        [MarshalAs(UnmanagedType.IDispatch)] object args);
}

/// <summary>
/// Connection point interface for IDiscFormat2Erase progress.
/// </summary>
[ComImport, Guid("2735413A-7F64-5B0F-8F00-5D77AFBE261E")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface DDiscFormat2EraseEvents
{
    [DispId(0x200)]
    void Update(
        [MarshalAs(UnmanagedType.IDispatch)] object sender,
        int elapsedSeconds,
        int estimatedTotalSeconds);
}

// --- Enums ---

[Flags]
internal enum FsiFileSystems
{
    None = 0,
    ISO9660 = 1,
    Joliet = 2,
    UDF = 4,
}

/// <summary>
/// IMAPI_MEDIA_PHYSICAL_TYPE values returned by CurrentPhysicalMediaType.
/// </summary>
internal enum ImapiMediaPhysicalType
{
    Unknown = 0,
    CdRom = 1,
    CdR = 2,
    CdRw = 3,
    DvdRom = 4,
    DvdRam = 5,
    DvdPlusR = 6,
    DvdPlusRw = 7,
    DvdPlusRDualLayer = 8,
    DvdDashR = 9,
    DvdDashRw = 10,
    DvdDashRDualLayer = 11,
    Disk = 12,
    DvdPlusRwDualLayer = 13,
    HdDvdRom = 14,
    HdDvdR = 15,
    HdDvdRam = 16,
    BdRom = 17,
    BdR = 18,
    BdRe = 19,
}

// --- Static helpers ---

internal static class Imapi2Guids
{
    /// <summary>IID for DDiscFormat2DataEvents connection point.</summary>
    public static Guid DDiscFormat2DataEvents = new("2735413C-7F64-5B0F-8F00-5D77AFBE261E");

    /// <summary>IID for DDiscFormat2EraseEvents connection point.</summary>
    public static Guid DDiscFormat2EraseEvents = new("2735413A-7F64-5B0F-8F00-5D77AFBE261E");
}

/// <summary>Sector size for data mode discs.</summary>
internal static class Imapi2Constants
{
    public const int SectorSize = 2048;
}

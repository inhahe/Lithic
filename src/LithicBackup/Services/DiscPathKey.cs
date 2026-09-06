using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace LithicBackup.Services;

/// <summary>
/// Turns a destination-relative path into a 128-bit key, so the destination
/// scan's lookup can hold a fixed 16 bytes per path instead of the path string.
///
/// <para><b>Why hashing is safe here, which is not obvious and was initially
/// rejected.</b> Three separate arguments, and the second is the one that
/// matters:</para>
///
/// <list type="number">
/// <item><b>The odds.</b> With 2.8M paths and 128 bits, the birthday bound gives
/// a collision probability of about N²/2^129 ≈ 1.2e-26 — many orders of magnitude
/// below the chance of an undetected memory or disk error during the same scan.</item>
///
/// <item><b>A collision could not cause a wrongful deletion even if one
/// happened.</b> The lookup aggregates with "active wins": any active catalog
/// record for a path sets <c>HasActive</c> and it is never cleared. A file whose
/// own path has an active record therefore always reads back as active, whatever
/// else collides with it, so it can never be offered for deletion. Every possible
/// collision outcome is the *other* direction — a file that should have been
/// reported as untracked or catalog-deleted is skipped instead. The failure mode
/// is "miss an orphan", never "delete a real backup".</item>
///
/// <item><b>Precedent.</b> The product already stakes real data on hash equality:
/// file-level deduplication keeps ONE copy when two files' SHA-256 match. A
/// 128-bit key used only to answer "is this path in the catalog?" is a far
/// weaker claim than that.</item>
/// </list>
///
/// <para><b>Case-insensitivity is preserved exactly.</b> The previous lookup used
/// <see cref="StringComparer.OrdinalIgnoreCase"/>, which compares the
/// per-character invariant uppercase forms; this uppercases per character with
/// <see cref="char.ToUpperInvariant(char)"/> before hashing, which is the same
/// operation. Getting that wrong would be the one genuinely dangerous mistake —
/// a path that no longer matches its own catalog entry would be reported as
/// untracked and offered for deletion — so it is done by construction rather
/// than by hoping <c>ToUpperInvariant</c> on the whole string agrees.</para>
///
/// <para>SHA-256 truncated to its first 16 bytes, rather than a faster
/// non-cryptographic hash, because it needs no extra package, is hardware
/// accelerated on any current CPU, and removes every question about distribution.
/// The cost is invisible next to a walk that stats a million files.</para>
/// </summary>
internal static class DiscPathKey
{
    /// <summary>Longest path handled without renting; covers essentially every real path.</summary>
    private const int StackBudget = 320;

    public static UInt128 From(string path) => From(path.AsSpan());

    /// <summary>
    /// Key for a destination-relative path. Normalises <c>/</c> to <c>\</c> and
    /// upper-cases invariantly as it goes, so neither step allocates a string.
    /// </summary>
    public static UInt128 From(ReadOnlySpan<char> path)
    {
        char[]? rented = null;
        Span<char> buffer = path.Length <= StackBudget
            ? stackalloc char[StackBudget]
            : (rented = ArrayPool<char>.Shared.Rent(path.Length));

        try
        {
            for (int i = 0; i < path.Length; i++)
            {
                char c = path[i];
                buffer[i] = char.ToUpperInvariant(c == '/' ? '\\' : c);
            }

            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(MemoryMarshal.AsBytes(buffer[..path.Length]), digest);

            // First 16 bytes. Truncating a cryptographic digest keeps the
            // uniformity of the full one, which is what the birthday bound above
            // assumes.
            return new UInt128(
                BinaryPrimitives.ReadUInt64LittleEndian(digest),
                BinaryPrimitives.ReadUInt64LittleEndian(digest[8..]));
        }
        finally
        {
            if (rented is not null)
                ArrayPool<char>.Shared.Return(rented);
        }
    }
}

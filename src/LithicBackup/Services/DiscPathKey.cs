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
public static class DiscPathKey
{
    /// <summary>Longest path handled without renting; covers essentially every real path.</summary>
    private const int StackBudget = 320;

    public static UInt128 From(string path) => From(path.AsSpan());

    /// <summary>
    /// Key for a path assembled from parts, without building the string.
    ///
    /// <para>The destination walk holds a directory's relative path and each
    /// entry's file name separately, and needs the key for
    /// <c>dir + "\\" + name</c> — plus, on a miss, for that path with
    /// <c>.fileref</c> and <c>.dedup</c> appended. Composing through
    /// <see cref="From(string)"/> allocated up to three strings per file and
    /// discarded them immediately, since the overwhelming majority of files match
    /// on the first probe and never need their path materialised at all.</para>
    ///
    /// <para>Identical by construction to hashing the concatenation: the
    /// normalisation is per character, so where the characters come from cannot
    /// change the result. The equivalence is asserted in
    /// <c>tools\dest_walk_test</c>.</para>
    /// </summary>
    /// <param name="dir">Relative directory, may be empty for the root.</param>
    /// <param name="name">File or directory name.</param>
    /// <param name="suffix">Optional suffix, e.g. <c>.fileref</c>.</param>
    public static UInt128 From(
        ReadOnlySpan<char> dir, ReadOnlySpan<char> name, ReadOnlySpan<char> suffix = default)
    {
        int total = name.Length + suffix.Length + (dir.Length == 0 ? 0 : dir.Length + 1);

        char[]? rented = null;
        Span<char> buffer = total <= StackBudget
            ? stackalloc char[StackBudget]
            : (rented = ArrayPool<char>.Shared.Rent(total));

        try
        {
            int at = 0;
            if (dir.Length != 0)
            {
                at += Normalise(dir, buffer);
                buffer[at++] = '\\';
            }
            at += Normalise(name, buffer[at..]);
            if (suffix.Length != 0)
                at += Normalise(suffix, buffer[at..]);

            return Digest(buffer[..at]);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Copy <paramref name="src"/> into <paramref name="dest"/> with <c>/</c>
    /// folded to <c>\</c> and invariant upper-casing, per character. Shared by
    /// both overloads so the two can never normalise differently.
    /// </summary>
    private static int Normalise(ReadOnlySpan<char> src, Span<char> dest)
    {
        for (int i = 0; i < src.Length; i++)
        {
            char c = src[i];
            dest[i] = char.ToUpperInvariant(c == '/' ? '\\' : c);
        }
        return src.Length;
    }

    private static UInt128 Digest(ReadOnlySpan<char> normalised)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(MemoryMarshal.AsBytes(normalised), digest);

        // First 16 bytes. Truncating a cryptographic digest keeps the uniformity
        // of the full one, which is what the birthday bound above assumes.
        return new UInt128(
            BinaryPrimitives.ReadUInt64LittleEndian(digest),
            BinaryPrimitives.ReadUInt64LittleEndian(digest[8..]));
    }

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
            Normalise(path, buffer);
            return Digest(buffer[..path.Length]);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<char>.Shared.Return(rented);
        }
    }
}

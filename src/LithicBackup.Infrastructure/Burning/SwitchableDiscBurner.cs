using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.Infrastructure.Burning;

/// <summary>
/// An <see cref="IDiscBurner"/> that delegates to either a real hardware burner
/// or a <see cref="SimulatedDiscBurner"/>, chosen at runtime by
/// <see cref="UseSimulated"/>.
///
/// <para>
/// Used in <c>--test-mode</c>: the app injects this single shared instance
/// everywhere a burner is needed (orchestrator, session strategy, view models),
/// and the UI's "use simulated burner" checkbox flips <see cref="UseSimulated"/>.
/// It defaults to <c>false</c> so that, even in test mode, real hardware is used
/// until the user explicitly opts in to simulation.
/// </para>
/// </summary>
public sealed class SwitchableDiscBurner : IDiscBurner
{
    private readonly IDiscBurner _real;

    public SwitchableDiscBurner(IDiscBurner real, SimulatedDiscBurner simulated)
    {
        _real = real;
        Simulated = simulated;
    }

    /// <summary>The simulated burner, exposed so the UI can arm its failure
    /// injection and storage-mode knobs regardless of which burner is active.</summary>
    public SimulatedDiscBurner Simulated { get; }

    /// <summary>When <c>true</c>, all operations route to the simulated burner;
    /// otherwise to the real hardware burner. Defaults to <c>false</c>.</summary>
    public bool UseSimulated { get; set; }

    private IDiscBurner Active => UseSimulated ? Simulated : _real;

    public IReadOnlyList<string> GetRecorderIds() => Active.GetRecorderIds();

    public Task<MediaInfo> GetMediaInfoAsync(string recorderId, CancellationToken ct = default)
        => Active.GetMediaInfoAsync(recorderId, ct);

    public Task BurnAsync(string recorderId, string sourceDirectory, BurnOptions options,
        IProgress<BurnProgress>? progress = null, CancellationToken ct = default)
        => Active.BurnAsync(recorderId, sourceDirectory, options, progress, ct);

    public Task EraseAsync(string recorderId, bool fullErase = false, CancellationToken ct = default)
        => Active.EraseAsync(recorderId, fullErase, ct);

    public Task<bool> CanMultisessionAsync(string recorderId, CancellationToken ct = default)
        => Active.CanMultisessionAsync(recorderId, ct);
}

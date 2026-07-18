namespace LithicBackup.Services;

/// <summary>
/// A single progress update for a long-running operation: a human-readable
/// <see cref="Text"/> line plus an optional 0–100 <see cref="Percent"/>.
///
/// A null <see cref="Percent"/> means "no measurable total for this step" (e.g.
/// an unbounded directory sweep, or a one-shot setup phase like "Loading
/// catalog…"), which the progress UI renders as an <em>indeterminate</em> bar.
/// A non-null percent drives a determinate bar showing real completion.
///
/// The implicit conversion from <see cref="string"/> lets any existing call site
/// keep reporting plain text (<c>progress.Report("…")</c>) unchanged — those
/// reports simply carry no percent and so render as indeterminate.
/// </summary>
public readonly record struct ProgressReport(string Text, double? Percent = null)
{
    public static implicit operator ProgressReport(string text) => new(text, null);
}

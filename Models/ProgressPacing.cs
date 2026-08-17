namespace Bigfile.Models;

/// <summary>
/// How often a line-by-line sweep reports progress.
///
/// A fixed interval cannot serve both ends of the range this viewer covers: on a
/// thousand-line document it would never report at all, and on a billion-line one
/// it would flood the UI thread. Deriving the interval from the size of the work
/// bounds the reports at <see cref="Steps"/> either way, so the bar always moves
/// and never floods.
/// </summary>
internal static class ProgressPacing
{
    /// <summary>Reports aimed for over a whole sweep.</summary>
    private const int Steps = 100;

    internal static int Interval(int lineCount) => Math.Max(lineCount / Steps, 1);
}

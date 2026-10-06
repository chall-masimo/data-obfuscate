using System.Globalization;
using CsvMasker.Web.Jobs;

namespace CsvMasker.Web.Infrastructure;

/// <summary>Small formatting helpers for the pages.</summary>
public static class Display
{
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} bytes",
    };

    public static string Delimiter(char delimiter) => delimiter switch
    {
        ',' => "comma",
        '\t' => "tab",
        ';' => "semicolon",
        '|' => "pipe",
        _ => $"'{delimiter}'",
    };

    public static string Percent(double fraction) => fraction.ToString("0.#%", CultureInfo.InvariantCulture);

    public static string Truncate(string value, int max = 60) => value.Length <= max ? value : value[..(max - 1)] + "…";

    public static string State(JobState state) => state switch
    {
        JobState.Uploaded => "Uploaded, waiting for review",
        JobState.Reviewed => "Reviewed, ready to preview and run",
        JobState.Queued => "Queued",
        JobState.Running => "Running",
        JobState.Completed => "Completed",
        JobState.Failed => "Failed",
        JobState.Cancelled => "Cancelled",
        _ => state.ToString(),
    };
}

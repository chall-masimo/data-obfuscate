using CsvMasker.Core.Csv;
using CsvMasker.Core.Masking;

namespace CsvMasker.Web.Infrastructure;

/// <summary>
/// Turns exceptions into text for users and logs. Core's exceptions are value-free by design
/// (record numbers, column names, limits). Any other exception's message might echo data, so
/// only its type is used.
/// </summary>
public static class SafeErrors
{
    public static string ForUser(Exception ex) => ex switch
    {
        CsvFormatException or MaskingPlanException or MaskingException => ex.Message,
        InvalidDataException => "The file doesn't start with the byte order mark its encoding implies.",
        _ => "Something went wrong while processing the file. Please try again; if it keeps happening, contact the tool's owner.",
    };

    public static string ForLog(Exception ex) => ex switch
    {
        CsvFormatException c => $"{nameof(CsvFormatException)} ({c.Kind}) at record {c.RecordNumber}",
        MaskingException m => $"{nameof(MaskingException)} ({m.Kind}) in column '{m.ColumnKey}'",
        MaskingPlanException p => $"{nameof(MaskingPlanException)} ({p.Problems.Count} problem(s))",
        _ => ex.GetType().FullName ?? ex.GetType().Name,
    };
}

public static class UserKey
{
    /// <summary>
    /// The signed-in user's name (DOMAIN\user), which owns their jobs. The authorization
    /// fallback policy means no page runs without one; throwing here is defence in depth.
    /// </summary>
    public static string Of(HttpContext context) =>
        context.User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name }
            ? name
            : throw new InvalidOperationException("No authenticated user.");
}

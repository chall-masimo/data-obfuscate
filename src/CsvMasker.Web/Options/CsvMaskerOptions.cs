using System.ComponentModel.DataAnnotations;

namespace CsvMasker.Web.Options;

public sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>Uploads and masked outputs (transient). Must be outside the web root. Empty = %TEMP%\CsvMasker.</summary>
    public string TempFolder { get; set; } = "";

    /// <summary>Saved recipes (build step 6).</summary>
    public string RecipeFolder { get; set; } = "";

    /// <summary>Temp files and idle jobs older than this are deleted by the sweeper.</summary>
    [Range(1, 24 * 60)]
    public int SweepAgeMinutes { get; set; } = 60;

    public string ResolveTempFolder() =>
        string.IsNullOrWhiteSpace(TempFolder)
            ? Path.Combine(Path.GetTempPath(), "CsvMasker")
            : Path.GetFullPath(TempFolder);
}

public sealed class LimitsOptions
{
    public const string Section = "Limits";

    /// <summary>
    /// Largest upload, in bytes. Also applied to IISServerOptions, Kestrel and FormOptions, and
    /// must equal maxAllowedContentLength in web.config.
    /// </summary>
    [Range(1, long.MaxValue)]
    public long MaxUploadBytes { get; set; } = 209_715_200;

    [Range(100, 10_000_000)]
    public int ProfileSampleRows { get; set; } = 50_000;

    [Range(1, long.MaxValue)]
    public long MaxMappingEntries { get; set; } = 2_000_000;

    /// <summary>Masking runs allowed at once across all users; more wait in a queue.</summary>
    [Range(1, 16)]
    public int MaxConcurrentJobs { get; set; } = 2;
}

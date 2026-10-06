using CsvMasker.Core.Csv;
using CsvMasker.Core.Masking;
using CsvMasker.Core.Masking.Verification;
using CsvMasker.Core.Profiling;

namespace CsvMasker.Web.Jobs;

public enum JobState
{
    Uploaded,
    Reviewed,
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
}

public static class JobSteps
{
    /// <summary>The page for the job's current step.</summary>
    public static string PageFor(Job job) => job.State switch
    {
        JobState.Uploaded => "/Jobs/Review",
        JobState.Reviewed => "/Jobs/Preview",
        _ => "/Jobs/Status",
    };
}

/// <summary>
/// One user's masking job, held in memory only. The profile's sample values and the session key
/// never leave this object; nothing here is persisted.
/// </summary>
public sealed class Job
{
    private long _rowsProcessed;

    public Job(string owner, string originalFileName, string uploadPath, FileProfile profile, MaskingPlan plan, DateTimeOffset now)
    {
        Owner = owner;
        OriginalFileName = originalFileName;
        UploadPath = uploadPath;
        Profile = profile;
        Plan = plan;
        Created = now;
        LastActivity = now;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string Owner { get; }

    /// <summary>The user's file name. Shown back to them; never logged.</summary>
    public string OriginalFileName { get; }

    public DateTimeOffset Created { get; }
    public DateTimeOffset LastActivity { get; set; }

    /// <summary>Guards state transitions between request threads and the runner.</summary>
    public object Sync { get; } = new();

    public JobState State { get; set; } = JobState.Uploaded;

    public string? UploadPath { get; set; }
    public string? OutputPath { get; set; }

    public FileProfile Profile { get; }
    public CsvDialect Dialect => Profile.Dialect;

    public MaskingPlan Plan { get; set; }
    public bool SkipMalformed { get; set; }

    /// <summary>Created when the review is confirmed; holds the per-job key used for preview and run.</summary>
    public MaskingSession? Session { get; set; }

    public CancellationTokenSource Cancellation { get; } = new();

    public long RowsProcessed
    {
        get => Interlocked.Read(ref _rowsProcessed);
        set => Interlocked.Exchange(ref _rowsProcessed, value);
    }

    public DateTimeOffset? Started { get; set; }
    public DateTimeOffset? Finished { get; set; }
    public VerificationReport? Report { get; set; }

    /// <summary>A value-free message for the user (see SafeErrors).</summary>
    public string? Error { get; set; }

    public bool Downloaded { get; set; }

    /// <summary>Set by the registry when the job is discarded or swept.</summary>
    public bool Removed { get; set; }

    /// <summary>A user may hold only one open job: from upload until it's downloaded, failed, cancelled or discarded.</summary>
    public bool IsOpen => !Removed && State switch
    {
        JobState.Completed => !Downloaded,
        JobState.Failed or JobState.Cancelled => false,
        _ => true,
    };

    public string MaskedFileName
    {
        get
        {
            string extension = Path.GetExtension(OriginalFileName);
            if (extension is not (".csv" or ".txt" or ".tsv"))
                extension = ".csv";
            return $"{Path.GetFileNameWithoutExtension(OriginalFileName)}_masked{extension}";
        }
    }
}

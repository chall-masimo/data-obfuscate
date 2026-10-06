using System.Diagnostics;
using CsvMasker.Core.Masking;
using CsvMasker.Core.Profiling;
using CsvMasker.Web.Infrastructure;
using CsvMasker.Web.Jobs;
using CsvMasker.Web.Options;
using CsvMasker.Web.Storage;
using CsvMasker.Web.Upload;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace CsvMasker.Web.Pages;

/// <summary>
/// Upload. The POST is sent by upload.js with the antiforgery token in a header, so the form
/// is never read (and never buffered to ASP.NET's temp folder); the file streams straight into
/// the temp store.
/// </summary>
[DisableFormValueModelBinding]
public sealed class IndexModel(
    JobRegistry registry,
    TempFileStore store,
    TimeProvider time,
    IOptions<LimitsOptions> limits,
    ILogger<IndexModel> logger) : PageModel
{
    public Job? CurrentJob { get; private set; }

    public long MaxUploadBytes => limits.Value.MaxUploadBytes;

    public void OnGet() => CurrentJob = registry.Current(UserKey.Of(HttpContext));

    public async Task<IActionResult> OnPostUploadAsync(CancellationToken cancellationToken)
    {
        string owner = UserKey.Of(HttpContext);
        if (registry.HasOpenJob(owner))
            return Error(StatusCodes.Status409Conflict, "You already have a job in progress. Continue it or discard it before uploading another file.");

        UploadedFile upload;
        try
        {
            upload = await MultipartUpload.SaveFileAsync(Request, store, MaxUploadBytes, cancellationToken);
        }
        catch (UploadException ex)
        {
            logger.LogInformation("Upload by {User} rejected: {Reason}", owner, ex.Message);
            return Error(ex.StatusCode, ex.Message);
        }

        var stopwatch = Stopwatch.StartNew();
        FileProfile profile;
        try
        {
            await using var stream = store.OpenRead(upload.Path);
            profile = CsvProfiler.Profile(stream, new ProfileOptions { SampleRows = limits.Value.ProfileSampleRows });
        }
        catch (Exception ex)
        {
            store.Delete(upload.Path);
            logger.LogInformation("Upload by {User} could not be read: {Error}", owner, SafeErrors.ForLog(ex));
            return Error(StatusCodes.Status422UnprocessableEntity, SafeErrors.ForUser(ex));
        }

        var job = new Job(owner, upload.FileName, upload.Path, profile, MaskingPlan.FromSuggestions(profile), time.GetUtcNow());
        if (!registry.TryAdd(job))
        {
            store.Delete(upload.Path);
            return Error(StatusCodes.Status409Conflict, "You already have a job in progress. Continue it or discard it before uploading another file.");
        }

        logger.LogInformation(
            "Job {JobId} uploaded by {User}: {Bytes} bytes, {Columns} columns, {Rows} rows profiled in {ElapsedMs} ms",
            job.Id, owner, upload.Bytes, profile.Columns.Count, profile.RowsProfiled, stopwatch.ElapsedMilliseconds);
        return new JsonResult(new { redirect = Url.Page("/Jobs/Review", new { id = job.Id }) });
    }

    private static JsonResult Error(int statusCode, string message) => new(new { error = message }) { StatusCode = statusCode };
}

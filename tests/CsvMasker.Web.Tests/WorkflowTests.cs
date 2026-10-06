using System.Net;
using System.Text;
using CsvMasker.Web.Jobs;
using CsvMasker.Web.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace CsvMasker.Web.Tests;

public class WorkflowTests
{
    internal static string SampleCsv(int rows = 60)
    {
        var csv = new StringBuilder("Customer_ID,Customer_Name,Contact_Email,Amount,Order_Date,Notes\r\n");
        for (int i = 0; i < rows; i++)
            csv.Append($"C{i % 40:D5},\"SECRET Corp {i % 40}\",secret{i % 40}@secret-domain.com,{i * 10.25:F2},2024-{i % 12 + 1:D2}-{i % 28 + 1:D2},\"SECRET note {i}, quite a long remark about this order that goes on and on\"\r\n");
        return csv.ToString();
    }

    internal static async Task<string> UploadAndReviewAsync(Browser browser, string csv)
    {
        var upload = await browser.UploadAsync(csv);
        Assert.True(upload.Status == HttpStatusCode.OK, upload.Error);
        string reviewUrl = upload.Redirect!;

        var review = await browser.SubmitAsync(reviewUrl, "/Review", new Dictionary<string, string> { ["Confirmed"] = "true" });
        Assert.Equal(HttpStatusCode.Redirect, review.StatusCode);
        return reviewUrl.Replace("/Review", "", StringComparison.Ordinal);
    }

    internal static async Task<string> WaitForStateAsync(Browser browser, string statusUrl, params string[] states)
    {
        for (int i = 0; i < 300; i++)
        {
            string html = await browser.GetAsync(statusUrl);
            if (states.Any(s => html.Contains($"data-job-state=\"{s}\"", StringComparison.Ordinal)))
                return html;
            await Task.Delay(100);
        }
        throw new TimeoutException("Job did not reach the expected state.");
    }

    [Fact]
    public async Task Upload_review_preview_run_download()
    {
        using var app = new TestApp();
        var browser = app.Browser();

        string job = await UploadAndReviewAsync(browser, SampleCsv());

        string preview = await browser.GetAsync(job + "/Preview");
        Assert.Contains("Masked", preview);
        Assert.Contains("SECRET Corp 0", preview); // originals are shown to their owner only, on this page

        var run = await browser.SubmitFromHtmlAsync(preview, job + "/Preview", "handler=Run");
        Assert.Equal(HttpStatusCode.Redirect, run.StatusCode);

        string status = await WaitForStateAsync(browser, job + "/Status", "Completed", "Failed");
        Assert.Contains("data-job-state=\"Completed\"", status);
        Assert.Contains("All verification checks passed", status);
        Assert.Single(app.TempFiles()); // the upload is gone; only the output remains

        var download = await browser.SubmitFromHtmlAsync(status, job + "/Status", "handler=Download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("data_masked.csv", download.Content.Headers.ContentDisposition?.FileNameStar ?? download.Content.Headers.ContentDisposition?.FileName);
        string masked = await download.Content.ReadAsStringAsync();
        download.Dispose();

        Assert.Equal(SampleCsv().Split("\r\n").Length, masked.Split("\r\n").Length);
        Assert.StartsWith("Customer_ID,Customer_Name,Contact_Email,Amount,Order_Date,Notes\r\n", masked);
        Assert.DoesNotContain("SECRET", masked);
        Assert.DoesNotContain("secret-domain.com", masked);

        await WaitUntilAsync(() => app.TempFiles().Length == 0);
        Assert.Empty(app.TempFiles());

        // A second download is not possible: the page offers none, and posting anyway just redirects.
        string after = await browser.GetAsync(job + "/Status");
        Assert.Contains("Downloaded.", after);
        var again = await browser.PostTokenAsync(job + "/Status?handler=Download", after);
        Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);

        Assert.DoesNotContain(app.Logs.Lines, line => line.Contains("SECRET", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(app.Logs.Lines, line => line.Contains("completed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Review_requires_confirmation()
    {
        using var app = new TestApp();
        var browser = app.Browser();
        var upload = await browser.UploadAsync(SampleCsv());

        var response = await browser.SubmitAsync(upload.Redirect!, "/Review", remove: new HashSet<string> { "Confirmed" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("confirm you have reviewed every column", await response.Content.ReadAsStringAsync());
        // Preview is not reachable yet: it sends the user back to review.
        var preview = await browser.GetRawAsync(upload.Redirect!.Replace("/Review", "/Preview", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Redirect, preview.StatusCode);
        Assert.EndsWith("/Review", preview.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Review_rejects_invalid_options()
    {
        using var app = new TestApp();
        var browser = app.Browser();
        var upload = await browser.UploadAsync(SampleCsv());

        var response = await browser.SubmitAsync(upload.Redirect!, "/Review", new Dictionary<string, string>
        {
            ["Confirmed"] = "true",
            ["Columns[3].PerturbPercent"] = "150",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Perturb percent must be between 1 and 99", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task One_open_job_per_user_and_jobs_are_private()
    {
        using var app = new TestApp();
        var alice = app.Browser("alice");
        var bob = app.Browser("bob");

        var first = await alice.UploadAsync(SampleCsv());
        var second = await alice.UploadAsync(SampleCsv());
        Assert.Equal(HttpStatusCode.Conflict, second.Status);
        Assert.Contains("already have a job", second.Error);
        Assert.Contains("data-test=\"open-job\"", await alice.GetAsync(alice.Url("/")));

        // Bob has his own slot and can't see Alice's job.
        Assert.Equal(HttpStatusCode.OK, (await bob.UploadAsync(SampleCsv())).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetRawAsync(first.Redirect!)).StatusCode);

        var discard = await alice.SubmitAsync(alice.Url("/"), "/Discard");
        Assert.Equal(HttpStatusCode.Redirect, discard.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.UploadAsync(SampleCsv())).Status);
    }

    [Fact]
    public async Task Bad_file_gives_a_value_free_error_and_leaves_nothing_behind()
    {
        using var app = new TestApp();
        var browser = app.Browser();

        var upload = await browser.UploadAsync("Name,Notes\r\nSECRET-1,ok\r\nSECRET-2,\"never closed\r\n");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, upload.Status);
        Assert.Contains("Record 3", upload.Error);
        Assert.DoesNotContain("SECRET", upload.Error);
        Assert.Empty(app.TempFiles());
        Assert.DoesNotContain(app.Logs.Lines, line => line.Contains("SECRET", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Upload_over_the_limit_is_rejected()
    {
        using var app = new TestApp(settings: new Dictionary<string, string?> { ["Limits:MaxUploadBytes"] = "1000" });
        var browser = app.Browser();

        var upload = await browser.UploadAsync(SampleCsv(100));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, upload.Status);
        Assert.Contains("larger than", upload.Error);
        Assert.Empty(app.TempFiles());
    }

    [Fact]
    public async Task Discard_deletes_files_and_forgets_the_job()
    {
        using var app = new TestApp();
        var browser = app.Browser();
        string job = await UploadAndReviewAsync(browser, SampleCsv());
        Assert.Single(app.TempFiles());

        await browser.SubmitAsync(job + "/Preview", "/Discard");

        Assert.Empty(app.TempFiles());
        Assert.Empty(app.Services.GetRequiredService<JobRegistry>().All());
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetRawAsync(job + "/Preview")).StatusCode);
    }

    [Fact]
    public async Task Running_job_can_be_cancelled()
    {
        using var app = new TestApp();
        var browser = app.Browser();
        string job = await UploadAndReviewAsync(browser, SampleCsv(300_000));

        string preview = await browser.GetAsync(job + "/Preview");
        await browser.SubmitFromHtmlAsync(preview, job + "/Preview", "handler=Run");
        string status = await browser.GetAsync(job + "/Status");
        await browser.SubmitFromHtmlAsync(status, job + "/Status", "handler=Cancel");

        string final = await WaitForStateAsync(browser, job + "/Status", "Cancelled", "Completed");
        Assert.Contains("data-job-state=\"Cancelled\"", final);
        await WaitUntilAsync(() => app.TempFiles().Length == 0);
        Assert.Empty(app.TempFiles());
    }

    [Fact]
    public async Task Sweeper_removes_old_files_and_idle_jobs()
    {
        using var app = new TestApp();
        var browser = app.Browser();
        await browser.UploadAsync(SampleCsv());

        var store = app.Services.GetRequiredService<TempFileStore>();
        var registry = app.Services.GetRequiredService<JobRegistry>();
        string stale = Path.Combine(store.Folder, "leftover.upload");
        string fresh = Path.Combine(store.Folder, "fresh.output");
        File.WriteAllText(stale, "x");
        File.WriteAllText(fresh, "x");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));
        var job = registry.All().Single();
        job.LastActivity = DateTimeOffset.UtcNow.AddHours(-2);

        var removed = app.Services.GetRequiredService<TempFolderSweeper>().SweepOnce();

        Assert.Equal(1, removed.Jobs);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.Empty(registry.All());
        Assert.Equal([fresh], app.TempFiles());
    }

    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 50 && !condition(); i++)
            await Task.Delay(100);
    }
}

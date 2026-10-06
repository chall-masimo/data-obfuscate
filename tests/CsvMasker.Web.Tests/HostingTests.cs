using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CsvMasker.Web.Tests;

/// <summary>IIS hosting concerns: running under /csvmasker, and limits that must agree.</summary>
public class HostingTests
{
    [Fact]
    public async Task Every_link_and_form_respects_the_path_base()
    {
        using var app = new TestApp(pathBase: "/csvmasker");
        var browser = app.Browser();

        var pages = new List<string> { await browser.GetAsync("/csvmasker/") };
        string job = await WorkflowTests.UploadAndReviewAsync(browser, WorkflowTests.SampleCsv());
        Assert.StartsWith("/csvmasker/Jobs/", job);
        pages.Add(await browser.GetAsync(job + "/Review"));
        string preview = await browser.GetAsync(job + "/Preview");
        pages.Add(preview);
        await browser.SubmitFromHtmlAsync(preview, job + "/Preview", "handler=Run");
        pages.Add(await WorkflowTests.WaitForStateAsync(browser, job + "/Status", "Completed"));

        foreach (string html in pages)
        {
            var urls = Regex.Matches(html, @"\b(?:href|src|action)=""([^""]*)""").Select(m => m.Groups[1].Value).ToList();
            Assert.NotEmpty(urls);
            Assert.All(urls, url => Assert.Matches(@"^/csvmasker(/|\?|$)", url));
        }
    }

    [Fact]
    public void Web_config_upload_limit_matches_appsettings()
    {
        string root = RepoRoot();
        var webConfig = XDocument.Load(Path.Combine(root, "src", "CsvMasker.Web", "web.config"));
        long iisLimit = long.Parse(webConfig.Descendants("requestLimits").Single().Attribute("maxAllowedContentLength")!.Value);

        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "CsvMasker.Web", "appsettings.json")));
        long appLimit = settings.RootElement.GetProperty("Limits").GetProperty("MaxUploadBytes").GetInt64();

        Assert.Equal(appLimit, iisLimit);
    }

    [Fact]
    public void Temp_folder_inside_the_web_root_fails_startup()
    {
        string webRoot = Path.Combine(RepoRoot(), "src", "CsvMasker.Web", "wwwroot");
        using var app = new TestApp(settings: new Dictionary<string, string?> { ["Storage:TempFolder"] = Path.Combine(webRoot, "uploads") });

        var ex = Assert.ThrowsAny<Exception>(() => app.Browser());

        Assert.Contains("outside the web root", ex.ToString());
        Assert.False(Directory.Exists(Path.Combine(webRoot, "uploads")));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CsvMasker.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

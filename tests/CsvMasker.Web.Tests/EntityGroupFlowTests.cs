using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace CsvMasker.Web.Tests;

/// <summary>Entity groups, per-entity modes and shared mappings through the review screen.</summary>
public class EntityGroupFlowTests
{
    /// <summary>Uploads, submits the review with overrides, runs, and downloads. Returns the masked CSV and the final status page.</summary>
    private static async Task<(string Masked, string Status)> RunAsync(Browser browser, string csv, Dictionary<string, string> overrides)
    {
        var upload = await browser.UploadAsync(csv);
        Assert.Equal(HttpStatusCode.OK, upload.Status);
        string job = upload.Redirect!.Replace("/Review", "", StringComparison.Ordinal);
        overrides["Confirmed"] = "true";
        var review = await browser.SubmitAsync(upload.Redirect!, "review-form", overrides);
        Assert.True(review.StatusCode == HttpStatusCode.Redirect, await review.Content.ReadAsStringAsync());

        string preview = await browser.GetAsync(job + "/Preview");
        await browser.SubmitFromHtmlAsync(preview, job + "/Preview", "handler=Run");
        string status = await WorkflowTests.WaitForStateAsync(browser, job + "/Status", "Completed", "Failed");
        var download = await browser.SubmitFromHtmlAsync(status, job + "/Status", "handler=Download");
        return (await download.Content.ReadAsStringAsync(), status);
    }

    private static string[][] Rows(string csv) =>
        csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(SplitCsv).ToArray();

    private static string[] SplitCsv(string line) =>
        Regex.Matches(line, @"(?:^|,)(""(?:[^""]|"""")*""|[^,]*)").Select(m => m.Groups[1].Value.Trim('"').Replace("\"\"", "\"")).ToArray();

    private static string Selected(string html, string field) =>
        Regex.Match(html, $@"name=""{Regex.Escape(field)}""[^>]*>(.*?)</select>", RegexOptions.Singleline) is { Success: true } select
            ? Regex.Match(select.Groups[1].Value, @"<option selected=""selected"" value=""([^""]*)""").Groups[1].Value
            : throw new InvalidOperationException($"{field} not found.");

    [Fact]
    public async Task Suggested_group_is_pre_linked_on_review()
    {
        using var app = new TestApp();
        var browser = app.Browser();

        var upload = await browser.UploadAsync(WorkflowTests.SampleCsv());
        string review = await browser.GetAsync(upload.Redirect!);

        Assert.Contains("data-test=\"entity-groups\"", review);
        Assert.Contains("Customer_ID → Customer_Name", review);
        Assert.Equal("Customer_ID", Selected(review, "Columns[1].LinkedTo"));
        Assert.Equal("", Selected(review, "Columns[2].LinkedTo")); // Contact_Email has no Customer_ prefix
    }

    [Fact]
    public async Task One_customer_keeps_one_identity_and_one_perturb_factor()
    {
        using var app = new TestApp();
        var browser = app.Browser();

        var (masked, status) = await RunAsync(browser, WorkflowTests.SampleCsv(), new()
        {
            ["Columns[3].LinkedTo"] = "Customer_ID",
            ["Columns[3].PerturbMode"] = "PerEntity",
        });

        Assert.Contains("All verification checks passed", status);
        Assert.Contains("per entity", status);
        var source = Rows(WorkflowTests.SampleCsv());
        var output = Rows(masked);
        foreach (var customer in Enumerable.Range(0, source.Length).GroupBy(i => source[i][0]))
        {
            Assert.Single(customer.Select(i => output[i][1]).Distinct()); // one fake company per customer
            var factors = customer.Where(i => decimal.Parse(source[i][3], CultureInfo.InvariantCulture) != 0)
                .Select(i => Math.Round(decimal.Parse(output[i][3], CultureInfo.InvariantCulture) / decimal.Parse(source[i][3], CultureInfo.InvariantCulture), 2))
                .Distinct().ToList();
            Assert.True(factors.Count <= 1 || factors.Max() - factors.Min() <= 0.01m, $"Customer {customer.Key} factors: {string.Join(", ", factors)}");
        }
    }

    [Fact]
    public async Task Linking_a_shared_attribute_warns_about_its_distribution()
    {
        using var app = new TestApp();
        var browser = app.Browser();
        string csv = string.Join("\r\n", WorkflowTests.SampleCsv().Split("\r\n")
            .Select((line, i) => line.Length == 0 ? line : line + (i == 0 ? ",Region" : $",R{i % 4}")));
        var upload = await browser.UploadAsync(csv);

        var response = await browser.SubmitAsync(upload.Redirect!, "review-form", new Dictionary<string, string>
        {
            ["Columns[6].LinkedTo"] = "Customer_ID", // not confirmed: the page re-renders with the note
        });

        string html = await response.Content.ReadAsStringAsync();
        Assert.Contains("data-test=\"link-note\"", html);
        Assert.Contains("changes Region&#x27;s distribution", html);
    }

    [Fact]
    public async Task Per_entity_without_a_link_is_rejected()
    {
        using var app = new TestApp();
        var browser = app.Browser();
        var upload = await browser.UploadAsync(WorkflowTests.SampleCsv());

        var response = await browser.SubmitAsync(upload.Redirect!, "review-form", new Dictionary<string, string>
        {
            ["Confirmed"] = "true",
            ["Columns[3].PerturbMode"] = "PerEntity",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Per entity needs the column to be linked", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Same_mapping_as_makes_two_columns_map_identically()
    {
        using var app = new TestApp();
        var browser = app.Browser();
        var csv = new StringBuilder("BillTo_Code,ShipTo_Code,Amount\r\n");
        for (int i = 0; i < 80; i++)
            csv.Append($"B{i % 30:D5},B{(i + 7) % 30:D5},{i}.00\r\n");

        var (masked, status) = await RunAsync(browser, csv.ToString(), new()
        {
            ["Columns[0].Strategy"] = "1", // HashId
            ["Columns[1].Strategy"] = "1",
            ["Columns[1].SameMappingAs"] = "BillTo_Code",
        });

        Assert.Contains("All verification checks passed", status);
        var source = Rows(csv.ToString());
        var output = Rows(masked);
        var map = new Dictionary<string, string>();
        for (int r = 0; r < source.Length; r++)
            for (int c = 0; c < 2; c++)
            {
                Assert.NotEqual(source[r][c], output[r][c]);
                if (map.TryGetValue(source[r][c], out var seen))
                    Assert.Equal(seen, output[r][c]); // the same code maps the same way in both columns
                map[source[r][c]] = output[r][c];
            }
    }

    [Fact]
    public async Task Recipes_keep_groups_and_shared_mappings()
    {
        using var app = new TestApp();
        var browser = app.Browser();
        var csv = new StringBuilder("Customer_ID,Customer_Name,BillTo_Code,ShipTo_Code\r\n");
        for (int i = 0; i < 60; i++)
            csv.Append($"C{i % 20:D4},Acme {i % 20},B{i % 15:D5},B{(i + 3) % 15:D5}\r\n");

        var (_, status) = await RunAsync(browser, csv.ToString(), new()
        {
            ["Columns[2].Strategy"] = "1", // HashId
            ["Columns[3].Strategy"] = "1",
            ["Columns[3].SameMappingAs"] = "BillTo_Code",
        });
        string job = Regex.Match(status, @"/Jobs/[0-9a-f-]+").Value;
        await browser.SubmitFromHtmlAsync(status, job + "/Status", "handler=SaveRecipe", new Dictionary<string, string> { ["recipeName"] = "Orders" });
        await browser.SubmitAsync(job + "/Status", "/Discard");

        string json = File.ReadAllText(Assert.Single(app.RecipeFiles()));
        Assert.Contains("\"anchor\": \"Customer_ID\"", json);
        Assert.Contains("\"mappingDomain\": \"BillTo_Code\"", json);

        var upload = await browser.UploadAsync(csv.ToString());
        string review = await browser.GetAsync(upload.Redirect!);
        Assert.Contains("data-test=\"recipe-exact\"", review);
        Assert.Equal("Customer_ID", Selected(review, "Columns[1].LinkedTo"));
        Assert.Equal("BillTo_Code", Selected(review, "Columns[3].SameMappingAs"));
    }
}

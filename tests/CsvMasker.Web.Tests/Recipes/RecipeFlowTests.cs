using System.Net;
using System.Text.RegularExpressions;

namespace CsvMasker.Web.Tests.Recipes;

/// <summary>Saving a recipe after a run, and having later uploads pre-filled from it.</summary>
public class RecipeFlowTests
{
    private const string Lorem = "7"; // MaskingStrategy.Lorem
    private const string Keep = "0";  // MaskingStrategy.Keep

    /// <summary>Runs a job with Notes switched to Lorem, saves it as a recipe, and discards the job.</summary>
    private static async Task SaveRecipeAsync(Browser browser, string name = "Monthly extract")
    {
        var upload = await browser.UploadAsync(WorkflowTests.SampleCsv());
        string job = upload.Redirect!.Replace("/Review", "", StringComparison.Ordinal);
        var review = await browser.SubmitAsync(upload.Redirect!, "review-form", new Dictionary<string, string>
        {
            ["Confirmed"] = "true",
            ["Columns[5].Strategy"] = Lorem,
        });
        Assert.Equal(HttpStatusCode.Redirect, review.StatusCode);

        string preview = await browser.GetAsync(job + "/Preview");
        await browser.SubmitFromHtmlAsync(preview, job + "/Preview", "handler=Run");
        string status = await WorkflowTests.WaitForStateAsync(browser, job + "/Status", "Completed");

        var save = await browser.SubmitFromHtmlAsync(status, job + "/Status", "handler=SaveRecipe",
            new Dictionary<string, string> { ["recipeName"] = name });
        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);
        Assert.Contains("Saved as recipe", await browser.GetAsync(job + "/Status"));

        await browser.SubmitAsync(job + "/Status", "/Discard");
    }

    private static string SelectedStrategy(string html, int column) =>
        Regex.Match(html, $@"name=""Columns\[{column}\]\.Strategy""[^>]*>(.*?)</select>", RegexOptions.Singleline) is { Success: true } select
            ? Regex.Match(select.Groups[1].Value, @"<option selected=""selected"" value=""(\d*)""").Groups[1].Value
            : throw new InvalidOperationException("Strategy select not found.");

    [Fact]
    public async Task Saved_recipe_contains_no_data_and_prefills_the_same_layout()
    {
        using var app = new TestApp();
        var browser = app.Browser();

        await SaveRecipeAsync(browser);

        string json = File.ReadAllText(Assert.Single(app.RecipeFiles()));
        Assert.DoesNotContain("SECRET", json);
        Assert.DoesNotContain("secret-domain", json);
        Assert.DoesNotContain("data.csv", json);
        Assert.Contains("\"strategy\": \"Lorem\"", json);
        Assert.Contains("\"owner\": \"TESTDOMAIN\\\\tester\"", json);

        var upload = await browser.UploadAsync(WorkflowTests.SampleCsv());
        string review = await browser.GetAsync(upload.Redirect!);

        Assert.Contains("data-test=\"recipe-exact\"", review);
        Assert.Contains("Monthly extract", review);
        Assert.Equal(Lorem, SelectedStrategy(review, 5)); // the recipe's choice, not the profiler's (Redact)
    }

    [Fact]
    public async Task Partial_match_leaves_new_columns_blank_and_blocks_until_chosen()
    {
        using var app = new TestApp();
        var browser = app.Browser();
        await SaveRecipeAsync(browser);

        string wider = string.Join("\r\n", WorkflowTests.SampleCsv().Split("\r\n")
            .Select((line, i) => line.Length == 0 ? line : line + (i == 0 ? ",Region" : ",North")));
        var upload = await browser.UploadAsync(wider);
        string review = await browser.GetAsync(upload.Redirect!);

        Assert.Contains("data-test=\"recipe-partial\"", review);
        Assert.Contains("6 of 7 columns", review);
        Assert.Single(Regex.Matches(review, "data-unassigned=\"true\""));
        Assert.Equal("", SelectedStrategy(review, 6));
        Assert.Equal(Lorem, SelectedStrategy(review, 5));

        var blocked = await browser.SubmitFromHtmlAsync(review, upload.Redirect!, "review-form", new Dictionary<string, string> { ["Confirmed"] = "true" });
        Assert.Equal(HttpStatusCode.OK, blocked.StatusCode);
        Assert.Contains("&#x27;Region&#x27; has no strategy assigned", await blocked.Content.ReadAsStringAsync());

        var accepted = await browser.SubmitFromHtmlAsync(review, upload.Redirect!, "review-form", new Dictionary<string, string>
        {
            ["Confirmed"] = "true",
            ["Columns[6].Strategy"] = Keep,
        });
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
    }

    [Fact]
    public async Task Recipes_are_shared_but_only_the_owner_can_delete()
    {
        using var app = new TestApp();
        var alice = app.Browser(@"TESTDOMAIN\alice");
        var bob = app.Browser(@"TESTDOMAIN\bob");
        await SaveRecipeAsync(alice);

        // Bob sees and uses Alice's recipe...
        string list = await bob.GetAsync("/Recipes");
        Assert.Contains("Monthly extract", list);
        Assert.DoesNotContain("data-test=\"delete-recipe\"", list);
        var upload = await bob.UploadAsync(WorkflowTests.SampleCsv());
        Assert.Contains("data-test=\"recipe-exact\"", await bob.GetAsync(upload.Redirect!));

        // ...but can't delete it, even by posting directly.
        string id = Path.GetFileNameWithoutExtension(Assert.Single(app.RecipeFiles()));
        var forced = await bob.PostTokenAsync($"/Recipes?handler=Delete&recipeId={id}", await bob.GetAsync("/")); // the list has no forms for Bob; borrow a token
        Assert.Equal(HttpStatusCode.Forbidden, forced.StatusCode);
        Assert.Contains("data-test=\"not-allowed\"", await forced.Content.ReadAsStringAsync());
        Assert.Single(app.RecipeFiles());

        // Alice can.
        var deleted = await alice.SubmitAsync("/Recipes", "handler=Delete");
        Assert.Equal(HttpStatusCode.Redirect, deleted.StatusCode);
        Assert.Empty(app.RecipeFiles());
    }

    [Fact]
    public async Task Review_can_switch_back_to_profiler_suggestions()
    {
        using var app = new TestApp();
        var browser = app.Browser();
        await SaveRecipeAsync(browser);
        var upload = await browser.UploadAsync(WorkflowTests.SampleCsv());

        var applied = await browser.SubmitAsync(upload.Redirect!, "handler=ApplyRecipe", new Dictionary<string, string> { ["recipeId"] = "" });
        Assert.Equal(HttpStatusCode.Redirect, applied.StatusCode);

        string review = await browser.GetAsync(upload.Redirect!);
        Assert.Contains("data-test=\"recipe-none\"", review);
        Assert.Equal("6", SelectedStrategy(review, 5)); // profiler suggestion: Redact
    }

    [Fact]
    public async Task Owner_can_update_the_recipe_a_job_used()
    {
        using var app = new TestApp();
        var browser = app.Browser();
        await SaveRecipeAsync(browser);

        var upload = await browser.UploadAsync(WorkflowTests.SampleCsv());
        string job = upload.Redirect!.Replace("/Review", "", StringComparison.Ordinal);
        await browser.SubmitAsync(upload.Redirect!, "review-form", new Dictionary<string, string>
        {
            ["Confirmed"] = "true",
            ["Columns[5].Strategy"] = Keep,
        });
        string preview = await browser.GetAsync(job + "/Preview");
        await browser.SubmitFromHtmlAsync(preview, job + "/Preview", "handler=Run");
        string status = await WorkflowTests.WaitForStateAsync(browser, job + "/Status", "Completed");

        Assert.Contains("Update recipe", status);
        await browser.SubmitFromHtmlAsync(status, job + "/Status", "handler=UpdateRecipe");

        Assert.Contains("\"strategy\": \"Keep\"", File.ReadAllText(Assert.Single(app.RecipeFiles())));
    }

    [Fact]
    public void Production_without_a_recipe_folder_refuses_to_start()
    {
        using var app = new TestApp(settings: new Dictionary<string, string?> { ["Storage:RecipeFolder"] = "" });

        var ex = Assert.ThrowsAny<Exception>(() => app.Browser());

        Assert.Contains("Storage:RecipeFolder is not set", ex.ToString());
    }

    [Fact]
    public void Recipe_folder_inside_the_temp_folder_fails_startup()
    {
        string root = Path.Combine(Path.GetTempPath(), "CsvMaskerWebTests", Guid.NewGuid().ToString("N"));
        using var app = new TestApp(settings: new Dictionary<string, string?>
        {
            ["Storage:TempFolder"] = root,
            ["Storage:RecipeFolder"] = Path.Combine(root, "recipes"),
        });

        var ex = Assert.ThrowsAny<Exception>(() => app.Browser());

        Assert.Contains("separate folders", ex.ToString());
    }
}

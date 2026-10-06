using System.Net;

namespace CsvMasker.Web.Tests;

/// <summary>Security rule 6: Windows authentication, and only members of the configured AD group.</summary>
public class AccessTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/?handler=Upload")]
    [InlineData("/Jobs/00000000-0000-0000-0000-000000000001/Review")]
    public async Task Anonymous_requests_are_challenged(string path)
    {
        using var app = new TestApp();

        var response = await app.Anonymous().GetRawAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(TestAuthHandler.SchemeName, response.Headers.WwwAuthenticate.ToString());
        // A 401 is a handshake step: it must not be re-executed as a status page (that breaks NTLM).
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Users_outside_the_group_get_a_friendly_403()
    {
        using var app = new TestApp();
        var outsider = app.Browser(@"TESTDOMAIN\outsider", @"TESTDOMAIN\Some Other Group");

        var response = await outsider.GetRawAsync("/");
        string html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("data-test=\"access-denied\"", html);
        Assert.Contains(@"TESTDOMAIN\outsider", html);
        Assert.DoesNotContain(TestApp.Group, html); // the group name isn't advertised
        Assert.Contains(app.Logs.Lines, l => l.Contains(@"Access denied for TESTDOMAIN\outsider", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Outsiders_cannot_upload()
    {
        using var app = new TestApp();
        var outsider = app.Browser(@"TESTDOMAIN\outsider", @"TESTDOMAIN\Some Other Group");
        using var form = new MultipartFormDataContent { { new ByteArrayContent("a,b\r\n1,2\r\n"u8.ToArray()), "file", "x.csv" } };

        var response = await outsider.Client.PostAsync("/?handler=Upload", form);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(app.TempFiles());
    }

    [Fact]
    public async Task Members_get_in_and_see_their_name()
    {
        using var app = new TestApp();

        string html = await app.Browser(@"TESTDOMAIN\member").GetAsync("/");

        Assert.Contains(@"data-test=""user"">TESTDOMAIN\member<", html);
    }

    [Fact]
    public async Task Group_names_match_case_insensitively()
    {
        using var app = new TestApp();

        var response = await app.Browser(@"TESTDOMAIN\member", TestApp.Group.ToUpperInvariant()).GetRawAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Static_assets_are_public_so_the_denied_page_is_styled()
    {
        using var app = new TestApp();
        string html = await app.Browser().GetAsync("/");
        string css = System.Text.RegularExpressions.Regex.Match(html, @"href=""(/css/site[^""]+)""").Groups[1].Value;

        var response = await app.Anonymous().GetRawAsync(css);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Production_without_a_group_refuses_to_start()
    {
        using var app = new TestApp(settings: new Dictionary<string, string?> { ["Authorization:AllowedGroup"] = "" });

        var ex = Assert.ThrowsAny<Exception>(() => app.Browser());

        Assert.Contains("Authorization:AllowedGroup is not set", ex.ToString());
    }

    [Fact]
    public async Task Development_without_a_group_lets_any_signed_in_user_in()
    {
        using var app = new TestApp(settings: new Dictionary<string, string?> { ["Authorization:AllowedGroup"] = "" }, environment: "Development");

        var member = await app.Browser(@"TESTDOMAIN\anyone", "No Groups").GetRawAsync("/");
        var anonymous = await app.Anonymous().GetRawAsync("/");

        Assert.Equal(HttpStatusCode.OK, member.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Unknown_job_gets_a_friendly_404()
    {
        using var app = new TestApp();

        var response = await app.Browser().GetRawAsync("/Jobs/00000000-0000-0000-0000-000000000001/Review");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("That page or job doesn't exist", await response.Content.ReadAsStringAsync());
    }
}

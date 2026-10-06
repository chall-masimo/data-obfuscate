using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CsvMasker.Web.Tests;

/// <summary>Drives the app like a browser: reads forms out of the rendered HTML and posts them back.</summary>
public sealed partial class Browser(HttpClient client, string pathBase)
{
    public HttpClient Client => client;

    public string Url(string path) => pathBase + path;

    public async Task<string> GetAsync(string url)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {url} returned {(int)response.StatusCode}");
        return await response.Content.ReadAsStringAsync();
    }

    public Task<HttpResponseMessage> GetRawAsync(string url) => client.GetAsync(url);

    /// <summary>Uploads a file the way upload.js does: multipart body, antiforgery token in a header.</summary>
    public async Task<(HttpStatusCode Status, string? Redirect, string? Error)> UploadAsync(byte[] content, string fileName = "data.csv")
    {
        string page = await GetAsync(Url("/"));
        // With a job open the form is hidden; post to the handler anyway, as a stale tab would.
        var match = UploadAction().Match(page);
        string action = match.Success ? Decode(match.Groups[1].Value) : Url("/?handler=Upload");

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", fileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, action) { Content = form };
        request.Headers.Add("RequestVerificationToken", Token(page));
        var response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        using var json = JsonDocument.Parse(body);
        string? redirect = json.RootElement.TryGetProperty("redirect", out var r) ? r.GetString() : null;
        string? error = json.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
        return (response.StatusCode, redirect, error);
    }

    public Task<(HttpStatusCode Status, string? Redirect, string? Error)> UploadAsync(string csv, string fileName = "data.csv") =>
        UploadAsync(Encoding.UTF8.GetBytes(csv), fileName);

    /// <summary>Posts back the first form on the page whose action contains <paramref name="actionContains"/>, with its current field values plus overrides.</summary>
    public async Task<HttpResponseMessage> SubmitAsync(string pageUrl, string actionContains, IDictionary<string, string>? overrides = null, ISet<string>? remove = null)
    {
        string page = await GetAsync(pageUrl);
        return await SubmitFromHtmlAsync(page, pageUrl, actionContains, overrides, remove);
    }

    public async Task<HttpResponseMessage> SubmitFromHtmlAsync(string page, string pageUrl, string actionContains, IDictionary<string, string>? overrides = null, ISet<string>? remove = null)
    {
        foreach (Match form in Form().Matches(page))
        {
            string action = Decode(Attribute(form.Groups["attrs"].Value, "action") ?? pageUrl);
            if (!action.Contains(actionContains, StringComparison.Ordinal))
                continue;

            var fields = Fields(form.Groups["body"].Value);
            foreach (var (key, value) in overrides ?? new Dictionary<string, string>())
            {
                fields.RemoveAll(f => f.Key == key);
                fields.Add(new(key, value));
            }
            if (remove is not null)
                fields.RemoveAll(f => remove.Contains(f.Key));

            return await client.PostAsync(action, new FormUrlEncodedContent(fields));
        }
        throw new InvalidOperationException($"No form with action containing '{actionContains}' on {pageUrl}.");
    }

    /// <summary>Posts only the antiforgery token (taken from <paramref name="tokenSourceHtml"/>) to a handler URL.</summary>
    public Task<HttpResponseMessage> PostTokenAsync(string url, string tokenSourceHtml) =>
        client.PostAsync(url, new FormUrlEncodedContent([new("__RequestVerificationToken", Token(tokenSourceHtml))]));

    public static string Token(string html) => Decode(TokenInput().Match(html).Groups[1].Value);

    public static string Decode(string value) => WebUtility.HtmlDecode(value);

    private static List<KeyValuePair<string, string>> Fields(string formHtml)
    {
        var fields = new List<KeyValuePair<string, string>>();
        foreach (Match input in Input().Matches(formHtml))
        {
            string attrs = input.Value;
            string? name = Attribute(attrs, "name");
            if (name is null) continue;
            string type = Attribute(attrs, "type") ?? "text";
            if (type is "checkbox" or "radio" && !attrs.Contains("checked", StringComparison.Ordinal)) continue;
            fields.Add(new(name, Decode(Attribute(attrs, "value") ?? "")));
        }
        foreach (Match select in Select().Matches(formHtml))
        {
            string? name = Attribute(select.Groups["attrs"].Value, "name");
            if (name is null) continue;
            var options = Option().Matches(select.Groups["body"].Value);
            var chosen = options.FirstOrDefault(o => o.Value.Contains("selected", StringComparison.Ordinal)) ?? options.FirstOrDefault();
            if (chosen is not null)
                fields.Add(new(name, Decode(Attribute(chosen.Value, "value") ?? "")));
        }
        return fields;
    }

    private static string? Attribute(string tag, string name)
    {
        var match = Regex.Match(tag, $@"\b{name}=""([^""]*)""");
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"id=""upload-form""[^>]*?action=""([^""]*)""")]
    private static partial Regex UploadAction();

    [GeneratedRegex(@"name=""__RequestVerificationToken"" type=""hidden"" value=""([^""]+)""")]
    private static partial Regex TokenInput();

    [GeneratedRegex(@"<form\b(?<attrs>[^>]*)>(?<body>.*?)</form>", RegexOptions.Singleline)]
    private static partial Regex Form();

    [GeneratedRegex(@"<input\b[^>]*>")]
    private static partial Regex Input();

    [GeneratedRegex(@"<select\b(?<attrs>[^>]*)>(?<body>.*?)</select>", RegexOptions.Singleline)]
    private static partial Regex Select();

    [GeneratedRegex(@"<option\b[^>]*>")]
    private static partial Regex Option();
}

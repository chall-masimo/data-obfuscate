using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CsvMasker.Web.Tests;

/// <summary>
/// The real app in-process, with its own temp folder, an optional path base (as under IIS at
/// /csvmasker), and every log line captured. Windows authentication is replaced by a test scheme:
/// the X-Test-User and X-Test-Groups headers become the signed-in user and their groups.
/// </summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    public const string Group = @"TESTDOMAIN\CsvMasker Users";
    public const string DefaultUser = @"TESTDOMAIN\tester";

    private readonly string _environment;

    public TestApp(string? pathBase = null, IDictionary<string, string?>? settings = null, string environment = "Production")
    {
        PathBase = pathBase ?? "";
        Settings = settings ?? new Dictionary<string, string?>();
        Settings.TryAdd("Authorization:AllowedGroup", Group);
        _environment = environment;
        TempFolder = Path.Combine(Path.GetTempPath(), "CsvMaskerWebTests", Guid.NewGuid().ToString("N"));
    }

    public string PathBase { get; }
    public IDictionary<string, string?> Settings { get; }
    public string TempFolder { get; }
    public LogCapture Logs { get; } = new();

    public string[] TempFiles() => Directory.Exists(TempFolder) ? Directory.GetFiles(TempFolder) : [];

    /// <summary>A browser signed in as <paramref name="user"/> (a member of the allowed group unless <paramref name="groups"/> says otherwise).</summary>
    public Browser Browser(string? user = DefaultUser, params string[]? groups)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        if (user is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
            client.DefaultRequestHeaders.Add(TestAuthHandler.GroupsHeader, string.Join(";", groups is { Length: > 0 } ? groups : [Group]));
        }
        return new Browser(client, PathBase);
    }

    /// <summary>A browser with no credentials.</summary>
    public Browser Anonymous() => Browser(user: null);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.UseSetting("Storage:TempFolder", TempFolder);
        foreach (var (key, value) in Settings)
            builder.UseSetting(key, value);
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureServices(services =>
        {
            services.AddTransient<IStartupFilter>(_ => new PathBaseStartupFilter(PathBase));
            services.AddAuthentication(o =>
                {
                    o.DefaultScheme = TestAuthHandler.SchemeName;
                    o.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    o.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    o.DefaultForbidScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            if (Directory.Exists(TempFolder))
                Directory.Delete(TempFolder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class PathBaseStartupFilter(string pathBase) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            // Negotiate inspects every request and needs Kestrel or IIS; the in-memory test server
            // is neither, so the test scheme stands in for it entirely.
            app.ApplicationServices.GetRequiredService<IAuthenticationSchemeProvider>()
                .RemoveScheme(Microsoft.AspNetCore.Authentication.Negotiate.NegotiateDefaults.AuthenticationScheme);
            if (pathBase.Length > 0)
                app.UsePathBase(pathBase);
            next(app);
        };
    }
}

/// <summary>Stands in for Windows authentication: user and groups come from request headers.</summary>
public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string UserHeader = "X-Test-User";
    public const string GroupsHeader = "X-Test-Groups";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var user))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim> { new(ClaimTypes.Name, user.ToString()) };
        foreach (var group in Request.Headers[GroupsHeader].ToString().Split(';', StringSplitOptions.RemoveEmptyEntries))
            claims.Add(new Claim(ClaimTypes.Role, group));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // Like Negotiate's first handshake leg: a bare 401. The app itself must keep it from being
        // re-executed as a status page.
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = SchemeName;
        return Task.CompletedTask;
    }
}

/// <summary>Captures every log line the app writes (subject to the app's own level filters).</summary>
public sealed class LogCapture : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(LogCapture capture, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            capture.Lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
    }
}

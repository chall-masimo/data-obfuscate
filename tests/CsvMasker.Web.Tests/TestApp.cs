using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CsvMasker.Web.Tests;

/// <summary>
/// The real app in-process, with its own temp folder, an optional path base (as under IIS at
/// /csvmasker), a test user taken from the X-Test-User header, and every log line captured.
/// </summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    public TestApp(string? pathBase = null, IDictionary<string, string?>? settings = null)
    {
        PathBase = pathBase ?? "";
        Settings = settings ?? new Dictionary<string, string?>();
        TempFolder = Path.Combine(Path.GetTempPath(), "CsvMaskerWebTests", Guid.NewGuid().ToString("N"));
    }

    public string PathBase { get; }
    public IDictionary<string, string?> Settings { get; }
    public string TempFolder { get; }
    public LogCapture Logs { get; } = new();

    public string[] TempFiles() => Directory.Exists(TempFolder) ? Directory.GetFiles(TempFolder) : [];

    public Browser Browser(string? user = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        if (user is not null)
            client.DefaultRequestHeaders.Add("X-Test-User", user);
        return new Browser(client, PathBase);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("Storage:TempFolder", TempFolder);
        foreach (var (key, value) in Settings)
            builder.UseSetting(key, value);
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureServices(services => services.AddTransient<IStartupFilter>(_ => new TestStartupFilter(PathBase)));
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

    private sealed class TestStartupFilter(string pathBase) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            if (pathBase.Length > 0)
                app.UsePathBase(pathBase);
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue("X-Test-User", out var user))
                    context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user.ToString())], "Test"));
                return nextMiddleware(context);
            });
            next(app);
        };
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

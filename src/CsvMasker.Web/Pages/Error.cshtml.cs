using System.Diagnostics;
using CsvMasker.Web.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CsvMasker.Web.Pages;

/// <summary>
/// Unhandled-error page. The framework's own exception logging is switched off in
/// appsettings.json (an exception message could echo data); this logs a sanitized line instead.
/// </summary>
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
[Microsoft.AspNetCore.Authorization.AllowAnonymous]
public class ErrorModel(ILogger<ErrorModel> logger) : PageModel
{
    public string? RequestId { get; set; }

    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

    public void OnGet() => Handle();

    public void OnPost() => Handle();

    private void Handle()
    {
        RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
        if (HttpContext.Features.Get<IExceptionHandlerPathFeature>() is { } failure)
            logger.LogError("Unhandled {Error} on {Path} (request {RequestId})", SafeErrors.ForLog(failure.Error), failure.Path, RequestId);
    }
}

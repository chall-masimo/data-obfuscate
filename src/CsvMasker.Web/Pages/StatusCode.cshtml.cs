using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CsvMasker.Web.Pages;

/// <summary>Friendly pages for error status codes, re-executed by UseStatusCodePagesWithReExecute.</summary>
[AllowAnonymous]
[IgnoreAntiforgeryToken]
public sealed class StatusCodeModel(ILogger<StatusCodeModel> logger) : PageModel
{
    public int Code { get; private set; }

    public void OnGet(int code) => Handle(code);

    public void OnPost(int code) => Handle(code);

    private void Handle(int code)
    {
        Code = code;
        if (code == StatusCodes.Status403Forbidden)
            logger.LogWarning("Access denied for {User}", User.Identity?.Name ?? "(unknown)");
    }
}

using CsvMasker.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CsvMasker.Web.Pages;

/// <summary>Friendly pages for error status codes, re-executed by UseStatusCodePagesWithReExecute.</summary>
[AllowAnonymous]
[IgnoreAntiforgeryToken]
public sealed class StatusCodeModel(IAuthorizationService authorization, ILogger<StatusCodeModel> logger) : PageModel
{
    public int Code { get; private set; }

    /// <summary>For a 403: true when the user is in the access group and was refused one action (e.g. deleting someone else's recipe).</summary>
    public bool IsMember { get; private set; }

    public Task OnGetAsync(int code) => HandleAsync(code);

    public Task OnPostAsync(int code) => HandleAsync(code);

    private async Task HandleAsync(int code)
    {
        Code = code;
        if (code != StatusCodes.Status403Forbidden)
            return;

        IsMember = (await authorization.AuthorizeAsync(User, AllowedGroupRequirement.PolicyName)).Succeeded;
        if (IsMember)
            logger.LogWarning("{User} was refused an action", User.Identity?.Name);
        else
            logger.LogWarning("Access denied for {User}", User.Identity?.Name ?? "(unknown)");
    }
}

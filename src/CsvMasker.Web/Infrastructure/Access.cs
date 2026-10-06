using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace CsvMasker.Web.Infrastructure;

/// <summary>Who may use the tool: members of one AD group (security rule 6).</summary>
public sealed class AccessOptions
{
    public const string Section = "Authorization";

    /// <summary>DOMAIN\Group. Required outside Development; set it in the server's appsettings.Production.json.</summary>
    public string AllowedGroup { get; set; } = "";
}

/// <summary>Fails startup outside Development when no group is configured, so a bad deploy is caught at once.</summary>
public sealed class AccessOptionsValidator(IHostEnvironment environment) : IValidateOptions<AccessOptions>
{
    public ValidateOptionsResult Validate(string? name, AccessOptions options) =>
        string.IsNullOrWhiteSpace(options.AllowedGroup) && !environment.IsDevelopment()
            ? ValidateOptionsResult.Fail("Authorization:AllowedGroup is not set. Set it to the AD group (DOMAIN\\Group) whose members may use CSV Masker.")
            : ValidateOptionsResult.Success;
}

public sealed class AllowedGroupRequirement : IAuthorizationRequirement
{
    public const string PolicyName = "AllowedGroup";
}

/// <summary>
/// Succeeds for members of <see cref="AccessOptions.AllowedGroup"/>. For Windows identities
/// IsInRole checks the logon token's groups (nested groups included). With no group configured,
/// which is only possible in Development, any authenticated user is allowed.
/// </summary>
public sealed class AllowedGroupHandler(IOptions<AccessOptions> options) : AuthorizationHandler<AllowedGroupRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AllowedGroupRequirement requirement)
    {
        string group = options.Value.AllowedGroup.Trim();
        if (context.User.Identity?.IsAuthenticated == true && (group.Length == 0 || IsMember(context.User, group)))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }

    /// <summary>Windows group names are case-insensitive; role claims (non-Windows identities) are compared the same way.</summary>
    internal static bool IsMember(ClaimsPrincipal user, string group) =>
        user.IsInRole(group)
        || user.Identities.Any(identity => identity.FindAll(identity.RoleClaimType)
            .Any(claim => string.Equals(claim.Value, group, StringComparison.OrdinalIgnoreCase)));
}

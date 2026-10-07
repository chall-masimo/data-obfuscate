using CsvMasker.Web.Infrastructure;
using CsvMasker.Web.Jobs;
using CsvMasker.Web.Options;
using CsvMasker.Web.Recipes;
using CsvMasker.Web.Storage;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<StorageOptions>().BindConfiguration(StorageOptions.Section).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<StorageOptions>, StorageOptionsValidator>();
builder.Services.AddOptions<LimitsOptions>().BindConfiguration(LimitsOptions.Section).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<AccessOptions>().BindConfiguration(AccessOptions.Section).ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<AccessOptions>, AccessOptionsValidator>();

// One upload limit, applied everywhere a request body can be rejected. IIS request filtering
// gets the same value from web.config (a test checks they agree).
var limits = builder.Configuration.GetSection(LimitsOptions.Section).Get<LimitsOptions>() ?? new LimitsOptions();
builder.Services.Configure<IISServerOptions>(o => o.MaxRequestBodySize = limits.MaxUploadBytes);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = limits.MaxUploadBytes);
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = limits.MaxUploadBytes;
    o.ValueCountLimit = 20_000; // the review form has ~10 fields per column
});

// Windows authentication: under IIS this defers to IIS Windows auth; on Kestrel (local dev) it
// does Kerberos/NTLM itself. Every endpoint, static files included, requires the AD group via
// the fallback policy, so nothing can be left open by a forgotten attribute.
builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
    .AddNegotiate();
builder.Services.AddSingleton<IAuthorizationHandler, AllowedGroupHandler>();
builder.Services.AddAuthorization(o =>
{
    var policy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .AddRequirements(new AllowedGroupRequirement())
        .Build();
    o.AddPolicy(AllowedGroupRequirement.PolicyName, policy);
    o.DefaultPolicy = policy;
    o.FallbackPolicy = policy;
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TempFileStore>();
builder.Services.AddSingleton<JobRegistry>();
builder.Services.AddSingleton<JobRunner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobRunner>());
builder.Services.AddSingleton(sp =>
{
    string folder = sp.GetRequiredService<IOptions<StorageOptions>>().Value.ResolveRecipeFolder();
    if (TempFileStore.IsInside(folder, sp.GetRequiredService<IWebHostEnvironment>().WebRootPath))
        throw new InvalidOperationException("Storage:RecipeFolder must be outside the web root.");
    return new RecipeStore(folder, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<RecipeStore>>());
});
builder.Services.AddSingleton<TempFolderSweeper>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TempFolderSweeper>());

builder.Services.AddRazorPages();

var app = builder.Build();

// Fail at startup, not on the first upload, if a storage folder is misconfigured.
_ = app.Services.GetRequiredService<IOptions<StorageOptions>>().Value;
_ = app.Services.GetRequiredService<TempFileStore>();
_ = app.Services.GetRequiredService<RecipeStore>();

// No HSTS: it applies to the whole host name, not just /csvmasker, and would force HTTPS on the
// shared server's other HTTP-only apps (e.g. SSRS) for anyone who had used this tool.
if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");

// Friendly 403 (not in the group) and 404 pages.
app.UseStatusCodePagesWithReExecute("/StatusCode", "?code={0}");

// Never re-execute a 401: it's a step in the Kerberos/NTLM handshake, and running
// authentication a second time mid-handshake fails ("incomplete authentication context").
app.Use(async (context, next) =>
{
    await next(context);
    if (context.Response.StatusCode == StatusCodes.Status401Unauthorized
        && context.Features.Get<IStatusCodePagesFeature>() is { } statusPages)
    {
        statusPages.Enabled = false;
    }
});

// Redirects to HTTPS only when the site has an HTTPS binding; with HTTP only (the current
// deployment decision) it does nothing.
app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// Bundled CSS/JS only; open so the access-denied page renders styled for non-members.
app.MapStaticAssets().AllowAnonymous();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();

public partial class Program;

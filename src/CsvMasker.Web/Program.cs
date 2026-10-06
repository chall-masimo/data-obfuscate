using CsvMasker.Web.Jobs;
using CsvMasker.Web.Options;
using CsvMasker.Web.Storage;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<StorageOptions>().BindConfiguration(StorageOptions.Section).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<LimitsOptions>().BindConfiguration(LimitsOptions.Section).ValidateDataAnnotations().ValidateOnStart();

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

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TempFileStore>();
builder.Services.AddSingleton<JobRegistry>();
builder.Services.AddSingleton<JobRunner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobRunner>());
builder.Services.AddSingleton<TempFolderSweeper>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TempFolderSweeper>());

builder.Services.AddRazorPages();

var app = builder.Build();

// Fail at startup, not on the first upload, if the temp folder is misconfigured.
_ = app.Services.GetRequiredService<TempFileStore>();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();

public partial class Program;

using CsvMasker.Web.Infrastructure;
using CsvMasker.Web.Storage;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace CsvMasker.Web.Upload;

public sealed record UploadedFile(string Path, string FileName, long Bytes);

/// <summary>A rejected upload; the message is safe to show (it never contains file content).</summary>
public sealed class UploadException(string message, int statusCode = StatusCodes.Status400BadRequest) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Streams the uploaded file straight into the temp store, enforcing the size limit while it
/// streams. Unlike IFormFile, nothing is buffered into ASP.NET's own temp folder, so the raw file
/// only ever exists in Storage:TempFolder.
/// </summary>
public static class MultipartUpload
{
    public static async Task<UploadedFile> SaveFileAsync(HttpRequest request, TempFileStore store, long maxBytes, CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || !string.Equals(mediaType.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value is not { Length: > 0 } boundary)
        {
            throw new UploadException("Expected a file upload.");
        }

        var reader = new MultipartReader(boundary, request.Body);
        try
        {
            while (await reader.ReadNextSectionAsync(cancellationToken) is { } section)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                    || !disposition.IsFileDisposition())
                {
                    continue; // other fields are skipped; the next read drains them
                }

                string fileName = Path.GetFileName(HeaderUtilities.RemoveQuotes(
                    disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value ?? "");
                if (string.IsNullOrWhiteSpace(fileName))
                    fileName = "upload.csv";

                string path = store.NewPath("upload");
                try
                {
                    long total = await CopyAsync(section.Body, store, path, maxBytes, cancellationToken);
                    if (total == 0)
                        throw new UploadException("The file is empty.");
                    return new UploadedFile(path, fileName, total);
                }
                catch
                {
                    store.Delete(path);
                    throw;
                }
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            throw TooLarge(maxBytes);
        }

        throw new UploadException("No file was uploaded.");
    }

    public static UploadException TooLarge(long maxBytes) =>
        new($"The file is larger than the {Display.Bytes(maxBytes)} limit.", StatusCodes.Status413PayloadTooLarge);

    private static async Task<long> CopyAsync(Stream source, TempFileStore store, string path, long maxBytes, CancellationToken cancellationToken)
    {
        await using var file = store.Create(path);
        var buffer = new byte[81_920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw TooLarge(maxBytes);
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return total;
    }
}

/// <summary>Stops MVC from reading the request body as a form, so the upload handler can stream it.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class DisableFormValueModelBindingAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        context.ValueProviderFactories.RemoveType<FormValueProviderFactory>();
        context.ValueProviderFactories.RemoveType<FormFileValueProviderFactory>();
        context.ValueProviderFactories.RemoveType<JQueryFormValueProviderFactory>();
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}

using CsvMasker.Web.Options;
using Microsoft.Extensions.Options;

namespace CsvMasker.Web.Storage;

/// <summary>
/// The only place raw uploads and masked outputs live: a dedicated folder outside the web root,
/// with random file names. Callers delete files as soon as they're done; the sweeper catches the rest.
/// </summary>
public sealed class TempFileStore
{
    private readonly ILogger<TempFileStore> _logger;

    public TempFileStore(IOptions<StorageOptions> options, IWebHostEnvironment environment, ILogger<TempFileStore> logger)
    {
        _logger = logger;
        Folder = options.Value.ResolveTempFolder();

        if (IsInside(Folder, environment.WebRootPath))
            throw new InvalidOperationException("Storage:TempFolder must be outside the web root.");
        if (IsInside(Folder, options.Value.ResolveRecipeFolder()) || IsInside(options.Value.ResolveRecipeFolder(), Folder))
            throw new InvalidOperationException("Storage:TempFolder and Storage:RecipeFolder must be separate folders (the sweeper deletes old temp files).");

        Directory.CreateDirectory(Folder);
    }

    public string Folder { get; }

    public string NewPath(string kind) => Path.Combine(Folder, $"{Guid.NewGuid():N}.{kind}");

    public FileStream Create(string path) =>
        new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, FileOptions.SequentialScan);

    public FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81_920, FileOptions.SequentialScan);

    /// <summary>Opens a file that is deleted as soon as the stream is closed (downloads).</summary>
    public FileStream OpenReadAndDeleteOnClose(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.None, 81_920, FileOptions.SequentialScan | FileOptions.DeleteOnClose);

    public void Delete(string? path)
    {
        if (path is null)
            return;
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The sweeper retries later. File names are random GUIDs, so logging the name is safe.
            _logger.LogWarning("Could not delete temp file {File}: {ErrorType}", Path.GetFileName(path), ex.GetType().Name);
        }
    }

    public IEnumerable<FileInfo> Files() => new DirectoryInfo(Folder).EnumerateFiles();

    public static bool IsInside(string folder, string? root)
    {
        if (string.IsNullOrEmpty(root))
            return false;
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        string rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
    }
}

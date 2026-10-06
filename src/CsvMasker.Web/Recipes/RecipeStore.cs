using System.Text.Json;

namespace CsvMasker.Web.Recipes;

public enum RecipeChange
{
    Done,
    NotFound,
    NotOwner,
}

/// <summary>
/// Recipes as one JSON file each (<c>{id}.json</c>) in Storage:RecipeFolder. File names never
/// come from user input. Writes are atomic (temp file, then rename), and every update or delete
/// checks the owner.
/// </summary>
public sealed class RecipeStore(string folder, TimeProvider time, ILogger<RecipeStore> logger)
{
    private readonly Lock _lock = new();

    public string Folder { get; } = Directory.CreateDirectory(folder).FullName;

    public IReadOnlyList<Recipe> List()
    {
        lock (_lock)
        {
            var recipes = new List<Recipe>();
            foreach (var path in Directory.EnumerateFiles(Folder, "*.json"))
                if (Read(path) is { } recipe)
                    recipes.Add(recipe);
            return recipes;
        }
    }

    public Recipe? Get(Guid id)
    {
        lock (_lock)
            return File.Exists(PathFor(id)) ? Read(PathFor(id)) : null;
    }

    public Recipe Create(string name, string owner, string headerSignature, List<RecipeColumn> columns, List<RecipeEntityGroup> entityGroups, bool skipMalformed)
    {
        var now = time.GetUtcNow();
        var recipe = new Recipe
        {
            Id = Guid.NewGuid(),
            Name = CleanName(name),
            Owner = owner,
            Created = now,
            Updated = now,
            HeaderSignature = headerSignature,
            Columns = columns,
            EntityGroups = entityGroups,
            SkipMalformed = skipMalformed,
        };
        lock (_lock)
            Write(recipe);
        logger.LogInformation("Recipe {RecipeId} saved by {User} ({Columns} columns)", recipe.Id, owner, columns.Count);
        return recipe;
    }

    /// <summary>Replaces a recipe's layout and strategies. Only its owner may do this.</summary>
    public RecipeChange Update(Guid id, string user, string headerSignature, List<RecipeColumn> columns, List<RecipeEntityGroup> entityGroups, bool skipMalformed)
    {
        lock (_lock)
        {
            var recipe = File.Exists(PathFor(id)) ? Read(PathFor(id)) : null;
            if (recipe is null)
                return RecipeChange.NotFound;
            if (!recipe.IsOwnedBy(user))
                return RecipeChange.NotOwner;

            recipe.HeaderSignature = headerSignature;
            recipe.Columns = columns;
            recipe.EntityGroups = entityGroups;
            recipe.SkipMalformed = skipMalformed;
            recipe.Updated = time.GetUtcNow();
            Write(recipe);
        }
        logger.LogInformation("Recipe {RecipeId} updated by {User} ({Columns} columns)", id, user, columns.Count);
        return RecipeChange.Done;
    }

    public RecipeChange Delete(Guid id, string user)
    {
        lock (_lock)
        {
            var recipe = File.Exists(PathFor(id)) ? Read(PathFor(id)) : null;
            if (recipe is null)
                return RecipeChange.NotFound;
            if (!recipe.IsOwnedBy(user))
                return RecipeChange.NotOwner;
            File.Delete(PathFor(id));
        }
        logger.LogInformation("Recipe {RecipeId} deleted by {User}", id, user);
        return RecipeChange.Done;
    }

    public static string CleanName(string name)
    {
        string trimmed = name.Trim();
        return trimmed.Length <= Recipe.MaxNameLength ? trimmed : trimmed[..Recipe.MaxNameLength];
    }

    private string PathFor(Guid id) => Path.Combine(Folder, $"{id:D}.json");

    private void Write(Recipe recipe)
    {
        string target = PathFor(recipe.Id);
        string temp = Path.Combine(Folder, $"{recipe.Id:D}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(recipe, RecipeJson.Options));
        File.Move(temp, target, overwrite: true);
    }

    private Recipe? Read(string path)
    {
        try
        {
            var recipe = JsonSerializer.Deserialize<Recipe>(File.ReadAllText(path), RecipeJson.Options);
            if (recipe is null || recipe.Id == Guid.Empty || recipe.SchemaVersion > Recipe.CurrentSchemaVersion)
                throw new JsonException("Unsupported recipe.");
            return recipe;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogWarning("Recipe file {File} could not be read and was skipped: {ErrorType}", Path.GetFileName(path), ex.GetType().Name);
            return null;
        }
    }
}

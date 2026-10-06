using System.Text.Json;
using System.Text.Json.Serialization;
using CsvMasker.Core.Masking;

namespace CsvMasker.Web.Recipes;

/// <summary>
/// A saved column→strategy configuration for one file layout. Configuration only: column names,
/// strategies and options. Never data values, sample values or file names (security rule 5).
/// </summary>
public sealed class Recipe
{
    public const int CurrentSchemaVersion = 1;
    public const int MaxNameLength = 100;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Guid Id { get; set; }

    /// <summary>A label chosen by the owner, e.g. "Monthly bookings extract".</summary>
    public string Name { get; set; } = "";

    /// <summary>DOMAIN\user. Only the owner may update or delete.</summary>
    public string Owner { get; set; } = "";

    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Updated { get; set; }

    /// <summary>See <see cref="Recipes.HeaderSignature"/>.</summary>
    public string HeaderSignature { get; set; } = "";

    public bool SkipMalformed { get; set; }

    /// <summary>One entry per column, in file order.</summary>
    public List<RecipeColumn> Columns { get; set; } = [];

    /// <summary>Entity groups by column name: each anchor column and the columns linked to it.</summary>
    public List<RecipeEntityGroup> EntityGroups { get; set; } = [];

    public bool IsOwnedBy(string user) => string.Equals(Owner, user, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A column's strategy and the options that apply to it; options that don't apply are omitted.</summary>
public sealed class RecipeColumn
{
    public string Name { get; set; } = "";
    public MaskingStrategy Strategy { get; set; }
    public FakeKind? FakeKind { get; set; }
    public string? Prefix { get; set; }
    public int? PerturbPercent { get; set; }
    public PerturbMode? PerturbMode { get; set; }
    public bool? IsCount { get; set; }
    public int? MaxDays { get; set; }
    public bool? KeepWeekday { get; set; }
    public DateShiftMode? DateShiftMode { get; set; }
    public string? RedactText { get; set; }

    /// <summary>Name of the column whose mapping this column shares (e.g. ShipTo shares BillTo's), if any.</summary>
    public string? MappingDomain { get; set; }
}

/// <summary>An entity group by column name.</summary>
public sealed class RecipeEntityGroup
{
    public string Anchor { get; set; } = "";
    public List<string> Members { get; set; } = [];
}

public static class RecipeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}

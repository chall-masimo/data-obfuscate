namespace CsvMasker.Core.Masking;

/// <summary>Masking strategies (see CLAUDE.md). Implemented in build step 3.</summary>
public enum MaskingStrategy
{
    Keep,
    HashId,
    Fake,
    ZipRemap,
    Perturb,
    DateShift,
    Redact,
    Lorem,
}

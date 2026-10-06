using CsvMasker.Core.Csv;
using CsvMasker.Core.Masking.Strategies;
using CsvMasker.Core.Masking.Verification;

namespace CsvMasker.Core.Masking;

/// <summary>
/// Streams a file through the plan one row at a time: read → mask each column → write → verify.
/// Memory is the reader/writer buffers plus the mapping dictionaries (capped) plus bounded
/// verification counters, regardless of file size.
/// </summary>
internal static class MaskingPipeline
{
    /// <param name="onRow">Called with each source row and its masked row; return false to stop (used by preview).</param>
    public static VerificationReport Run(
        Stream input, CsvDialect dialect, Stream output, MaskingPlan plan, byte[] key, MaskingOptions options,
        IProgress<long>? progress, CancellationToken cancellationToken, Func<CsvRecord, CsvRecord, bool>? onRow)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(plan);

        using var reader = new CsvReader(input, dialect, options.Reader, leaveOpen: true);
        var header = reader.Header;
        PlanValidator.Validate(plan, header);

        var seeds = new SeedSource(key);
        var store = new MappingStore(options.MaxMappingEntries);
        var maskers = new ColumnMasker[header.Count];
        var verifiers = new ColumnVerifier[header.Count];
        for (int i = 0; i < header.Count; i++)
        {
            string columnKey = header.Keys[i];
            var rule = plan.Columns[columnKey];
            string domain = rule.MappingDomain ?? columnKey;
            var group = plan.EntityGroups.FirstOrDefault(g => g.Members.Contains(columnKey, StringComparer.Ordinal));
            int? anchorIndex = group is null ? null : header.IndexOf(group.Anchor);
            var strategy = CreateStrategy(rule, domain, seeds, options);
            verifiers[i] = new ColumnVerifier(columnKey, rule.Strategy, strategy.IsMapping)
            {
                Warning = strategy.Warning,
                PerEntity = group is not null,
            };
            maskers[i] = new ColumnMasker(columnKey, strategy, domain, store, verifiers[i], anchorIndex, group?.SeedDomain);
        }

        long rowsRead = 0, rowsWritten = 0, blankLines = 0, malformed = 0;
        var malformedNumbers = new List<long>();

        using (var writer = new CsvWriter(output, dialect, leaveOpen: true))
        {
            writer.WriteRecord(header.Record);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                CsvRecord? record;
                try
                {
                    if (!reader.TryRead(out record))
                        break;
                }
                catch (CsvFormatException ex) when (ex.Kind == CsvErrorKind.FieldCountMismatch && !options.FailOnMalformed)
                {
                    malformed++;
                    if (malformedNumbers.Count < options.MaxMalformedRowsListed)
                        malformedNumbers.Add(ex.RecordNumber);
                    continue;
                }

                if (record.IsBlankLine)
                {
                    writer.WriteRecord(record);
                    blankLines++;
                    continue;
                }

                rowsRead++;
                var masked = new string[maskers.Length];
                for (int i = 0; i < maskers.Length; i++)
                    masked[i] = maskers[i].Mask(record.Values[i], record.IsNull(i), record);

                var maskedRecord = record.WithValues(masked);
                writer.WriteRecord(maskedRecord);
                rowsWritten++;

                for (int i = 0; i < verifiers.Length; i++)
                    verifiers[i].Record(record.Values[i], record.IsNull(i), masked[i], maskedRecord.IsNull(i), maskers[i].AnchorValue(record));

                if (progress is not null && rowsRead % options.ProgressInterval == 0)
                    progress.Report(rowsRead);
                if (onRow is not null && !onRow(record, maskedRecord))
                    break;
            }
        }

        progress?.Report(rowsRead);
        return new VerificationReport
        {
            RowsRead = rowsRead,
            RowsWritten = rowsWritten,
            BlankLines = blankLines,
            MalformedRowCount = malformed,
            MalformedRecordNumbers = malformedNumbers,
            Columns = verifiers.Select(v => v.Build()).ToArray(),
        };
    }

    internal static IMaskingStrategy CreateStrategy(ColumnRule rule, string domain, SeedSource seeds, MaskingOptions options) =>
        rule.Strategy switch
        {
            MaskingStrategy.Keep => new KeepStrategy(),
            MaskingStrategy.HashId => new HashIdStrategy(seeds, rule.Options as HashIdOptions ?? new HashIdOptions()),
            MaskingStrategy.Fake => new FakeStrategy(seeds, (FakeOptions)rule.Options!),
            MaskingStrategy.ZipRemap => new ZipRemapStrategy(seeds, options.ZipReference),
            MaskingStrategy.Perturb => new PerturbStrategy(seeds, domain, rule.Options as PerturbOptions ?? new PerturbOptions()),
            MaskingStrategy.DateShift => new DateShiftStrategy(seeds, domain, rule.Options as DateShiftOptions ?? new DateShiftOptions()),
            MaskingStrategy.Redact => new RedactStrategy(rule.Options as RedactOptions ?? new RedactOptions()),
            MaskingStrategy.Lorem => new LoremStrategy(seeds, domain),
            _ => throw new InvalidOperationException($"Unknown strategy {rule.Strategy}."),
        };
}

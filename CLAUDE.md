# Project Brief — CSV Masker (internal data obfuscation tool)

This file briefs Claude Code (or any new engineer) on this project's purpose, constraints,
and design. Read it before making changes.

---

## Purpose

An internal web tool that takes a CSV containing sensitive data and produces a masked copy
that is **safe to use for testing reports and analytics output**. The masked file must keep
the *shape* of the data (row count, cardinality, frequencies, nulls, formats, plausible value
ranges) so reports built on it behave like reports built on the real thing, while the actual
sensitive values are not recoverable.

### Goals
- Upload a CSV → automatic profiling → per-column masking suggestions → user review →
  preview → run → download.
- Masking is **one-directional and irreversible**. There is no unmask feature, ever.
- Consistency is required **within a single file only**: the same source value maps to the
  same masked value everywhere in that file. No cross-file or cross-run consistency.
- Used by a small internal team (Sales Ops), not a broad audience.

### Non-goals
- Not a formal anonymization tool (no k-anonymity guarantees). It's for test data.
- No database. No persistent storage of uploaded or masked data.
- No reverse mapping, no lookup tables persisted, no key storage.

---

## Stack & hosting constraints

- **.NET 10 (LTS), ASP.NET Core, Razor Pages.** Server-rendered pages with minimal vanilla JS.
  No SPA framework, no build pipeline for front-end assets.
- **Libraries:** `Bogus` (fake data generation). CSV read/write is in-house
  (`CsvMasker.Core/Csv`), not CsvHelper: byte-identical round trips need per-field quote state
  and per-record line endings, which CsvHelper doesn't expose, and its exception messages can
  include cell content. Prefer the BCL for everything else
  (`System.Security.Cryptography.HMACSHA256`, etc.). Keep the dependency list short and justify
  any addition.
- **No external CDNs or internet calls at runtime.** All JS/CSS ships with the app.
- **No Python anywhere.** Deliberate decision for this server.
- **Hosting:** IIS on Windows Server 2022, **in-process hosting** via the ASP.NET Core
  Module, deployed as an IIS *application* under an existing site (e.g. `/csvmasker`), so:
  - All links and form actions must be path-base-aware (`~/`, tag helpers, `asp-page`).
    Never hard-code root-relative URLs like `/upload`.
- **The server is shared** with SQL Server, SSIS, SSRS, Tableau Bridge, and other IIS
  sites. The tool must be a polite neighbor:
  - **Stream, don't load.** Read and write CSV row by row. Never materialize the whole file
    in memory (no `ReadToEnd`, no `GetRecords().ToList()` on the full file).
  - The app pool has a private-memory recycle limit. Design so a large file can't approach
    it. The main memory consumer is the per-column mapping dictionaries (see below).
  - One masking job at a time per user. Reject concurrent jobs from the same user.

---

## Security rules (non-negotiable)

1. **Never log cell values.** Not in app logs, not in exceptions, not in stdout. Log column
   names, row counts, timings, and error types only. Exception messages from CsvHelper can
   include raw field content; catch and sanitize before logging.
2. **Raw uploads live only in a dedicated temp folder** (configurable, outside the web root),
   with randomized file names. Delete the upload as soon as the job completes or fails.
   Delete the masked output immediately after download.
3. **Background sweeper:** a hosted service deletes anything in the temp folder older than a
   configurable age (default 60 minutes), covering abandoned sessions.
4. **Per-job random key:** each job generates a fresh 32-byte key with
   `RandomNumberGenerator`, held in memory only, discarded at job end. Never written to
   disk, logs, or recipes.
5. **Recipes contain no data.** Saved configs store column names, strategies, and options
   only. Sample values shown on the review screen are never persisted.
6. **Authentication:** Windows authentication (IIS). **Authorization:** require membership
   in a configurable AD group (`appsettings.json` → `Authorization:AllowedGroup`). Everyone
   else gets 403.
7. **Fail closed:** a job cannot run unless every column has an explicitly assigned strategy
   (including `Keep`). Unknown/new columns never pass through by default.

---

## Workflow (UI)

1. **Upload** — file picker, size limit shown on page. Header row required.
2. **Profile** — read up to N rows (configurable, default 50,000) and compute per column:
   inferred type, distinct count (exact up to the sample), null/blank rate, min/max length,
   leading-zero presence, numeric scale (decimal places), date formats seen, and 5 sample
   values. Label distinct counts as estimates when the file exceeds the sample.
3. **Review** — one row per column: name, detected type, sample values, suggested strategy
   (dropdown), strategy options, and entity-group assignment. If a saved recipe matches the
   header signature, pre-fill from it and say so.
4. **Preview** — first 20 rows, original vs masked, side by side.
5. **Run** — stream the full file through the masking pipeline.
6. **Download + verification report** — show per-column checks computed during the run
   (see Verification). Offer "save as recipe".

---

## Type detection heuristics

Read **every value as a string first.** Never let a parser coerce ZIPs, IDs, or codes to
numbers. Detection combines column-name hints with value patterns; name hints break ties.

| Detected type | Signals |
|---|---|
| `Identifier` | High cardinality (distinct/non-null > ~0.5), name matches `*ID`, `*_ID`, `*Id`, `*Key`, `*Number`, `*_No`, `*Code`; or digits with leading zeros / fixed width. **Never treated as a measure even if numeric.** |
| `Zip` | Name contains `zip`/`postal`; values match `^\d{5}(-\d{4})?$` (also 3–4 digit values if leading zeros were stripped upstream; flag that). |
| `Email` | Pattern match. |
| `Phone` | Pattern match / name hint. |
| `PersonName` | Name hints (`*Name` with `First`, `Last`, `Rep`, `Contact`, `Owner`, `Manager`). |
| `OrgName` | Name hints (`Customer`, `Account`, `Facility`, `Hospital`, `Company`, `Vendor` + `Name`). |
| `Address` / `City` / `State` | Name hints + patterns (2-letter state codes). |
| `Date` / `DateTime` | Parses consistently under one or more formats; record the formats. |
| `Measure` | Numeric, non-identifier, decimal values or name hints (`Amount`, `Revenue`, `Price`, `Cost`, `Margin`, `Total`). |
| `Count` | Integer, non-identifier, name hints (`Qty`, `Quantity`, `Count`, `Number_of_`, `Beds`). |
| `Boolean` | Two distinct values (Y/N, 1/0, True/False, Yes/No). |
| `Categorical` | Low cardinality (distinct ≤ ~50 or ratio < ~0.01). Status, region, service line, fiscal period. |
| `FreeText` | Long strings (avg length > ~60), high cardinality, spaces/punctuation. |

Suggested default strategy by type: `Identifier`→`HashId`, `Zip`→`ZipRemap`,
`Email`/`Phone`/`PersonName`/`OrgName`/`Address`/`City`→`Fake`, `State`→`Keep`,
`Date`→`DateShift`, `Measure`/`Count`→`Perturb`, `Boolean`/`Categorical`→`Keep`,
`FreeText`→`Redact`. These are suggestions only; the user confirms every column.

---

## Masking strategies

All deterministic strategies derive from:
`seed = HMACSHA256(jobKey, mappingDomain + "\u001F" + sourceValue)`.
`mappingDomain` defaults to the column name but can be shared between columns (see Entity
groups). Use the seed bytes to drive a seeded `Random`/Bogus `Faker` instance.

### Universal invariants (every strategy, enforced centrally, not per strategy)
- Null stays null; empty string stays empty; whitespace-only stays as-is.
- Row count and column order unchanged. Header unchanged.
- **Cardinality preserved exactly** for mapping strategies: distinct inputs → distinct outputs.
  Enforce with a per-domain `Dictionary<string,string>` of source→output plus a
  `HashSet<string>` of used outputs; on collision, re-derive with a counter suffix in the
  HMAC input until unique. These dictionaries are the main memory cost: track their size
  and fail the job cleanly (with a clear message) if a configurable cap is exceeded, rather
  than letting the app pool recycle mid-job.

| Strategy | Behavior & options |
|---|---|
| `Keep` | Pass through unchanged. Must be chosen explicitly. |
| `HashId` | Deterministic replacement preserving **format**: same length, same character classes per position (digits→digits, letters→letters, separators kept), leading-zero style kept. Option: `prefix` (e.g. `T-`). |
| `Fake` | Deterministic Bogus value of a chosen kind: `PersonFirst`, `PersonLast`, `PersonFull`, `Company`, `Hospital` (custom generator: fake city/place + "Medical Center"/"Hospital"/"Health"), `Email`, `Phone` (keep source format), `StreetAddress`, `City`. Option: `case` (preserve source casing pattern). |
| `ZipRemap` | Deterministic map to another ZIP **with the same 3-digit prefix**, preserving 5 vs ZIP+4 format and leading zeros. Uses a bundled reference list of real ZIPs (see Open items); fallback if no list: keep first 3 digits, derive last 2 (may not geocode — warn in UI). |
| `Perturb` | Multiply by a factor in `[1 - pct, 1 + pct]` (default pct 0.15). Modes: `PerRow`, `PerEntity` (factor seeded from an anchor column's value), `Global` (one factor for the column). Preserve: sign, zero, integer-ness, source decimal scale, source formatting (thousands separators, currency symbols, parentheses negatives). `Count` columns round to integers and never go below 0 (or 1 if source min ≥ 1). |
| `DateShift` | Shift by a whole number of days in `[-maxDays, +maxDays]` (default 30). Modes: `Global`, `PerEntity`. Preserve the exact source format per value and any time component. Option: `keepWeekday` (shift in multiples of 7). |
| `Redact` | Replace non-empty values with a constant (default `[REDACTED]`). |
| `Lorem` | Replace with deterministic lorem text of similar length (for free text where length matters to layout testing). |

### Entity groups
- Users can link columns into a group with one **anchor** column (e.g. `Customer_ID` anchors
  `Customer_Name`, `Address`, `City`, `Zip`). Linked columns are seeded from the anchor's
  value, so one source customer always gets one coherent fake identity (consistent name,
  city, ZIP, and state).
- Users can also put two columns in the **same mapping domain** (e.g. `BillTo_Customer` and
  `ShipTo_Customer`) so the same source value maps identically in both.
- `PerEntity` perturbation and date shifting reference an entity group's anchor.

### Derived columns (v2, design for it now)
Allow marking a column as derived from others (`Amount = Price * Qty`) and recompute it from
masked inputs instead of masking it independently. Not in v1, but don't architect it out.

---

## CSV handling

- Detect encoding (UTF-8 with/without BOM, UTF-16, fall back to Windows-1252) and write the
  output in the **same encoding**, BOM included if present.
- Detect delimiter (comma, tab, pipe, semicolon) from the header line; write the same.
- Respect RFC 4180 quoting; preserve line endings (CRLF vs LF) of the source.
- Duplicate or blank header names: disambiguate internally, write the originals back.
- Malformed rows: count and report them by row number (no content); configurable
  fail-vs-skip, default fail.

Decisions made while building step 1 (implemented in `CsvMasker.Core/Csv`):
- **Round trip is byte-identical.** Reading then writing an unmodified file reproduces it
  exactly: BOM, quoting per field (including quotes that weren't needed), line ending per
  record (mixed endings preserved), and a missing final newline.
- **Null vs empty:** an *unquoted* empty field is null; a quoted `""` is an empty string
  (`CsvRecord.IsNull`).
- **Record numbers** are 1-based with the header as record 1, which matches Excel's row number.
- **Field-count mismatch** is a malformed row (`CsvErrorKind.FieldCountMismatch`). The reader
  stays usable afterwards, so a skip mode can continue. Quote errors are not recoverable.
- **Lenient on stray quotes:** a quote inside an unquoted field (`5" pipe`) is kept literally
  and written back unquoted. Text after a closing quote (`"a"b`) is malformed.
- **Writer quoting:** source-quoted fields stay quoted. A source-unquoted field is quoted only
  if its (masked) value contains the delimiter or a line break, or starts with a quote. Values
  with no source are quoted by strict RFC 4180 rules.
- **Blank lines** in a multi-column file are passed through verbatim (`IsBlankLine`) and are
  not data rows. In a one-column file an empty line is a row with a null value.
- **Encodings:** BOM detection covers UTF-8, UTF-16 LE/BE and UTF-32 LE/BE. Without a BOM,
  UTF-16 is detected by a null-byte heuristic. Otherwise the **whole file** is scanned as
  strict UTF-8 (constant memory), falling back to Windows-1252. All encoders and decoders throw
  rather than substitute `?`/U+FFFD.
- **Delimiter:** count `,` tab `;` `|` outside quotes in the header line. Ties are broken by
  field-count consistency over the next 20 records, then by that order. A single-column file
  defaults to comma.
- **Record size cap:** `CsvReaderOptions.MaxRecordChars` (default 1,000,000) bounds memory
  when a closing quote is missing. It should become `Limits:MaxRecordChars` in step 4.
- `CsvFormatException` messages carry record/field numbers only, with no inner exception.

---

## Recipes

- Stored as JSON in a configurable folder outside the web root.
- Keyed by a **header signature** (SHA-256 of normalized, ordered column names).
- Contain: name, owner, created/updated, header signature, per-column strategy + options,
  entity groups. **No data values.**
- On upload, an exact signature match pre-fills the review screen. A partial match
  (columns added/removed) pre-fills what matches and highlights the rest as unassigned:
  the job stays blocked until they're assigned.

---

## Verification report (computed during the run, shown on the download page)

Per column: source vs output distinct count (must match for mapping strategies), null count
(must match), row count (must match), and for `Perturb` columns the source vs output sum
and the % difference. Any invariant failure is shown prominently, and the download is still
allowed but flagged.

---

## Suggested solution layout

```
CsvMasker.slnx               # .NET 10 XML solution format
src/CsvMasker.Core/        # profiling, detection, strategies, pipeline — no ASP.NET refs
src/CsvMasker.Web/         # Razor Pages UI, auth, temp-file service, sweeper, recipes
tests/CsvMasker.Core.Tests/  # xUnit
```

Keep all masking logic in `Core` with no web dependencies so it can be unit tested in
isolation and, if ever needed, wrapped in a CLI.

## Testing expectations

- Unit tests per strategy, including the universal invariants.
- Property-style tests on generated CSVs: row count, null positions, and distinct counts
  preserved; no output value equals its source value for mapping strategies (allowing for
  `Keep`).
- Encoding/delimiter round-trip tests (UTF-8 BOM, Windows-1252, tab, pipe, quoted newlines).
- A memory test that streams a large synthetic file (e.g. 2M rows) and asserts the working
  set stays bounded apart from mapping dictionaries.

## Build order

1. `Core`: CSV read/write round-trip with encoding/delimiter preservation (no masking).
2. `Core`: profiler + type detection.
3. `Core`: strategies + invariants + pipeline + verification stats.
4. `Web`: upload → profile → review → preview → run → download, temp-file handling, sweeper.
5. `Web`: Windows auth + AD group authorization.
6. Recipes.
7. Entity groups and `PerEntity` modes.

## Configuration (`appsettings.json`)

`Authorization:AllowedGroup`, `Storage:TempFolder`, `Storage:RecipeFolder`,
`Storage:SweepAgeMinutes`, `Limits:MaxUploadBytes`, `Limits:ProfileSampleRows`,
`Limits:MaxMappingEntries`. The upload limit must agree with the IIS `web.config`
`maxAllowedContentLength`, `IISServerOptions.MaxRequestBodySize`, and
`FormOptions.MultipartBodyLengthLimit`. All three are set from the one config value where
possible, and the project includes its own `web.config` so publish doesn't regenerate it
without the request-filtering limit.

## Open items (confirm with Chris)

- AD group name for authorization.
- Maximum upload size.
- Source for the bundled US ZIP reference list (public dataset; must be licensed for
  internal use) and whether non-US postal codes need handling.
- IIS site/application path (assumed `/csvmasker` under an existing HTTPS site).

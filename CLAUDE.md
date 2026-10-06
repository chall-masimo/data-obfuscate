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

Decisions made while building step 2 (implemented in `CsvMasker.Core/Profiling`):
- **Extra types:** `Text` (no rule matched) → suggest `Redact`, since it may be sensitive.
  `Empty` (no non-blank values) → suggest `Keep`. **`Keep` is always an allowed choice for
  every type**, so the user can leave any field alone; it just has to be chosen explicitly.
- **Distinct counts** are exact up to `ExactDistinctLimit` (default 1,000) per column, then a
  HyperLogLog estimate (16 KB per column, ≤ ~2% error). This bounds profiling memory on wide
  files. `DistinctIsEstimate` is true for an estimate **or** when the file is longer than the
  sample.
- **Null vs blank:** null = unquoted empty; blank = quoted `""` or whitespace-only. They are
  counted separately. Distinct counts, lengths and patterns use the remaining values.
- **"Matches a pattern"** means ≥ 95% of non-blank values (`PatternMatchRatio`), so a few
  dirty values don't break detection.
- **Precedence** (first match wins): Empty → Email → Zip → Phone → Date/DateTime → Boolean →
  State → Identifier → Person/Org name → City → Address → Count → hinted Measure → Categorical
  → other numeric (Measure) → FreeText → Text. Details:
  - Zip needs a `zip`/`postal` name hint, unless the values are ZIP+4.
  - Phone needs a name hint, unless the values are *formatted*. Bare 10-digit numbers are
    identifiers.
  - `ID`/`Key`/`No`/`Number` at the end of a name is a strong identifier hint. `Code` is weak:
    it only counts with high cardinality or leading zeros, so `Region_Code` with 6 values is
    Categorical.
  - Value-only identifier rules (fixed-width codes, near-unique integers) yield to a
    count/measure name hint (`Number_of_Beds`, `Amount`).
  - Person vs org is decided by the qualifier closest before `Name`, so `Account_Owner_Name`
    is a person.
- **Dates:** formats are tried with InvariantCulture. A digit-only `yyyyMMdd` needs a date-like
  name hint. Excel serial numbers are not dates (v1). When every value fits both M/d and d/M,
  month/day (US) is assumed and a warning is shown. Mixed formats in one column are allowed
  and all recorded.
- **Numbers:** US formats only (`$1,234.50`, `(12.00)`). Decimal-comma values are text in v1.
- **Sample values** are the first 5 distinct non-blank values. They are in memory only, and
  `ToString()` on profiles omits them. Reasons and warnings never contain cell values.
- **Malformed rows** (field-count mismatch) found while profiling are skipped and listed by
  record number (first 100 + total). Quote errors stop the profile.

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

### Decisions made while building step 3 (implemented in `CsvMasker.Core/Masking`)
- **Bogus** is referenced by `CsvMasker.Core` only. Each value reseeds one shared `Faker` from
  its HMAC seed, so output is deterministic within a job and unrelated across jobs.
- **Seeds:** `HMACSHA256(jobKey, domain ␟ value)` (␟ = U+001F). Variations append further
  fields after another ␟:
  - collision retries add `␟n`;
  - PerRow perturbation adds `␟row:{recordNumber}`;
  - Global modes seed from `domain ␟ U+001E "global"`.
- **Mapping strategies** (cardinality kept exactly, output never equals source, cached per
  domain) are `HashId`, `Fake` and `ZipRemap`. The other strategies are deterministic but not
  tracked, so they cost no memory:
  - `Perturb`, `DateShift` and `Lorem`;
  - `Redact`, which outputs a constant;
  - a Global date shift, which is a bijection anyway.
- **`Limits:MaxMappingEntries`** defaults to 2,000,000 entries across all domains. Exceeding
  it fails the job cleanly (`MaskingException`, naming the column only).
- **Fake pool exhausted:** after 50 colliding retries, a numeric suffix is appended (`Maria 2`;
  for emails the number goes before `@`). The report counts suffixed values.
- **Fake emails** use only the RFC 2606 reserved domains `example.com/.net/.org`.
- **HashId zero padding:** a multi-digit run that starts with `0` still starts with `0`, and
  one that doesn't never gains a leading zero; all other positions are random. After 100
  random collisions it steps through the remaining output space deterministically, so dense
  sequential IDs always find a free value if one exists.
- **Unmaskable values** (`N/A` in Perturb, `TBD` in DateShift, a non-ZIP in ZipRemap, `-` in
  HashId) are replaced with `[REDACTED]` and counted as warnings, never passed through.
- **Perturb:** zero stays the exact source string, and a non-zero value never rounds to zero.
  Counts never drop below 1 when the source is ≥ 1, and zero padding (`007`) is kept.
- **DateShift:** only the year/month/day characters are rewritten (same widths, same
  month-name case). Time, fractions and offsets stay byte-for-byte. The profiled formats are
  tried first, so a day-first column stays day-first.
- **ZipRemap:** the fallback keeps the first 3 digits and derives the rest, with a "may not
  geocode" warning. A real list plugs in through `IZipReference`.
- **Malformed rows:** skip mode leaves the row out of the output and lists its record number.
  Fail mode (the default) stops at the first one.
- **Preview and run share the session key**, so the preview matches the downloaded file. The
  key is zeroed when the session is disposed.
- **Verification:** mapping columns count distinct values exactly with 64-bit hashes,
  independently of the mapping dictionaries. Other columns use bounded estimates.
- `PerEntity` modes are rejected by plan validation until entity groups (step 7).

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

Decisions made while building step 6 (`src/CsvMasker.Web/Recipes`):
- **Signature:** SHA-256 hex of the ordered column names joined with U+001F. Each name is
  normalized first: trimmed, internal whitespace collapsed, lower-cased (invariant).
  Duplicate and blank names are kept.
- **Matching:**
  - An exact signature wins; among several, the most recently updated.
  - Otherwise, match column names (normalized; the nth duplicate matches the nth). The
    recipe with the best matched/total score is **auto-applied if ≥ 50%**; ties go to the
    most recently updated.
  - Unmatched columns start blank (`— choose —`), highlighted, with the profiler's
    suggestion as a hint. The review POST rejects blanks.
  - The review page has a "Pre-fill from" selector (any recipe, or profiler suggestions).
    Applying one sends the job back to Uploaded, so it needs re-confirming.
- **Sharing:** team-wide. Every allowed user can apply any recipe; only the owner (matched
  case-insensitively) can update or delete it. Other members get a 403 "You can't do that"
  page, not the access-denied one.
- **Saving:** from a completed job's status page, either "Save new recipe" (name ≤ 100
  characters) or "Update '<name>'" when the job was pre-filled from your own recipe. Both
  store the confirmed plan plus `SkipMalformed`.
- **Storage:**
  - One file per recipe, `{guid}.json`, in schema version 1, in `Storage:RecipeFolder`.
  - Writes are atomic (temp file + rename). Corrupt files, or files from a newer schema, are
    skipped and logged by file name.
  - DateShift formats are not stored; they come from each file's profile.
  - `mappingDomain` and `entityGroups` are reserved for step 7.
- **`Storage:RecipeFolder`** is required outside Development; startup fails without it. In
  Development, empty means `%LOCALAPPDATA%\CsvMasker\recipes`. It must be outside wwwroot
  and separate from `Storage:TempFolder`, because the sweeper deletes old files there.
- Logs record recipe ids and users, never recipe names or contents.

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

Added in step 4: `Limits:MaxConcurrentJobs` (default 2), a global cap on masking runs at once;
extra runs queue. `Storage:TempFolder` empty means `%TEMP%\CsvMasker` (dev only); startup
fails if it resolves inside `wwwroot`. A test asserts `web.config` and
`Limits:MaxUploadBytes` agree.

## Web workflow decisions (step 4, `src/CsvMasker.Web`)
- **Uploads stream** straight into `Storage:TempFolder` via `MultipartReader`. There's no
  `IFormFile`, which would buffer into ASP.NET's own temp dir. The upload page posts with a
  small vanilla-JS XHR that puts the antiforgery token in the `RequestVerificationToken`
  header, so the body is never read as a form, and shows upload progress. JavaScript is
  required to upload.
- **Jobs live in memory** (`JobRegistry`). There's one open job per user, from upload until
  download, failure, cancel or discard; another upload is rejected with "continue or
  discard". Another user's job id returns 404.
- **Review is fail-closed:** strategies are pre-filled from the profiler, but nothing runs until
  "I have reviewed every column" is ticked and the plan validates. The per-job "Skip malformed
  rows" option is off by default.
- **Run** happens on a background worker (`JobRunner`). The status page auto-refreshes with a
  meta refresh (no JS), with Cancel. The upload is deleted as soon as the run ends, whatever
  the outcome.
- **Download** is a POST, so link prefetch can't consume it. It streams with
  `FileOptions.DeleteOnClose`, so the output is gone once sent; an interrupted download means
  re-running.
- **Sweeper** runs every 5 minutes. It removes temp files and idle jobs older than
  `SweepAgeMinutes`, skipping files that belong to a running job.
- **Logging:** only job ids, sizes, counts, timings and error kinds are logged
  (`SafeErrors.ForLog`). The framework's exception-handler logging is switched off in
  appsettings, because an exception message could echo data. The error page logs a
  sanitized line instead. Users only see value-free messages (`SafeErrors.ForUser`).
## Access decisions (step 5)
- **Authentication:** `Microsoft.AspNetCore.Authentication.Negotiate`. Under IIS it defers to IIS
  Windows auth; on Kestrel (`dotnet run`) it does Kerberos/NTLM itself.
- **Authorization:**
  - The `AllowedGroup` policy (authenticated + `User.IsInRole(group)`, case-insensitive) is the
    **fallback policy**, so every page and handler requires it without attributes.
  - Exceptions: `StatusCode` and `Error` pages, plus the bundled CSS/JS, which allow anonymous
    access so the access-denied page renders styled.
- **`Authorization:AllowedGroup`** is set per server in `appsettings.Production.json`; no group
  name is committed. Outside Development the app **refuses to start** when it's empty. In
  Development, empty means any signed-in Windows user.
- **403:** a friendly page that names the user but not the group, and logs
  `Access denied for {User}`. 404s for unknown or expired jobs also get a friendly page.
- **401s are never re-executed as status pages.** They're steps in the NTLM/Kerberos handshake,
  and re-running authentication mid-handshake throws. A tiny middleware disables status pages
  for 401.
- **Audit:** job events (upload, review, queue, cancel, completed/failed, download, discard)
  log the Windows user name, still with no file names or cell values. `UserKey` no longer has a
  fallback identity.
- **Tests** replace Negotiate with a header-driven test scheme, since Negotiate needs Kestrel or
  IIS. Real Windows sign-in was checked on Kestrel with `curl --negotiate`.

## Open items (confirm with Chris)

- ~~AD group name for authorization~~: set per server in `appsettings.Production.json` (step 5).
- ~~Maximum upload size~~: 200 MB (209,715,200 bytes), decided in step 4.
- (Fallback shipped in step 3; a list plugs in via `IZipReference`.)
  Source for the bundled US ZIP reference list (public dataset; must be licensed for
  internal use) and whether non-US postal codes need handling.
- IIS site/application path (assumed `/csvmasker` under an existing HTTPS site).

# 6. File import parser contracts and interpretations

- Status: Accepted
- Date: 2026-09-24
- Milestone: M3

## Context

F-TXN-2 asks for CSV (with layout auto-detection and a remembered column mapping), OFX/QFX in
SGML and XML, and QIF, with ambiguous date formats prompting the user. PRD 7.1 pins CsvHelper
and says OFX/QIF are hand-written. The M0 `IImportService` stub already declares
`IncomingTransaction` and a `DedupOutcome` enum. Several points are not spelled out.

## Decision

- **Separate parse and pipeline types.** Parsers return `ParsedTransaction` records
  (`Keel.Application.Import`), which implement the domain's `IImportRecord` so
  `DuplicateMatcher` can classify them directly. The existing `IncomingTransaction` and
  `DedupOutcome` in `IImportService.cs` are left untouched; the pipeline task maps
  `DedupDecision` (domain, named after PRD 6.5 steps) onto them or replaces them.
- **Parser signature.** `IFileImportParser.ParseAsync(Stream, ImportOptions, CancellationToken ct = default)`:
  the token is an addition with a default, so two-argument calls still compile. Parsers are
  pure; row problems are `ImportWarning`s with a code, a one-based line and no payee or amount
  text, so the UI can localize them and logs stay clean.
- **Ambiguous dates.** When every value fits both month-first and day-first and at least one
  value reads differently, the CSV and QIF parsers still return rows, read month-first, and
  report `AmbiguousDateFormat` with both candidates (`DetectedCsvLayout.IsDateFormatAmbiguous`).
  The UI asks and re-parses with `ImportOptions.PreferredDateOrder` or an explicit mapping.
  If all values read the same either way, nothing is reported.
- **CSV mapping by column index.** `CsvColumnMapping` stores zero-based indexes, the date format
  name, delimiter, decimal separator, sign convention and `SkipRows` (counted in non-blank
  records). Parsing with an explicit mapping skips all detection; a test re-parses every CSV
  fixture with its detected mapping and requires identical output.
- **Sign heuristics.** With a signed amount column, a file where positives outnumber negatives
  and every "PAYMENT"/"THANK YOU"/"AUTOPAY" row is negative is read as outflow-positive
  (credit-card exports such as Discover), with a `SignConventionGuessed` warning; an all-positive
  file keeps inflow-positive and warns. Amount-plus-type is chosen only when the amount column
  has no negatives and at least 80% of type values are recognized.
- **OFX dates** are the civil date in the first eight digits of `DTPOSTED` (falling back to
  `DTUSER`); a time and a time-zone suffix such as `[-5:EST]` are accepted and not converted,
  because banks state the posting date in their own zone and conversion can move it by a day.
- **OFX leniency.** Aggregates are recognized from a list of banking and credit-card aggregate
  names; unknown valueless tags are empty leaves. A repeated aggregate open tag closes the
  previous one (missing `</STMTTRN>`), and a missing `</OFX>` alone is not reported. Both
  `STMTTRN` and `CCSTMTTRN` are read (the OFX spec uses `STMTTRN` inside `CCSTMTRS`).
  Undeclared or ASCII-declared files that are valid UTF-8 are read as UTF-8, otherwise as the
  declared single-byte code page (Windows-1252 by default).
- **QIF** amounts keep the file's sign for every account type, `L[Account]` is carried as
  `Category` plus an `Extras["TransferAccount"]`, and investment sections are skipped with an
  `UnsupportedSection` warning (investments are balance-only in v1, PRD D7).
- **CsCheck 4.9.1** is added for the dedup property test; PRD 13 names it as an option.

## Consequences

- The import pipeline and mapping dialog (next M3 task) must handle `AmbiguousDateFormat` by
  asking, and persist `CsvColumnMapping` per account (for example in the `Setting` table).
- Fixture-driven tests pin the exact output of 13 files; a deliberate behaviour change is made
  by running `KEEL_UPDATE_FIXTURES=1` and reviewing the JSON diff.

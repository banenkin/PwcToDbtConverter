# CLAUDE.md

## Project

Converter from Informatica PowerCenter mapping XML to dbt models (take-home exercise, see `docs/assignment.md`). The input is a PowerCenter XML export. The output is a dbt project whose models reproduce the mapping logic. The jaffle shop reference (`jaffle_shop_duckdb`) is only used to check results. It must never be hard-coded.

The design is in `specs/architecture.md`. Read it before changing parsing, models or SQL generation. If the design changes, update the spec in the same change.

## Layout

```
PwcToDbtConverter.sln                solution (source + tests)
source/                              console app, net8.0, Nullable + ImplicitUsings enabled
  Program.cs                         CLI entry point (still a Hello World stub)
tests/PwcToDbtConverterTests/        xUnit test project (still the template UnitTest1)
sample-data/FLOWLINE_DEMO_JAFFLESHOP.xml   the one PowerCenter export to convert
specs/architecture.md                design, models, edge cases, build order
docs/                                assignment and prompt logs
```

## Commands

Requires the .NET 8 SDK (8.0.416 installed locally).

```bash
dotnet build PwcToDbtConverter.sln
dotnet test PwcToDbtConverter.sln
dotnet test tests/PwcToDbtConverterTests        # tests only
dotnet format PwcToDbtConverter.sln --verify-no-changes
dotnet run --project source -- sample-data/FLOWLINE_DEMO_JAFFLESHOP.xml <output-dir>   # planned CLI, not yet implemented
```

Setup gap: `tests/PwcToDbtConverterTests` does not reference `source/` yet. Add it before writing tests against the converter:

```bash
dotnet add tests/PwcToDbtConverterTests reference source/PwcToDbtConverter.csproj
```

## Architecture rules

1. **Layers only flow one way:** `Parsing` → `Model` → `Graph` → `Translation` → `Sql` → `Dbt`. A layer never reaches back into an earlier one. `Model` depends on nothing.
2. **XML is touched only in `Parsing`.** No `XElement`, `XAttribute` or other `System.Xml` type leaves that namespace. Everything downstream works with the records in `Model`.
3. **Models are immutable records.** No setters, no public mutable collections.
4. **Graph nodes are keyed by instance name, never by transformation name.** The same table can appear several times in one mapping, and instances can be aliased (`ORDER_FINANCIALS1`).
5. **Expressions are translated only by `Translation`.** No regex replacement of Informatica syntax. Use the tokenizer and recursive-descent parser.
6. **Never drop logic silently.** Anything that cannot be converted produces a `Diagnostic` and a `-- UNSUPPORTED` comment in the SQL. Parsing never throws for unknown or incomplete content.
7. **Deterministic output.** Models, columns and files are written in sorted order with stable whitespace, so diffs stay readable and tests can compare text.
8. **No jaffle shop output in code or tests.** Expected results come from the XML and the spec. Comparison against jaffle shop is a manual check, not a unit test fixture.
9. **Dialect is DuckDB for now.** Keep dialect-specific SQL behind one place (`Translation` / `Sql`), so it can be swapped later.
10. **Decisions are recorded in the spec.** If you take a decision the spec does not cover, add it to section 6 of `specs/architecture.md`.

## Testing

- xUnit. Test files mirror the source folders (`Parsing/`, `Graph/`, `Translation/`, `Sql/`).
- Tests that read the sample XML use a fixture that resolves the path from the repo root. Never use absolute paths.
- Parser tests assert the inventory counts in `specs/architecture.md` section 1 (3 mappings, 16 targets, 12 sources, 98 transformations, 142 instances, 1066 connectors). A count change means a parser change, so it must be deliberate.
- Each edge case in section 5 of the spec gets a test with a small inline XML snippet, not the full sample.

## Working agreements

- Write no C# until the spec for the affected layer is current.
- Keep the change small and in the layer it belongs to. Do not mix parsing changes with SQL changes in one edit.
- Do not add packages without saying why. The one expected addition is `System.Text.Encoding.CodePages`, for the Windows-1252 declaration.
- Do not commit `bin/` or `obj/`. They are already present in the folder and should be ignored.
- Comments explain *why*, not *what*. Match the density of the surrounding code.

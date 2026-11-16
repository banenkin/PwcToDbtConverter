# Informatica PowerCenter to dbt (.NET 8 & DuckDB)

An end-to-end automated migration tool built in **C# (.NET 8)** that parses Informatica PowerCenter XML exports, resolves dependency graphs, translates expressions, and generates a fully runnable **dbt** project targeted at **DuckDB**.

---

## 🚀 How to Run It

### Prerequisites
* .NET 8 SDK installed.
* Python 3.x (for running dbt with DuckDB).

### 1. Build and Run the C# Converter CLI
To parse an Informatica XML export and generate the dbt project structure:
```bash
dotnet build
dotnet run -- <path-to-export.xml> <output_dbt_directory>
```
*Example:*
```bash
dotnet run -- sample-data/FLOWLINE_DEMO_JAFFLESHOP.xml output_dbt
```

### 2. Execute dbt Build against DuckDB
Navigate to the generated output directory, set up a virtual environment, install `dbt-duckdb`, and run the project:
```bash
cd output_dbt

# Set up Python virtual environment and install dbt-duckdb
python -m venv .venv
source .venv/bin/activate  # On Windows: .venv\Scripts\activate
pip install dbt-duckdb

# Load synthetic seed data matching the XML source definitions
dbt seed --profiles-dir .

# Run the full dbt build pipeline
dbt build --profiles-dir .
```

---

## 🔍 Assumptions & What Was Chosen Not to Handle

### Assumptions
* **XML Structure:** The input XML conforms to standard Informatica PowerCenter repository export schemas containing `<SOURCE>`, `<TARGET>`, and `<MAPPING>` definitions.
* **Primary Keys:** Primary key metadata is declared via `KEYTYPE="PRIMARY KEY"` on target fields when unique/not-null tests are required.
* **Environment:** DuckDB is used as the target analytical engine via `dbt-duckdb`.

### What Was Chosen NOT to Handle (Defensive Design)
Rather than failing silently or generating invalid SQL, complex or stateful legacy transformation types are intentionally kept out of automatic rewriting:
* **Routers & Unions (`RTR_*`, `UN_*`):** Complex branching and merging logic are flagged with `-- TODO [UNSUPPORTED]` comments while preserving column projections.
* **Previous-Row / Stateful Variables (`v_PREV_` / Sequence variables):** Row-order dependent variables requiring iterative state maintenance are flagged rather than guessed via raw window functions without explicit sort keys.

---

## ⚠️ Where the Converter is Most Likely to Produce Wrong Output

1. **Complex Custom Expression Syntax:** Non-standard Informatica expression functions or nested custom lookups that do not map cleanly to standard SQL scalar functions may require manual post-processing.
2. **Implicit Row Ordering:** Mappings relying heavily on implicit source-file order (without explicit Sorter keys) will produce unsupported warnings where window functions cannot safely be inferred.

---

## 🤖 Where the Coding Agent Got Something Wrong & How It Was Caught

* **Issue 1: Missing Cross-Mapping Lineage (`source()` vs `ref()`)**
  * *What happened:* The agent initially treated every upstream table as an external source via `{{ source() }}`, even when that exact table was produced as a target model by an earlier mapping in the same XML export.
  * *How it was caught:* Running `dbt build` threw a `Catalog Error` stating that schema `STG` did not exist. By inspecting the compiled SQL and sources configuration, we caught the lack of a project DAG.
  * *Fix:* Updated the C# generator to verify if a source matches an internal target model name, automatically emitting `{{ ref() }}` and excluding it from `sources.yml`.

* **Issue 2: Public Jaffle Shop Seed Schema Mismatch**
  * *What happened:* Public sample CSVs used old column naming conventions (`first_name` instead of `NAME`, `user_id` instead of `CUSTOMER`), causing compilation errors against the strict XML source columns.
  * *How it was caught:* DuckDB binder errors flagged missing columns during staging model compilation.
  * *Fix:* Recognized that the C# generator's SQL was correct according to the XML; replaced the legacy CSVs with clean synthetic seed files matching the native XML `<SOURCE>` schemas.

---

## 🛠️ Process Outline Followed with the Coding Agent

1. **Step 1 — XML Parsing & Graph Construction:** Ingested PowerCenter XML and built a directed acyclic graph (DAG) using topological sorting.
2. **Step 2 — Expression Translator:** Translated Informatica expression language syntax into standard SQL expressions.
3. **Step 3 — SQL Generator:** Implemented modular CTE generation (`WITH step AS (...)`) supporting Source Qualifiers, Filters, Joiners, Aggregators, and Lookups, while flagging unsupported constructs.
4. **Step 4 — dbt Project Writer & CLI:** Wired up project file generation (`dbt_project.yml`, `sources.yml`, modular directories) and built the C# CLI.
5. **Step 5 — DuckDB Validation & Debugging:** Set up `dbt-duckdb`, uncovered and resolved the cross-mapping `ref()` issue, and validated end-to-end execution.

---

## 📝 Prompt History (Agent Conversation Log)

Below is the chronological sequence of prompts provided to the coding agent during development:

1. **Initial Prompt:**
   > *"Let's build a robust C# (.NET 8) Informatica-to-dbt converter for DuckDB, ensuring a clean lineage (DAG) between staging and marts models, and validating the output through end-to-end `dbt build` execution."*
2. **Step 4 (SQL Generator):**
   > *"Let's implement Step 4: the dbt SQL Generator. Using our dependency graph and ExpressionTranslator, build a `SqlGenerator` class that produces valid DuckDB dbt SQL model strings with CTEs, proper source/ref handling, and `-- TODO [UNSUPPORTED]` comments for unsupported transforms."*
3. **Step 5 (Project Writer & CLI):**
   > *"Let's implement Step 5: the dbt Project Writer and CLI runner. Build a `DbtProjectWriter` that outputs `dbt_project.yml`, `sources.yml`, modular staging/marts models, and schema tests, wired up via `Program.cs`."*
4. **Cross-Mapping Bug Investigation:**
   > *"I noticed that data/tables are being passed between different mappings in the XML (a target produced by one mapping is used as a source in another). I'd like the converter to handle this properly instead of failing on missing external sources. If it makes sense architecturally, let's implement it; if not, tell me why. Here is the error log from `dbt build`: [Error Log]"*
5. **Seed Alignment:**
   > *"Agreed—do NOT touch the C# generator or `schema.yml`, the SQL is right. Can you generate small synthetic CSV files in `output_dbt/seeds/` matching the exact `<SOURCE>` columns from the XML, run `dbt seed` and `dbt build`, and verify the pipeline?"*
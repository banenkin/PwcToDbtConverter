# Informatica PowerCenter to dbt (.NET 8 & DuckDB)

An end-to-end automated migration tool built in **C# (.NET 8)** that parses Informatica PowerCenter XML exports, resolves dependency graphs, translates expressions, and generates a fully runnable **dbt** project targeted at **DuckDB**.

---

## How to Run

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

## Assumptions & What Was Chosen Not to Handle

### Assumptions

* **XML Structure:** The input XML conforms to standard Informatica PowerCenter repository export schemas containing `<SOURCE>`, `<TARGET>`, and `<MAPPING>` definitions.
* **Environment:** DuckDB is used as the target analytical engine via `dbt-duckdb`.

### What Was Chosen NOT to Handle (Defensive Design)

Rather than failing silently or generating invalid SQL, complex or stateful legacy transformation types are intentionally kept out of automatic rewriting:

* **Routers & Unions (**`RTR_`***,** `UN_`***):** Complex branching and merging logic are flagged with `-- TODO [UNSUPPORTED]` comments while preserving column projections.
* **Previous-Row / Stateful Variables (**`v_PREV_` **/ Sequence variables):** Row-order dependent variables requiring iterative state maintenance are flagged rather than guessed via raw window functions without explicit sort keys.

---

## Where the Converter is Most Likely to Produce Wrong Output

1. **Complex Custom Expression Syntax:** Non-standard Informatica expression functions or nested custom lookups that do not map cleanly to standard SQL scalar functions may require manual post-processing.
2. **Implicit Row Ordering:** Mappings relying heavily on implicit source-file order (without explicit Sorter keys) will produce unsupported warnings where window functions cannot safely be inferred.
3. Many other edge cases, out of the timeframe. Four hours are completely far from enough

---

## Where the Coding Agent Got Something Wrong & How It Was Caught

* **Issue 1: Missing Cross-Mapping Lineage (**`source()` **vs** `ref()`**)**
  * *What happened:* The agent initially treated every upstream table as an external source via `{{ source() }}`, even when that exact table was produced as a target model by an earlier mapping in the same XML export.
  * *How it was caught:* Running `dbt build` threw a `Catalog Error` stating that schema `STG` did not exist. By inspecting the compiled SQL and sources configuration, we caught the lack of a project DAG.
* **Issue 2: Public Jaffle Shop Seed Schema Mismatch**
  * *What happened:* Public sample CSVs used old column naming conventions (`first_name` instead of `NAME`, `user_id` instead of `CUSTOMER`), causing compilation errors against the strict XML source columns.
  * *How it was caught:* DuckDB binder errors flagged missing columns during staging model compilation.
* **Issues 3,4,5, etc.** - Out of the timeframe

---

## Where a coding agent got something wrong, and how I caught it.

Coding agent got it brilliant - from explanations to implementation. One mistake worth noticing is human mistake - on Step 2 i gave the architecture of the graph to Claude and forgot to instruct Cursor for the implementation. Anyway he got it later.

Coding agent gave me a lot of information that was hard to understand, especially in the given timeframe. I left many prompts that i didn't understand completely. Checked the code, tests and the main idea. Also the basic Design concepts.

---

## A short outline of the process I followed with the agent to build the solution.

FIrst of all - understanding the task.

Looking through all the documentation sequentially. Getting to know the functionalities of Informatica and dbt. Two tools completely unknown to me.  Getting to know the requirements and possible architecture took apoximately half of my time.

Basically i used Claude and Gemini as a brain(analytics), and Cursor as hands(coding). Claude gives larger context window, while Cursor is optimized in writing code. In my expirience Cursor is handling the tasks more accurately, while Claude is analysing better and is doing everything else in corporate environment - mails, Jira tasks etc.

First it looked like an old school task, easy to implement for a certain amount of time. Then realizing the complexity of translation and separating it to smaller tasks.

Used agents for every line of code, for architecture and specification of prompts. Used AI for answering questions related to understanding the tools and the task.

In the begining i thought it is straight-forward solution of processing text data. At the end saw that it is probably long task for a real system, that deserves much longer time.
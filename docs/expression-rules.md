# Expression rules: Informatica to DuckDB

Source: every `EXPRESSION` attribute on a `TRANSFORMFIELD` in `sample-data/FLOWLINE_DEMO_JAFFLESHOP.xml`.

- 384 expression attributes in the file, none empty.
- 211 unique expressions after whitespace normalization.
- 3 contain CRLF line breaks and indentation, which the translator must normalize.

The golden strings in section 4 are the expected output of the translator, one per case. They were checked by running each one through DuckDB 1.4.5: all 36 parse and bind against a table with matching columns, and the value checks in section 6 pass.

Scope: this file covers `TRANSFORMFIELD` expressions. Filter conditions, router group expressions and join conditions live in other attributes. They are listed separately in section 5, because the translator needs them too.

---

## 1. Functions and operators used in this file

Counts are occurrences across all 384 expression attributes.

| Informatica | Uses | DuckDB equivalent | Rule |
|---|---|---|---|
| `IIF(c, a, b)` | 65 | `case when c then a else b end` | Nested IIFs become nested `case` in the `else` branch. Do not flatten them. |
| `ISNULL(x)` | 33 | `x is null` | Used only as the condition of an `IIF` in this file (verified: all 33). |
| `SUM(x)` | 29 | `sum(x)` | Valid only inside an Aggregator. |
| `MAX(x)` | 7 | `max(x)` | Aggregator only. |
| `MIN(x)` | 2 | `min(x)` | Aggregator only. |
| `COUNT(*)` | 1 | `count(*)` | Aggregator only. |
| `COUNT(x)` | 5 | `count(x)` | Counts non-null values, same as DuckDB. |
| `TRUNC(x)` on a date | 5 | `cast(x as date)` | No format argument in this file, so it truncates to the day. |
| `DATE_DIFF(a, b, 'DD')` | 2 | `date_diff('day', b, a)` | **Argument order flips.** Informatica returns `a - b`. DuckDB takes the start first. |
| `TO_CHAR(x, 'DD-MM-YY')` | 1 | `strftime(x, '%d-%m-%y')` | Format tokens translated one by one. Only `DD`, `MM`, `YY` appear in this file. |
| `TO_DATE(s, 'DD-MM-YY')` | 1 | `cast(strptime(s, '%d-%m-%y') as date)` | Returns a date, so the outer `cast` keeps the type. |
| `UPPER(x)` | 2 | `upper(x)` | |
| `LTRIM(x)` | 2 | `ltrim(x)` | Keep the nesting. Do not replace `LTRIM(RTRIM(x))` with `trim(x)`. |
| `RTRIM(x)` | 2 | `rtrim(x)` | |
| `=` | 25 | `=` | |
| `<>`, `!=` | 0 | `<>` | Not used in this file. |
| `>`, `>=`, `<`, `<=` | 2, 9, 0, 2 | same | |
| `AND`, `OR` | 5, 1 | `and`, `or` | Lowercase keywords. Add parentheses in the translator if precedence is unclear. |
| `+`, `-`, `*`, `/` | 5, 2, 5, 18 | same | Spaces around binary operators. See section 6 on division. |
| `NOT` | 0 in expressions | `not` | Appears in Filter conditions, see section 5. |
| `$$RUN_DATE` | 2 | `{{ var('run_date', '2025-12-31') }}` | Default comes from the mapping variable: `12/31/2025 00:00:00`. |
| Local variable `v_*` | many | column reference | Resolved through the scope of the Expression instance. |
| Port name | many | the name as written | Pass-through when the expression is just a port name. |
| String literal `'...'` | many | same | Unchanged. |
| Numeric literal | many | same | Unchanged. |

---

## 2. Canonical output format

Golden strings follow these rules exactly. Another formatting would fail the test even if it meant the same thing.

1. Keywords and function names are lowercase: `case`, `when`, `then`, `else`, `end`, `and`, `or`, `is`, `null`, `not`, `cast`, `as`, `date`, `upper`, `ltrim`, `rtrim`, `sum`, `max`, `min`, `count`, `date_diff`, `strptime`, `strftime`.
2. Identifiers are copied exactly as written in the XML. Case is kept, so `LIFETIME_SPEND` stays uppercase and `v_PREV_LOCATION` keeps its prefix.
3. String literals and numeric literals are copied exactly.
4. One space around each binary operator (`=`, `>=`, `+`, `/`, and so on). No space inside function parentheses. No space after `(` or before `)`.
5. Nested `IIF` becomes nested `case` expressions, with each inner `case` inside the `else` branch. Do not flatten into `when ... when`.
6. Whitespace, including CRLF and indentation in the source, is collapsed before translation. Golden IIF-14 is the same as IIF-06 for this reason.
7. Golden strings are the translation before any cast to the port's data type. A later step adds casts, so these strings stay simple.

---

## 3. Patterns that matter for the translator

- **IIF with ISNULL condition**: the most common pattern in the file (33 of 65 IIFs). `IIF(ISNULL(x), d, x)` becomes `case when x is null then d else x end`.
- **Nested IIF as a lookup**: the `CASE`-like chains for `CUSTOMER_TYPE`, `RFM` segments, `TAX_RATE` regions and recency scores. Each level becomes a `case` inside the `else` of the previous one.
- **Aggregates over expressions**: `SUM(IIF(...))` appears in an Aggregator. The translator emits an aggregate over a `case`, and the group-by key comes from `GROUPBY` ports.
- **Local variables**: `v_*` ports are evaluated in document order and may depend on each other. In SQL they become columns of an intermediate CTE, not substitutions.
- **Date handling**: `DATE_DIFF` with `TRUNC` and the `$$RUN_DATE` parameter in the recency calculation.

---

## 4. Golden test cases

Each row is one test. `Informatica` is the expression after whitespace normalization, except where the case is about whitespace. `DuckDB` is the exact expected output.

| ID | Informatica | DuckDB (golden) | Notes |
|---|---|---|---|
| IIF-01 | `IIF(ISNULL(LIFETIME_SPEND), 0, LIFETIME_SPEND)` | `case when LIFETIME_SPEND is null then 0 else LIFETIME_SPEND end` | Numeric default |
| IIF-02 | `IIF(ISNULL(CUSTOMER_NAME_IN), 'Unknown', CUSTOMER_NAME_IN)` | `case when CUSTOMER_NAME_IN is null then 'Unknown' else CUSTOMER_NAME_IN end` | String default |
| IIF-03 | `IIF(ORDERS_COUNT = 0, 0, REVENUE_TOTAL / ORDERS_COUNT)` | `case when ORDERS_COUNT = 0 then 0 else REVENUE_TOTAL / ORDERS_COUNT end` | IIF guarding a division |
| IIF-04 | `IIF(COUNT_LIFETIME_ORDERS>=5,3,IIF(COUNT_LIFETIME_ORDERS>=2,2,1))` | `case when COUNT_LIFETIME_ORDERS >= 5 then 3 else case when COUNT_LIFETIME_ORDERS >= 2 then 2 else 1 end end` | Nested, not flattened |
| IIF-05 | `IIF(COUNT_LIFETIME_ORDERS=0,'PROSPECT',IIF(RFM_SCORE=333,'CHAMPION',IIF(R_SCORE=3 AND F_SCORE>=2 AND M_SCORE>=2,'LOYAL',IIF(R_SCORE=1 AND F_SCORE>=2,'AT_RISK','STANDARD'))))` | `case when COUNT_LIFETIME_ORDERS = 0 then 'PROSPECT' else case when RFM_SCORE = 333 then 'CHAMPION' else case when R_SCORE = 3 and F_SCORE >= 2 and M_SCORE >= 2 then 'LOYAL' else case when R_SCORE = 1 and F_SCORE >= 2 then 'AT_RISK' else 'STANDARD' end end end end` | Four levels, `AND` inside a condition |
| IIF-06 | `IIF(TAX_RATE >= 0.15,'EMEA', IIF(TAX_RATE >= 0.09,'APAC','AMER'))` | `case when TAX_RATE >= 0.15 then 'EMEA' else case when TAX_RATE >= 0.09 then 'APAC' else 'AMER' end end` | Decimal literals, string results |
| IIF-07 | `IIF(RECENCY_DAYS<=30,3,IIF(RECENCY_DAYS<=90,2,1))` | `case when RECENCY_DAYS <= 30 then 3 else case when RECENCY_DAYS <= 90 then 2 else 1 end end` | `<=` with no spaces in the source |
| IIF-08 | `IIF(IIF(ISNULL(LIFETIME_ORDERS), 0, LIFETIME_ORDERS) > 1, 'returning', 'new')` | `case when case when LIFETIME_ORDERS is null then 0 else LIFETIME_ORDERS end > 1 then 'returning' else 'new' end` | IIF inside the condition of another IIF |
| IIF-09 | `IIF(PERISHABLE = 'Y' OR PERISHABLE = '1', 1, 0)` | `case when PERISHABLE = 'Y' or PERISHABLE = '1' then 1 else 0 end` | `OR`. `'1'` stays a string. |
| IIF-10 | `IIF(v_PREV_CUSTOMER_ID = CUSTOMER_ID, v_ORDER_SEQUENCE + 1, 1)` | `case when v_PREV_CUSTOMER_ID = CUSTOMER_ID then v_ORDER_SEQUENCE + 1 else 1 end` | Local variable as operand |
| IIF-11 | `IIF(v_PREV_LOCATION = LOCATION_ID AND v_PREV_DAY = ORDERED_AT_DAY AND v_PREV_CUSTOMER = CUSTOMER_ID, 0, 1)` | `case when v_PREV_LOCATION = LOCATION_ID and v_PREV_DAY = ORDERED_AT_DAY and v_PREV_CUSTOMER = CUSTOMER_ID then 0 else 1 end` | Three-term `AND` |
| IIF-12 | `IIF( UPPER(LTRIM(RTRIM(TYPE))) = 'BEVERAGE', 1, 0 )` | `case when upper(ltrim(rtrim(TYPE))) = 'BEVERAGE' then 1 else 0 end` | Source has spaces inside the parentheses; golden does not |
| IIF-13 | `IIF(v_ITEM_LINES_CL = 0, 0, v_DRINK_ITEM_LINES_CL / v_ITEM_LINES_CL)` | `case when v_ITEM_LINES_CL = 0 then 0 else v_DRINK_ITEM_LINES_CL / v_ITEM_LINES_CL end` | Division guarded by a local variable |
| IIF-14 | `IIF(TAX_RATE >= 0.15,'EMEA',\r\nIIF(TAX_RATE >= 0.09,'APAC','AMER'))` | same as IIF-06 | Source contains CRLF. Normalization must make it equal to IIF-06. |
| ISNULL-01 | `IIF(ISNULL(ORDER_COST), 0, ORDER_COST)` | `case when ORDER_COST is null then 0 else ORDER_COST end` | `ISNULL` as the condition |
| DATE-01 | `IIF(ISNULL(LAST_ORDERED_AT), 9999, DATE_DIFF(TRUNC($$RUN_DATE), TRUNC(LAST_ORDERED_AT), 'DD'))` | `case when LAST_ORDERED_AT is null then 9999 else date_diff('day', cast(LAST_ORDERED_AT as date), cast({{ var('run_date', '2025-12-31') }} as date)) end` | Argument order flips. `$$RUN_DATE` becomes a dbt var with the mapping variable's default. |
| DATE-02 | `TRUNC(OPENED_AT)` | `cast(OPENED_AT as date)` | `TRUNC` with no format |
| DATE-03 | `TO_DATE(TO_CHAR(ORDERED_AT, 'DD-MM-YY'), 'DD-MM-YY')` | `cast(strptime(strftime(ORDERED_AT, '%d-%m-%y'), '%d-%m-%y') as date)` | Format tokens translated; round trip gives a date |
| AGG-01 | `SUM(ORDER_TOTAL)` | `sum(ORDER_TOTAL)` | `SUM` in an Aggregator |
| AGG-02 | `SUM(IIF(ORDER_VALUE_BAND = 'HIGH', 1, 0))` | `sum(case when ORDER_VALUE_BAND = 'HIGH' then 1 else 0 end)` | Aggregate over a `case` |
| AGG-03 | `SUM(IIF(ISNULL(SUPPLY_COST_PER_PRODUCT), 0, SUPPLY_COST_PER_PRODUCT))` | `sum(case when SUPPLY_COST_PER_PRODUCT is null then 0 else SUPPLY_COST_PER_PRODUCT end)` | Aggregate over a null-default `case` |
| AGG-04 | `MAX(IS_DRINK_ITEM)` | `max(IS_DRINK_ITEM)` | `MAX` |
| AGG-05 | `MIN(ORDERED_AT_DAY)` | `min(ORDERED_AT_DAY)` | `MIN` |
| AGG-06 | `COUNT(*)` | `count(*)` | Row count |
| AGG-07 | `COUNT(ORDER_ID)` | `count(ORDER_ID)` | Counts non-null values |
| OP-01 | `(v_R_SCORE*100)+(v_F_SCORE*10)+v_M_SCORE` | `(v_R_SCORE * 100) + (v_F_SCORE * 10) + v_M_SCORE` | Parentheses kept; spaces added |
| OP-02 | `COST/100` | `COST / 100` | Division by a literal |
| OP-03 | `PRODUCT_PRICE + (PRODUCT_PRICE * v_TAX_RATE_CL)` | `PRODUCT_PRICE + (PRODUCT_PRICE * v_TAX_RATE_CL)` | Precedence kept by the parentheses |
| OP-04 | `PRODUCT_PRICE - v_SUPPLY_COST_CL` | `PRODUCT_PRICE - v_SUPPLY_COST_CL` | Subtraction |
| OP-05 | `ORDER_TOTAL_CENTS/100` | `ORDER_TOTAL_CENTS / 100` | Underscored identifier; the `/` is the operator |
| PASS-01 | `CUSTOMER_ID` | `CUSTOMER_ID` | Pass-through: expression is a bare port name |
| PASS-02 | `'HIGH'` | `'HIGH'` | String literal |
| CONST-01 | `'Y'` | `'Y'` | String literal |
| FILTER-01 | `NOT ISNULL(LOCATION_ID)` | `LOCATION_ID is not null` | Filter Condition attribute, not a TRANSFORMFIELD |
| ROUTER-01 | `REGION = 'APAC'` | `REGION = 'APAC'` | Router group expression, not a TRANSFORMFIELD |
| ROUTER-02 | `ORDER_TOTAL >= 50` | `ORDER_TOTAL >= 50` | Router group expression, not a TRANSFORMFIELD |

---

## 5. Expressions outside TRANSFORMFIELD

The translator needs these as well. They are not in the scan above, but they use the same rules.

| Where | Informatica | DuckDB (golden) |
|---|---|---|
| Filter Condition (`FIL_LOCS`) | `NOT ISNULL(LOCATION_ID)` | `LOCATION_ID is not null` |
| Router group `G_APAC` | `REGION = 'APAC'` | `REGION = 'APAC'` |
| Router group `G_AMER` / `G_EMEA` | `REGION = 'AMER'` / `REGION = 'EMEA'` | same pattern |
| Router group `HIGH_VALUE` | `ORDER_TOTAL >= 50` | `ORDER_TOTAL >= 50` |
| Router group `STANDARD` | `ORDER_TOTAL < 50` | `ORDER_TOTAL < 50` |
| Joiner Join Condition | `SKU1 = SKU`, `ORDER_ID = ORDER_ID1`, and similar | `SKU1 = SKU`, and the same for the others. Used as the `on` clause. |

Router groups become one CTE per group, filtered with `where <group expression>`. The default group is the rows matching none of the group expressions.

---

## 6. Known differences and risks

1. **Division.** DuckDB returns a fractional result for integer `/` (`7 / 2` gives `3.5`). Informatica's result type depends on the port's data type. This file divides mostly decimal values, so the risk is low here. The translator should cast the result to the target port's type in a later step. Verify against the Informatica type rules before trusting integer ports.
2. **Date difference argument order.** `DATE_DIFF(a, b, 'DD')` is `a - b` in Informatica. DuckDB's `date_diff('day', start, end)` is `end - start`, so `DATE_DIFF(a, b)` maps to `date_diff('day', b, a)`. Checked: with a = 2025-12-31 and b = 2025-12-01 both give 30.
3. **`$$RUN_DATE`.** Golden DATE-01 contains the dbt `var()` call, so it does not run as plain DuckDB SQL. The validation substitutes the literal `'2025-12-31'`. The default comes from `MAPPINGVARIABLE` (`12/31/2025 00:00:00`).
4. **Format tokens.** Only `DD`, `MM` and `YY` occur here. Any other Informatica token (for example `HH24`, `MI`, `YYYY`) should produce a diagnostic and an `UNSUPPORTED` comment rather than a guess.
5. **Identifiers that look like keywords.** `TYPE` appears as a column (`UPPER(LTRIM(RTRIM(TYPE)))`). It bound fine in DuckDB, but the translator should quote identifiers that collide with a reserved word, and the test set should keep this case.
6. **Local variable order.** `v_*` ports depend on document order (`v_R_SCORE` before `v_F_SCORE` before `RFM_SCORE`, for example). Keep `PORTTYPE` order and do not sort them.
7. **Aggregate context.** `SUM`, `MAX`, `MIN` and `COUNT` are valid only in an Aggregator. The translator should check the context and raise a diagnostic if an aggregate appears in an Expression.

---

## 7. Validation

- All 36 golden strings were run in DuckDB 1.4.5 with a probe table whose columns match each expression's identifiers. All parse and bind.
- Value checks: DATE-01 gives `30` for a last order on 2025-12-01 and `9999` for a null last order. DATE-03 returns `2025-03-07` for an input of `2025-03-07 10:30:00`.
- The validation script and its inputs are kept in the scratchpad and are not part of the repo. The golden strings in section 4 are the source of truth for the Cursor tests.

using PwcToDbtConverter.Model;
using PwcToDbtConverter.Translation;

namespace PwcToDbtConverterTests.Translation;

public class ExpressionTranslatorTests
{
    [Theory]
    [InlineData("IIF-01", "IIF(ISNULL(LIFETIME_SPEND), 0, LIFETIME_SPEND)", "case when LIFETIME_SPEND is null then 0 else LIFETIME_SPEND end", false)]
    [InlineData("IIF-02", "IIF(ISNULL(CUSTOMER_NAME_IN), 'Unknown', CUSTOMER_NAME_IN)", "case when CUSTOMER_NAME_IN is null then 'Unknown' else CUSTOMER_NAME_IN end", false)]
    [InlineData("IIF-03", "IIF(ORDERS_COUNT = 0, 0, REVENUE_TOTAL / ORDERS_COUNT)", "case when ORDERS_COUNT = 0 then 0 else REVENUE_TOTAL / ORDERS_COUNT end", false)]
    [InlineData("IIF-04", "IIF(COUNT_LIFETIME_ORDERS>=5,3,IIF(COUNT_LIFETIME_ORDERS>=2,2,1))", "case when COUNT_LIFETIME_ORDERS >= 5 then 3 else case when COUNT_LIFETIME_ORDERS >= 2 then 2 else 1 end end", false)]
    [InlineData("IIF-05", "IIF(COUNT_LIFETIME_ORDERS=0,'PROSPECT',IIF(RFM_SCORE=333,'CHAMPION',IIF(R_SCORE=3 AND F_SCORE>=2 AND M_SCORE>=2,'LOYAL',IIF(R_SCORE=1 AND F_SCORE>=2,'AT_RISK','STANDARD'))))", "case when COUNT_LIFETIME_ORDERS = 0 then 'PROSPECT' else case when RFM_SCORE = 333 then 'CHAMPION' else case when R_SCORE = 3 and F_SCORE >= 2 and M_SCORE >= 2 then 'LOYAL' else case when R_SCORE = 1 and F_SCORE >= 2 then 'AT_RISK' else 'STANDARD' end end end end", false)]
    [InlineData("IIF-06", "IIF(TAX_RATE >= 0.15,'EMEA', IIF(TAX_RATE >= 0.09,'APAC','AMER'))", "case when TAX_RATE >= 0.15 then 'EMEA' else case when TAX_RATE >= 0.09 then 'APAC' else 'AMER' end end", false)]
    [InlineData("IIF-07", "IIF(RECENCY_DAYS<=30,3,IIF(RECENCY_DAYS<=90,2,1))", "case when RECENCY_DAYS <= 30 then 3 else case when RECENCY_DAYS <= 90 then 2 else 1 end end", false)]
    [InlineData("IIF-08", "IIF(IIF(ISNULL(LIFETIME_ORDERS), 0, LIFETIME_ORDERS) > 1, 'returning', 'new')", "case when case when LIFETIME_ORDERS is null then 0 else LIFETIME_ORDERS end > 1 then 'returning' else 'new' end", false)]
    [InlineData("IIF-09", "IIF(PERISHABLE = 'Y' OR PERISHABLE = '1', 1, 0)", "case when PERISHABLE = 'Y' or PERISHABLE = '1' then 1 else 0 end", false)]
    [InlineData("IIF-10", "IIF(v_PREV_CUSTOMER_ID = CUSTOMER_ID, v_ORDER_SEQUENCE + 1, 1)", "case when v_PREV_CUSTOMER_ID = CUSTOMER_ID then v_ORDER_SEQUENCE + 1 else 1 end", false)]
    [InlineData("IIF-11", "IIF(v_PREV_LOCATION = LOCATION_ID AND v_PREV_DAY = ORDERED_AT_DAY AND v_PREV_CUSTOMER = CUSTOMER_ID, 0, 1)", "case when v_PREV_LOCATION = LOCATION_ID and v_PREV_DAY = ORDERED_AT_DAY and v_PREV_CUSTOMER = CUSTOMER_ID then 0 else 1 end", false)]
    [InlineData("IIF-12", "IIF( UPPER(LTRIM(RTRIM(TYPE))) = 'BEVERAGE', 1, 0 )", "case when upper(ltrim(rtrim(TYPE))) = 'BEVERAGE' then 1 else 0 end", false)]
    [InlineData("IIF-13", "IIF(v_ITEM_LINES_CL = 0, 0, v_DRINK_ITEM_LINES_CL / v_ITEM_LINES_CL)", "case when v_ITEM_LINES_CL = 0 then 0 else v_DRINK_ITEM_LINES_CL / v_ITEM_LINES_CL end", false)]
    [InlineData("IIF-14", "IIF(TAX_RATE >= 0.15,'EMEA',\r\nIIF(TAX_RATE >= 0.09,'APAC','AMER'))", "case when TAX_RATE >= 0.15 then 'EMEA' else case when TAX_RATE >= 0.09 then 'APAC' else 'AMER' end end", false)]
    [InlineData("ISNULL-01", "IIF(ISNULL(ORDER_COST), 0, ORDER_COST)", "case when ORDER_COST is null then 0 else ORDER_COST end", false)]
    [InlineData("DATE-01", "IIF(ISNULL(LAST_ORDERED_AT), 9999, DATE_DIFF(TRUNC($$RUN_DATE), TRUNC(LAST_ORDERED_AT), 'DD'))", "case when LAST_ORDERED_AT is null then 9999 else date_diff('day', cast(LAST_ORDERED_AT as date), cast({{ var('run_date', '2025-12-31') }} as date)) end", false)]
    [InlineData("DATE-02", "TRUNC(OPENED_AT)", "cast(OPENED_AT as date)", false)]
    [InlineData("DATE-03", "TO_DATE(TO_CHAR(ORDERED_AT, 'DD-MM-YY'), 'DD-MM-YY')", "cast(strptime(strftime(ORDERED_AT, '%d-%m-%y'), '%d-%m-%y') as date)", false)]
    [InlineData("AGG-01", "SUM(ORDER_TOTAL)", "sum(ORDER_TOTAL)", true)]
    [InlineData("AGG-02", "SUM(IIF(ORDER_VALUE_BAND = 'HIGH', 1, 0))", "sum(case when ORDER_VALUE_BAND = 'HIGH' then 1 else 0 end)", true)]
    [InlineData("AGG-03", "SUM(IIF(ISNULL(SUPPLY_COST_PER_PRODUCT), 0, SUPPLY_COST_PER_PRODUCT))", "sum(case when SUPPLY_COST_PER_PRODUCT is null then 0 else SUPPLY_COST_PER_PRODUCT end)", true)]
    [InlineData("AGG-04", "MAX(IS_DRINK_ITEM)", "max(IS_DRINK_ITEM)", true)]
    [InlineData("AGG-05", "MIN(ORDERED_AT_DAY)", "min(ORDERED_AT_DAY)", true)]
    [InlineData("AGG-06", "COUNT(*)", "count(*)", true)]
    [InlineData("AGG-07", "COUNT(ORDER_ID)", "count(ORDER_ID)", true)]
    [InlineData("OP-01", "(v_R_SCORE*100)+(v_F_SCORE*10)+v_M_SCORE", "(v_R_SCORE * 100) + (v_F_SCORE * 10) + v_M_SCORE", false)]
    [InlineData("OP-02", "COST/100", "COST / 100", false)]
    [InlineData("OP-03", "PRODUCT_PRICE + (PRODUCT_PRICE * v_TAX_RATE_CL)", "PRODUCT_PRICE + (PRODUCT_PRICE * v_TAX_RATE_CL)", false)]
    [InlineData("OP-04", "PRODUCT_PRICE - v_SUPPLY_COST_CL", "PRODUCT_PRICE - v_SUPPLY_COST_CL", false)]
    [InlineData("OP-05", "ORDER_TOTAL_CENTS/100", "ORDER_TOTAL_CENTS / 100", false)]
    [InlineData("PASS-01", "CUSTOMER_ID", "CUSTOMER_ID", false)]
    [InlineData("PASS-02", "'HIGH'", "'HIGH'", false)]
    [InlineData("CONST-01", "'Y'", "'Y'", false)]
    [InlineData("FILTER-01", "NOT ISNULL(LOCATION_ID)", "LOCATION_ID is not null", false)]
    [InlineData("ROUTER-01", "REGION = 'APAC'", "REGION = 'APAC'", false)]
    [InlineData("ROUTER-02", "ORDER_TOTAL >= 50", "ORDER_TOTAL >= 50", false)]
    [InlineData("ROUTER-03", "ORDER_TOTAL < 50", "ORDER_TOTAL < 50", false)]
    [InlineData("JOIN-01", "SKU1 = SKU", "SKU1 = SKU", false)]
    [InlineData("CMP-01", "A <> B", "A <> B", false)]
    [InlineData("CMP-02", "A != B", "A <> B", false)]
    public void Translate_GoldenString(string id, string source, string expected, bool allowAggregates)
    {
        ExpressionTranslation result = Translate(source, allowAggregates);

        Assert.True(result.Sql == expected, id + " got: " + result.Sql);
        Assert.True(result.Diagnostics.Count == 0, id + " " + DiagnosticText(result));
    }

    [Fact]
    public void Translate_UnknownFunction_KeepsTextAndWarns()
    {
        ExpressionTranslation result = Translate("DECODE(STATUS, 'A', 1, 0)", allowAggregates: false);

        Assert.Contains("/* UNSUPPORTED: DECODE(STATUS, 'A', 1, 0) */", result.Sql, StringComparison.Ordinal);
        Assert.Contains("DECODE(STATUS, 'A', 1, 0)", result.Sql, StringComparison.Ordinal);
        Diagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("DECODE", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_UnknownFormatToken_DoesNotGuess()
    {
        ExpressionTranslation result = Translate("TO_CHAR(ORDERED_AT, 'YYYY-MM-DD')", allowAggregates: false);

        Assert.Contains("/* UNSUPPORTED: TO_CHAR(ORDERED_AT, 'YYYY-MM-DD') */", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("strftime", result.Sql, StringComparison.Ordinal);
        Diagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("YYYY", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_AggregateOutsideAggregator_WarnsAndKeepsCall()
    {
        ExpressionTranslation result = Translate("SUM(ORDER_TOTAL)", allowAggregates: false);

        Assert.Equal("sum(ORDER_TOTAL)", result.Sql);
        Diagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("Aggregator", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_MissingVariable_WarnsAndKeepsName()
    {
        ExpressionTranslation result = Translate("$$MISSING", allowAggregates: false);

        Assert.Contains("/* UNSUPPORTED: $$MISSING */", result.Sql, StringComparison.Ordinal);
        Assert.Contains("$$MISSING", result.Sql, StringComparison.Ordinal);
        Diagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
    }

    private static ExpressionTranslation Translate(string expression, bool allowAggregates)
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["$$RUN_DATE"] = "12/31/2025 00:00:00",
        };
        var context = new ExpressionContext(allowAggregates, variables);
        return new ExpressionTranslator().Translate(expression, context);
    }

    private static string DiagnosticText(ExpressionTranslation result)
    {
        return string.Join(
            "\n",
            result.Diagnostics.Select(diagnostic => diagnostic.Severity + " " + diagnostic.Message));
    }
}

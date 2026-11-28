using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Monarch.Core.Autonomy;

/// <summary>Exports read-only procurement analytics as Excel-friendly UTF-8 CSV.</summary>
public static class ProcurementAnalyticsCsvExporter
{
    private static readonly UTF8Encoding Utf8WithBom = new UTF8Encoding(true);

    public static byte[] ExportSupplierItemPerformance(IReadOnlyList<ProcurementSupplierItemPerformance> rows)
    {
        if (rows == null) throw new ArgumentNullException(nameof(rows));
        var csv = new StringBuilder();
        Row(csv, "Supplier ID", "Supplier Code", "Supplier Name", "Item ID", "SKU", "Item Name", "Currency", "Completed Orders", "Received Quantity (milli)", "Weighted Average Unit Cost (minor)");
        foreach (var x in rows)
            Row(csv, x.SupplierId.ToString("D"), x.SupplierCode, x.SupplierName, x.ItemId.ToString("D"), x.Sku,
                x.ItemName, x.CurrencyCode, Number(x.CompletedOrders), Number(x.ReceivedQuantityMilli), NullableNumber(x.AverageUnitCostMinor));
        return Utf8WithBom.GetBytes(csv.ToString());
    }

    public static byte[] ExportSupplierTrends(IReadOnlyList<ProcurementSupplierTrend> rows)
    {
        if (rows == null) throw new ArgumentNullException(nameof(rows));
        var csv = new StringBuilder();
        Row(csv, "Supplier ID", "Supplier Code", "Supplier Name", "Current Orders", "Previous Orders", "Current On-Time Rate", "Previous On-Time Rate", "Current Lead Time Days", "Previous Lead Time Days", "Reliable Comparison", "On-Time Declined", "Lead Time Deteriorated", "Alert Level");
        foreach (var x in rows)
            Row(csv, x.SupplierId.ToString("D"), x.SupplierCode, x.SupplierName, Number(x.CurrentCompletedOrders), Number(x.PreviousCompletedOrders),
                NullableNumber(x.CurrentOnTimeRate), NullableNumber(x.PreviousOnTimeRate), NullableNumber(x.CurrentAverageLeadTimeDays),
                NullableNumber(x.PreviousAverageLeadTimeDays), x.HasReliableComparison ? "true" : "false",
                x.OnTimePerformanceDeclined ? "true" : "false", x.LeadTimeDeteriorated ? "true" : "false", x.AlertLevel);
        return Utf8WithBom.GetBytes(csv.ToString());
    }

    public static byte[] ExportForecastHistory(IReadOnlyList<ProcurementForecastHistoryRow> rows)
    {
        if (rows == null) throw new ArgumentNullException(nameof(rows));
        var csv = new StringBuilder();
        Row(csv, "Item ID", "SKU", "Item Name", "Policy Version", "Horizon Days", "Window Start UTC", "Window End UTC", "Forecast Demand (milli)", "Actual Demand (milli)", "Absolute Error (milli)", "Absolute Percentage Error");
        foreach (var x in rows)
            Row(csv, x.ItemId.ToString("D"), x.Sku, x.ItemName, x.PolicyVersion, Number(x.HorizonDays), x.WindowStartUtc,
                x.WindowEndUtc, Number(x.ForecastDemandMilli), Number(x.ActualDemandMilli), Number(x.AbsoluteErrorMilli), NullableNumber(x.AbsolutePercentageError));
        return Utf8WithBom.GetBytes(csv.ToString());
    }

    // A private sentinel distinguishes trusted numeric fields from untrusted text cells.
    private static string Number(long value) => "\u0001" + value.ToString(CultureInfo.InvariantCulture);
    private static string Number(int value) => "\u0001" + value.ToString(CultureInfo.InvariantCulture);
    private static string NullableNumber(double? value) => value.HasValue ? "\u0001" + value.Value.ToString("R", CultureInfo.InvariantCulture) : string.Empty;

    private static void Row(StringBuilder output, params string[] cells)
    {
        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0) output.Append(',');
            var value = cells[i] ?? string.Empty;
            var trustedNumeric = value.Length > 0 && value[0] == '\u0001';
            if (trustedNumeric) value = value.Substring(1);
            // Prevent spreadsheet formula execution for untrusted textual values while
            // preserving signed numeric cells as numbers rather than text.
            var first = value.TrimStart();
            if (!trustedNumeric && first.Length > 0 && (first[0] == '=' || first[0] == '+' || first[0] == '-' || first[0] == '@' || first[0] == '\t' || first[0] == '\r'))
                value = "'" + value;
            output.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
        }
        output.Append("\r\n");
    }
}

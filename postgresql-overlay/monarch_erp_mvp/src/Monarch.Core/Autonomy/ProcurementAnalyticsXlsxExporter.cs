using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Monarch.Core.Autonomy;

/// <summary>Creates a dependency-free Open XML workbook with one worksheet per procurement report.</summary>
public static class ProcurementAnalyticsXlsxExporter
{
    public static byte[] Export(IReadOnlyList<ProcurementSupplierItemPerformance> supplierItems,
        IReadOnlyList<ProcurementSupplierTrend> supplierTrends,
        IReadOnlyList<ProcurementForecastHistoryRow> forecastHistory)
    {
        if (supplierItems == null) throw new ArgumentNullException(nameof(supplierItems));
        if (supplierTrends == null) throw new ArgumentNullException(nameof(supplierTrends));
        if (forecastHistory == null) throw new ArgumentNullException(nameof(forecastHistory));
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add(archive, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/worksheets/sheet3.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
            Add(archive, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Add(archive, "xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Supplier Items\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Supplier Trends\" sheetId=\"2\" r:id=\"rId2\"/><sheet name=\"Forecast History\" sheetId=\"3\" r:id=\"rId3\"/></sheets></workbook>");
            Add(archive, "xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet2.xml\"/><Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet3.xml\"/></Relationships>");
            Add(archive, "xl/worksheets/sheet1.xml", Sheet(new object?[] { "Supplier ID", "Supplier Code", "Supplier Name", "Item ID", "SKU", "Item Name", "Currency", "Completed Orders", "Received Quantity (milli)", "Weighted Average Unit Cost (minor)" }, Rows(supplierItems)));
            Add(archive, "xl/worksheets/sheet2.xml", Sheet(new object?[] { "Supplier ID", "Supplier Code", "Supplier Name", "Current Orders", "Previous Orders", "Current On-Time Rate", "Previous On-Time Rate", "Current Lead Time Days", "Previous Lead Time Days", "Reliable Comparison", "On-Time Declined", "Lead Time Deteriorated", "Alert Level" }, Rows(supplierTrends)));
            Add(archive, "xl/worksheets/sheet3.xml", Sheet(new object?[] { "Item ID", "SKU", "Item Name", "Policy Version", "Horizon Days", "Window Start UTC", "Window End UTC", "Forecast Demand (milli)", "Actual Demand (milli)", "Absolute Error (milli)", "Absolute Percentage Error" }, Rows(forecastHistory)));
        }
        return output.ToArray();
    }

    private static IEnumerable<object?[]> Rows(IReadOnlyList<ProcurementSupplierItemPerformance> rows)
    { foreach (var x in rows) yield return new object?[] { x.SupplierId.ToString("D"), x.SupplierCode, x.SupplierName, x.ItemId.ToString("D"), x.Sku, x.ItemName, x.CurrencyCode, x.CompletedOrders, x.ReceivedQuantityMilli, x.AverageUnitCostMinor }; }
    private static IEnumerable<object?[]> Rows(IReadOnlyList<ProcurementSupplierTrend> rows)
    { foreach (var x in rows) yield return new object?[] { x.SupplierId.ToString("D"), x.SupplierCode, x.SupplierName, x.CurrentCompletedOrders, x.PreviousCompletedOrders, x.CurrentOnTimeRate, x.PreviousOnTimeRate, x.CurrentAverageLeadTimeDays, x.PreviousAverageLeadTimeDays, x.HasReliableComparison, x.OnTimePerformanceDeclined, x.LeadTimeDeteriorated, x.AlertLevel }; }
    private static IEnumerable<object?[]> Rows(IReadOnlyList<ProcurementForecastHistoryRow> rows)
    { foreach (var x in rows) yield return new object?[] { x.ItemId.ToString("D"), x.Sku, x.ItemName, x.PolicyVersion, x.HorizonDays, x.WindowStartUtc, x.WindowEndUtc, x.ForecastDemandMilli, x.ActualDemandMilli, x.AbsoluteErrorMilli, x.AbsolutePercentageError }; }

    private static string Sheet(object?[] headers, IEnumerable<object?[]> rows)
    {
        var b = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        var rowNumber = 1; AppendRow(b, headers, rowNumber++);
        foreach (var row in rows) AppendRow(b, row, rowNumber++);
        return b.Append("</sheetData></worksheet>").ToString();
    }
    private static void AppendRow(StringBuilder b, object?[] cells, int rowNumber)
    {
        b.Append("<row r=\"").Append(rowNumber.ToString(CultureInfo.InvariantCulture)).Append("\">");
        for (var i = 0; i < cells.Length; i++)
        {
            var value = cells[i]; if (value == null) continue;
            var reference = Column(i + 1) + rowNumber.ToString(CultureInfo.InvariantCulture);
            if (value is bool boolean) b.Append("<c r=\"").Append(reference).Append("\" t=\"b\"><v>").Append(boolean ? "1" : "0").Append("</v></c>");
            else if (value is byte || value is short || value is int || value is long || value is float || value is double || value is decimal)
                b.Append("<c r=\"").Append(reference).Append("\"><v>").Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append("</v></c>");
            else b.Append("<c r=\"").Append(reference).Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">").Append(Escape(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)).Append("</t></is></c>");
        }
        b.Append("</row>");
    }
    private static string Column(int number) { var s = string.Empty; while (number > 0) { number--; s = (char)('A' + number % 26) + s; number /= 26; } return s; }
    private static string Escape(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
    private static void Add(ZipArchive archive, string path, string content)
    { var entry = archive.CreateEntry(path, CompressionLevel.Optimal); using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)); writer.Write(content); }
}

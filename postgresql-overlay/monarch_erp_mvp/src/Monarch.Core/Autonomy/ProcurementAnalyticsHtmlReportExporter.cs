using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;

namespace Monarch.Core.Autonomy;

/// <summary>Builds a self-contained, Arabic RTL, print-ready procurement report that can be printed to PDF by a browser.</summary>
public static class ProcurementAnalyticsHtmlReportExporter
{
    public static byte[] Export(Guid companyId, Guid branchId, DateOnly fromDate, DateOnly toDate,
        IReadOnlyList<ProcurementSupplierItemPerformance> supplierItems,
        IReadOnlyList<ProcurementSupplierTrend> supplierTrends,
        IReadOnlyList<ProcurementForecastHistoryRow> forecastHistory,
        string companyName = "MONARCH ERP",
        string branchName = "",
        byte[]? logoImage = null,
        DateTime? generatedAtUtc = null)
    {
        if (companyId == Guid.Empty || branchId == Guid.Empty) throw new ArgumentException("Company and branch are required.");
        if (toDate < fromDate) throw new ArgumentException("To date must not precede from date.");
        if (supplierItems == null) throw new ArgumentNullException(nameof(supplierItems));
        if (supplierTrends == null) throw new ArgumentNullException(nameof(supplierTrends));
        if (forecastHistory == null) throw new ArgumentNullException(nameof(forecastHistory));

        var b = new StringBuilder();
        var logoDataUri = BuildLogoDataUri(logoImage);
        var generated = (generatedAtUtc ?? DateTime.UtcNow).ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        b.Append("<!doctype html><html lang=\"ar\" dir=\"rtl\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta name=\"generator\" content=\"MONARCH ERP\"><title>تقرير تحليلات المشتريات</title>");
        b.Append("<style>html{direction:rtl}body{font-family:Tahoma,Arial,sans-serif;color:#202735;margin:28px;line-height:1.55}header{border-bottom:3px solid #b08d57;padding-bottom:14px;margin-bottom:20px}.brand{display:flex;align-items:center;gap:16px}.brand img{max-width:88px;max-height:70px;object-fit:contain}.brand-copy{min-width:0}h1{font-size:24px;margin:0 0 6px}h2{font-size:17px;margin:24px 0 8px;color:#26364a}.meta{color:#596579;font-size:12px}.cards{display:flex;gap:10px;flex-wrap:wrap}.card{border:1px solid #d9dee7;border-radius:6px;padding:10px 14px;min-width:120px}.card b{display:block;font-size:20px}table{width:100%;border-collapse:collapse;font-size:10px;margin:8px 0 18px;direction:rtl}th,td{border:1px solid #d8dde5;padding:6px;text-align:right;overflow-wrap:anywhere}th{background:#edf1f6}tbody tr:nth-child(even){background:#fafbfd}.ltr{direction:ltr;unicode-bidi:embed;text-align:left}.note{font-size:10px;color:#697386;margin-top:20px}.report-footer{border-top:1px solid #d9dee7;padding-top:8px;margin-top:24px;color:#697386;font-size:9px;display:flex;justify-content:space-between;gap:12px;break-inside:avoid}.section{break-inside:auto}.summary-alerts{margin:12px 0;padding:10px 14px;border:1px solid #d9dee7;border-radius:6px}.summary-alerts span{display:inline-block;margin-inline-end:18px}@page{size:A4 landscape;margin:12mm}@media print{body{margin:0}h2{break-after:avoid;page-break-after:avoid}table{break-inside:auto;page-break-inside:auto}tr{break-inside:avoid;page-break-inside:avoid}thead{display:table-header-group}.report-footer{position:static}}</style></head><body>");
        b.Append("<header><div class=\"brand\">");
        if (logoDataUri != null) b.Append("<img alt=\"شعار المنشأة\" src=\"").Append(logoDataUri).Append("\">");
        b.Append("<div class=\"brand-copy\"><h1>تقرير تحليلات المشتريات</h1><div>").Append(E(companyName)).Append("</div><div class=\"meta\">الفرع: ").Append(E(branchName)).Append("</div></div></div><div class=\"meta\">الفترة: ").Append(fromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(" — ").Append(toDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append("</div><div class=\"meta ltr\">Company: ").Append(companyId.ToString("D")).Append(" | Branch: ").Append(branchId.ToString("D")).Append("</div><div class=\"meta ltr\">Generated: ").Append(generated).Append("</div></header>");
        var reliableTrends = 0;
        var criticalAlerts = 0;
        var warningAlerts = 0;
        foreach (var trend in supplierTrends)
        {
            if (trend.HasReliableComparison) reliableTrends++;
            if (string.Equals(trend.AlertLevel, "Critical", StringComparison.OrdinalIgnoreCase)) criticalAlerts++;
            else if (string.Equals(trend.AlertLevel, "Warning", StringComparison.OrdinalIgnoreCase)) warningAlerts++;
        }
        b.Append("<section class=\"cards\"><div class=\"card\">أداء المورد/الصنف<b>").Append(supplierItems.Count.ToString(CultureInfo.InvariantCulture)).Append("</b></div><div class=\"card\">اتجاهات الموردين<b>").Append(supplierTrends.Count.ToString(CultureInfo.InvariantCulture)).Append("</b></div><div class=\"card\">مقارنات موثوقة<b>").Append(reliableTrends.ToString(CultureInfo.InvariantCulture)).Append("</b></div><div class=\"card\">فترات التوقع<b>").Append(forecastHistory.Count.ToString(CultureInfo.InvariantCulture)).Append("</b></div></section>");
        b.Append("<div class=\"summary-alerts\"><strong>ملخص التنبيهات:</strong> <span>حرجة: ").Append(criticalAlerts.ToString(CultureInfo.InvariantCulture)).Append("</span><span>تحذيرية: ").Append(warningAlerts.ToString(CultureInfo.InvariantCulture)).Append("</span><span>مقارنات غير مكتملة: ").Append((supplierTrends.Count - reliableTrends).ToString(CultureInfo.InvariantCulture)).Append("</span></div>");

        TableStart(b, "أداء المورد حسب الصنف", new[] { "رمز المورد", "المورد", "SKU", "الصنف", "العملة", "أوامر مكتملة", "الكمية المستلمة (ملي)", "متوسط تكلفة الوحدة (وحدات صغرى)" });
        foreach (var x in supplierItems) Row(b, x.SupplierCode, x.SupplierName, x.Sku, x.ItemName, x.CurrencyCode, N(x.CompletedOrders), N(x.ReceivedQuantityMilli), D(x.AverageUnitCostMinor));
        TableEnd(b);

        TableStart(b, "اتجاهات أداء الموردين", new[] { "رمز المورد", "المورد", "أوامر الفترة الحالية", "أوامر الفترة السابقة", "الالتزام الحالي", "الالتزام السابق", "مدة التوريد الحالية (يوم)", "مدة التوريد السابقة (يوم)", "موثوقية المقارنة", "التنبيه" });
        foreach (var x in supplierTrends) Row(b, x.SupplierCode, x.SupplierName, N(x.CurrentCompletedOrders), N(x.PreviousCompletedOrders), P(x.CurrentOnTimeRate), P(x.PreviousOnTimeRate), D(x.CurrentAverageLeadTimeDays), D(x.PreviousAverageLeadTimeDays), x.HasReliableComparison ? "متاحة" : "بيانات غير كافية", x.AlertLevel);
        TableEnd(b);

        TableStart(b, "السجل التاريخي لدقة التوقع", new[] { "SKU", "الصنف", "إصدار السياسة", "أفق التوقع (يوم)", "بداية الفترة UTC", "نهاية الفترة UTC", "الطلب المتوقع (ملي)", "الطلب الفعلي (ملي)", "الخطأ المطلق (ملي)", "نسبة الخطأ" });
        foreach (var x in forecastHistory) Row(b, x.Sku, x.ItemName, x.PolicyVersion, N(x.HorizonDays), x.WindowStartUtc, x.WindowEndUtc, N(x.ForecastDemandMilli), N(x.ActualDemandMilli), N(x.AbsoluteErrorMilli), P(x.AbsolutePercentageError));
        TableEnd(b);
        b.Append("<p class=\"note\">تم إنشاء هذا التقرير من بيانات تحليلية للقراءة فقط. لتحويله إلى PDF: افتح الملف في متصفح حديث ثم اختر طباعة ← حفظ كملف PDF. لا يحتوي التقرير على بيانات خارج نطاق المنشأة والفرع المحددين.</p>");
        b.Append("<footer class=\"report-footer\"><span>").Append(E(companyName)).Append(" — ").Append(E(branchName)).Append("</span><span>MONARCH ERP | ").Append(generated).Append("</span></footer></body></html>");
        return new UTF8Encoding(true).GetBytes(b.ToString());
    }

    private static string? BuildLogoDataUri(byte[]? image)
    {
        if (image == null || image.Length == 0) return null;
        if (image.Length > 2 * 1024 * 1024) throw new ArgumentException("Logo image must not exceed 2 MB.", nameof(image));
        var png = image.Length >= 8 && image[0] == 0x89 && image[1] == 0x50 && image[2] == 0x4E && image[3] == 0x47 && image[4] == 0x0D && image[5] == 0x0A && image[6] == 0x1A && image[7] == 0x0A;
        var jpeg = image.Length >= 3 && image[0] == 0xFF && image[1] == 0xD8 && image[2] == 0xFF;
        if (!png && !jpeg) throw new ArgumentException("Logo must be a PNG or JPEG image.", nameof(image));
        return "data:" + (png ? "image/png" : "image/jpeg") + ";base64," + Convert.ToBase64String(image);
    }

    private static void TableStart(StringBuilder b, string title, string[] headers)
    { b.Append("<h2>").Append(E(title)).Append("</h2><table><thead><tr>"); foreach (var h in headers) b.Append("<th>").Append(E(h)).Append("</th>"); b.Append("</tr></thead><tbody>"); }
    private static void Row(StringBuilder b, params string[] values)
    { b.Append("<tr>"); foreach (var value in values) b.Append("<td>").Append(E(value)).Append("</td>"); b.Append("</tr>"); }
    private static void TableEnd(StringBuilder b) => b.Append("</tbody></table>");
    private static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
    private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
    private static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
    private static string D(double? value) => value.HasValue ? value.Value.ToString("N2", CultureInfo.InvariantCulture) : "غير متاح";
    private static string P(double? value) => value.HasValue ? (value.Value * 100d).ToString("N1", CultureInfo.InvariantCulture) + "%" : "غير متاح";
}

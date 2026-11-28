using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace Monarch.Core.Zatca;

/// <summary>
/// Produces a UBL 2.1 invoice XML skeleton with Saudi invoice extensions/identifiers.
/// The output must still pass the current ZATCA SDK and rule-set before production use.
/// </summary>
public sealed class ZatcaUblInvoiceGenerator
{
    private const string InvoiceNs = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";
    private const string Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private const string Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private const string Ext = "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2";
    private const string Sig = "urn:oasis:names:specification:ubl:schema:xsd:CommonSignatureComponents-2";
    private const string Sbc = "urn:oasis:names:specification:ubl:schema:xsd:SignatureBasicComponents-2";
    private const string Sac = "urn:oasis:names:specification:ubl:schema:xsd:SignatureAggregateComponents-2";

    public string Generate(ZatcaInvoiceDraft invoice, string previousInvoiceHashBase64, string qrPayloadBase64)
    {
        Validate(invoice);
        if (string.IsNullOrWhiteSpace(previousInvoiceHashBase64)) throw new ArgumentException("Previous invoice hash is required.");
        if (string.IsNullOrWhiteSpace(qrPayloadBase64)) throw new ArgumentException("QR payload is required.");
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, OmitXmlDeclaration = false };
        using var output = new MemoryStream();
        using (var x = XmlWriter.Create(output, settings))
        {
            x.WriteStartDocument(); x.WriteStartElement("Invoice", InvoiceNs);
            x.WriteAttributeString("xmlns", "cac", null, Cac); x.WriteAttributeString("xmlns", "cbc", null, Cbc);
            x.WriteAttributeString("xmlns", "ext", null, Ext); x.WriteAttributeString("xmlns", "sig", null, Sig);
            x.WriteAttributeString("xmlns", "sbc", null, Sbc); x.WriteAttributeString("xmlns", "sac", null, Sac);
            Element(x, "cbc", "UBLVersionID", "2.1");
            Element(x, "cbc", "CustomizationID", "urn:cen.eu:en16931:2017#compliant#urn:zatca: e-invoicing:1.0");
            Element(x, "cbc", "ProfileID", "reporting:1.0");
            Element(x, "cbc", "ID", invoice.InvoiceNumber);
            Element(x, "cbc", "UUID", invoice.Uuid.ToString());
            Element(x, "cbc", "IssueDate", invoice.IssueDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Element(x, "cbc", "IssueTime", invoice.IssueDateTime.ToUniversalTime().ToString("HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            x.WriteStartElement("cbc", "InvoiceTypeCode", Cbc); x.WriteAttributeString("name", InvoiceTypeName(invoice.Kind));
            x.WriteString(InvoiceTypeCode(invoice.Kind)); x.WriteEndElement();
            if (!string.IsNullOrWhiteSpace(invoice.Note)) Element(x, "cbc", "Note", invoice.Note!);
            Element(x, "cbc", "DocumentCurrencyCode", invoice.CurrencyCode.ToUpperInvariant());
            Element(x, "cbc", "TaxCurrencyCode", invoice.CurrencyCode.ToUpperInvariant());
            WriteReference(x, "ICV", invoice.InvoiceCounterValue.ToString(CultureInfo.InvariantCulture));
            WriteReference(x, "PIH", previousInvoiceHashBase64);
            WriteReference(x, "QR", qrPayloadBase64);
            WriteSignatureBlock(x);
            WriteSupplier(x, invoice.Seller);
            if (invoice.Buyer != null) WriteCustomer(x, invoice.Buyer);
            WriteTaxTotals(x, invoice);
            WriteLegalTotals(x, invoice);
            var lineNumber = 0;
            foreach (var line in invoice.Lines) WriteLine(x, invoice, line, ++lineNumber);
            x.WriteEndElement(); x.WriteEndDocument(); x.Flush();
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static void Validate(ZatcaInvoiceDraft d)
    {
        if (d.CompanyId == Guid.Empty || d.BranchId == Guid.Empty || d.EgsUnitId == Guid.Empty || d.Uuid == Guid.Empty) throw new ArgumentException("Company, branch, EGS and UUID are required.");
        if (string.IsNullOrWhiteSpace(d.InvoiceNumber) || d.InvoiceNumber.Length > 100) throw new ArgumentException("Invoice number is required.");
        if (d.CurrencyCode != "SAR") throw new ArgumentException("This initial ZATCA integration supports SAR only; foreign-currency invoices require a tax-currency and FX policy.");
        if (d.InvoiceCounterValue < 1) throw new ArgumentOutOfRangeException(nameof(d.InvoiceCounterValue));
        if (d.Kind is ZatcaInvoiceKind.CreditNote or ZatcaInvoiceKind.DebitNote) throw new NotSupportedException("Credit/debit notes require mandatory original-invoice references and reason fields; this release only generates tax invoices.");
        if (d.Lines == null || d.Lines.Count == 0 || d.Lines.Count > 1000) throw new ArgumentException("Invoice requires 1-1000 lines.");
        ValidateParty(d.Seller, true); if (d.Buyer != null) ValidateParty(d.Buyer, false);
        if (d.Lines.Any(l => string.IsNullOrWhiteSpace(l.Id) || string.IsNullOrWhiteSpace(l.Description) || string.IsNullOrWhiteSpace(l.UnitCode) || l.Quantity <= 0 || l.NetAmountMinor < 0 || l.TaxAmountMinor < 0 || l.TaxRateBasisPoints < 0 || l.TaxRateBasisPoints > 10000)) throw new ArgumentException("Invalid invoice line.");
        if (d.Lines.Any(l => l.TaxCategory == ZatcaTaxCategory.StandardRated && l.TaxRateBasisPoints != 1500)) throw new ArgumentException("Saudi standard-rated supplies must use 15% VAT; use the correct category for other tax treatments.");
        foreach (var line in d.Lines)
        {
            var expectedTax = checked((long)decimal.Round((decimal)line.NetAmountMinor * line.TaxRateBasisPoints / 10000m, 0, MidpointRounding.AwayFromZero));
            if (line.TaxAmountMinor != expectedTax) throw new ArgumentException("Invoice line tax amount does not match its taxable base and VAT rate.");
        }
        if (d.Lines.Any(l => l.TaxCategory != ZatcaTaxCategory.StandardRated && string.IsNullOrWhiteSpace(l.TaxExemptionReasonCode))) throw new ArgumentException("Zero-rated, exempt and out-of-scope lines require a tax exemption/reason code.");
        if (d.Kind == ZatcaInvoiceKind.StandardTaxInvoice && d.Buyer == null) throw new ArgumentException("A standard tax invoice requires buyer details.");
    }
    private static void ValidateParty(ZatcaParty p, bool seller)
    {
        if (p == null || string.IsNullOrWhiteSpace(p.LegalName) || p.Address == null || string.IsNullOrWhiteSpace(p.Address.StreetName) || string.IsNullOrWhiteSpace(p.Address.BuildingNumber) || string.IsNullOrWhiteSpace(p.Address.City) || string.IsNullOrWhiteSpace(p.Address.PostalCode) || p.Address.CountryCode != "SA") throw new ArgumentException("Saudi party legal name and complete Saudi address are required.");
        if (seller) ValidateVatNumber(p.VatNumber);
        else if (!string.IsNullOrWhiteSpace(p.VatNumber)) ValidateVatNumber(p.VatNumber);
    }
    private static void ValidateVatNumber(string? vat)
    {
        if (vat == null || vat.Length != 15 || vat[0] != '3' || vat[14] != '3' || vat.Any(ch => ch < '0' || ch > '9'))
            throw new ArgumentException("Saudi VAT registration number must contain 15 digits, starting and ending with 3.");
    }
    private static string InvoiceTypeName(ZatcaInvoiceKind k) => k == ZatcaInvoiceKind.StandardTaxInvoice || k == ZatcaInvoiceKind.CreditNote ? "0100000" : "0200000";
    private static string InvoiceTypeCode(ZatcaInvoiceKind k) => k == ZatcaInvoiceKind.CreditNote ? "381" : k == ZatcaInvoiceKind.DebitNote ? "383" : "388";
    private static void Element(XmlWriter x, string prefix, string name, string value) { x.WriteElementString(prefix, name, Cbc, value); }
    private static void WriteReference(XmlWriter x, string id, string value)
    {
        x.WriteStartElement("cac", "AdditionalDocumentReference", Cac); Element(x, "cbc", "ID", id);
        if (id == "ICV") Element(x, "cbc", "UUID", value);
        else { x.WriteStartElement("cac", "Attachment", Cac); x.WriteStartElement("cbc", "EmbeddedDocumentBinaryObject", Cbc); x.WriteAttributeString("mimeCode", "text/plain"); x.WriteString(value); x.WriteEndElement(); x.WriteEndElement(); }
        x.WriteEndElement();
    }
    private static void WriteSignatureBlock(XmlWriter x)
    {
        x.WriteStartElement("cac", "Signature", Cac); Element(x, "cbc", "ID", "urn:oasis:names:specification:ubl:signature:Invoice");
        Element(x, "cbc", "SignatureMethod", "urn:oasis:names:specification:ubl:dsig:enveloped:xades"); x.WriteEndElement();
    }
    private static void WriteSupplier(XmlWriter x, ZatcaParty p) => WriteParty(x, "AccountingSupplierParty", p, true);
    private static void WriteCustomer(XmlWriter x, ZatcaParty p) => WriteParty(x, "AccountingCustomerParty", p, false);
    private static void WriteParty(XmlWriter x, string role, ZatcaParty p, bool supplier)
    {
        x.WriteStartElement("cac", role, Cac); x.WriteStartElement("cac", "Party", Cac);
        x.WriteStartElement("cac", "PostalAddress", Cac); Element(x, "cbc", "StreetName", p.Address.StreetName); Element(x, "cbc", "BuildingNumber", p.Address.BuildingNumber); Element(x, "cbc", "CitySubdivisionName", p.Address.District ?? ""); Element(x, "cbc", "CityName", p.Address.City); Element(x, "cbc", "PostalZone", p.Address.PostalCode);
        x.WriteStartElement("cac", "Country", Cac); Element(x, "cbc", "IdentificationCode", p.Address.CountryCode); x.WriteEndElement(); x.WriteEndElement();
        if (supplier || !string.IsNullOrWhiteSpace(p.VatNumber)) { x.WriteStartElement("cac", "PartyTaxScheme", Cac); Element(x, "cbc", "CompanyID", p.VatNumber ?? ""); x.WriteStartElement("cac", "TaxScheme", Cac); Element(x, "cbc", "ID", "VAT"); x.WriteEndElement(); x.WriteEndElement(); }
        x.WriteStartElement("cac", "PartyLegalEntity", Cac); Element(x, "cbc", "RegistrationName", p.LegalName); x.WriteEndElement();
        x.WriteEndElement(); x.WriteEndElement();
    }
    private static void WriteTaxTotals(XmlWriter x, ZatcaInvoiceDraft d)
    {
        var groups = d.Lines.GroupBy(l => new { l.TaxCategory, l.TaxRateBasisPoints, l.TaxExemptionReasonCode, l.TaxExemptionReason }).ToList();
        long taxTotal = d.Lines.Sum(l => l.TaxAmountMinor);
        x.WriteStartElement("cac", "TaxTotal", Cac); Amount(x, "TaxAmount", taxTotal, d.CurrencyCode);
        foreach (var g in groups)
        {
            x.WriteStartElement("cac", "TaxSubtotal", Cac); Amount(x, "TaxableAmount", g.Sum(l => l.NetAmountMinor), d.CurrencyCode); Amount(x, "TaxAmount", g.Sum(l => l.TaxAmountMinor), d.CurrencyCode);
            x.WriteStartElement("cac", "TaxCategory", Cac); Element(x, "cbc", "ID", TaxCategoryCode(g.Key.TaxCategory)); Element(x, "cbc", "Percent", (g.Key.TaxRateBasisPoints / 100m).ToString("0.##", CultureInfo.InvariantCulture));
            if (g.Key.TaxCategory != ZatcaTaxCategory.StandardRated) { Element(x, "cbc", "TaxExemptionReasonCode", g.Key.TaxExemptionReasonCode ?? "VATEX-SA-0"); if (!string.IsNullOrWhiteSpace(g.Key.TaxExemptionReason)) Element(x, "cbc", "TaxExemptionReason", g.Key.TaxExemptionReason!); }
            x.WriteStartElement("cac", "TaxScheme", Cac); Element(x, "cbc", "ID", "VAT"); x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement();
        }
        x.WriteEndElement();
    }
    private static void WriteLegalTotals(XmlWriter x, ZatcaInvoiceDraft d)
    {
        var net = d.Lines.Sum(l => l.NetAmountMinor); var tax = d.Lines.Sum(l => l.TaxAmountMinor); var total = checked(net + tax);
        x.WriteStartElement("cac", "LegalMonetaryTotal", Cac); Amount(x, "LineExtensionAmount", net, d.CurrencyCode); Amount(x, "TaxExclusiveAmount", net, d.CurrencyCode); Amount(x, "TaxInclusiveAmount", total, d.CurrencyCode); Amount(x, "PayableAmount", total, d.CurrencyCode); x.WriteEndElement();
    }
    private static void WriteLine(XmlWriter x, ZatcaInvoiceDraft d, ZatcaInvoiceLine l, int number)
    {
        x.WriteStartElement("cac", "InvoiceLine", Cac); Element(x, "cbc", "ID", number.ToString(CultureInfo.InvariantCulture));
        x.WriteStartElement("cbc", "InvoicedQuantity", Cbc); x.WriteAttributeString("unitCode", l.UnitCode); x.WriteString(l.Quantity.ToString("0.###", CultureInfo.InvariantCulture)); x.WriteEndElement(); Amount(x, "LineExtensionAmount", l.NetAmountMinor, d.CurrencyCode);
        x.WriteStartElement("cac", "TaxTotal", Cac); Amount(x, "TaxAmount", l.TaxAmountMinor, d.CurrencyCode); Amount(x, "RoundingAmount", checked(l.NetAmountMinor + l.TaxAmountMinor), d.CurrencyCode); x.WriteEndElement();
        x.WriteStartElement("cac", "Item", Cac); Element(x, "cbc", "Name", l.Description); x.WriteStartElement("cac", "ClassifiedTaxCategory", Cac); Element(x, "cbc", "ID", TaxCategoryCode(l.TaxCategory)); Element(x, "cbc", "Percent", (l.TaxRateBasisPoints / 100m).ToString("0.##", CultureInfo.InvariantCulture)); if (l.TaxCategory != ZatcaTaxCategory.StandardRated) { Element(x, "cbc", "TaxExemptionReasonCode", l.TaxExemptionReasonCode ?? "VATEX-SA-0"); if (!string.IsNullOrWhiteSpace(l.TaxExemptionReason)) Element(x, "cbc", "TaxExemptionReason", l.TaxExemptionReason!); } x.WriteStartElement("cac", "TaxScheme", Cac); Element(x, "cbc", "ID", "VAT"); x.WriteEndElement(); x.WriteEndElement(); x.WriteEndElement();
        x.WriteStartElement("cac", "Price", Cac); Amount(x, "PriceAmount", l.Quantity == 0 ? 0 : checked((long)decimal.Round(l.NetAmountMinor / l.Quantity, 0, MidpointRounding.AwayFromZero)), d.CurrencyCode); x.WriteEndElement(); x.WriteEndElement();
    }
    private static string TaxCategoryCode(ZatcaTaxCategory c) => c == ZatcaTaxCategory.StandardRated ? "S" : c == ZatcaTaxCategory.ZeroRated ? "Z" : c == ZatcaTaxCategory.Exempt ? "E" : "O";
    private static void Amount(XmlWriter x, string name, long minor, string currency) { x.WriteStartElement("cbc", name, Cbc); x.WriteAttributeString("currencyID", currency); x.WriteString((minor / 100m).ToString("0.00", CultureInfo.InvariantCulture)); x.WriteEndElement(); }
}

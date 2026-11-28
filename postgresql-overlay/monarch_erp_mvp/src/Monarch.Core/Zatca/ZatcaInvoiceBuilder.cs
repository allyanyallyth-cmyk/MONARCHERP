using System;
using System.Globalization;
using System.Linq;

namespace Monarch.Core.Zatca;

public interface IZatcaCryptographicStampProvider
{
    ZatcaCryptographicStamp CreateStamp(byte[] invoiceHash, bool simplifiedInvoice);
}

/// <summary>Must create a complete ZATCA XAdES/X.509 XML signature, not merely insert a signature value.</summary>
public interface IZatcaInvoiceXmlSigner
{
    string ApplyCryptographicStamp(string unsignedInvoiceXml, byte[] invoiceHash, ZatcaCryptographicStamp stamp);
}

/// <summary>Builds hash-chained UBL payloads. It does not claim certification or replace ZATCA SDK validation.</summary>
public sealed class ZatcaInvoiceBuilder
{
    public const string FirstInvoicePreviousHash = "NWZlY2ViNjZmZmM4NmYzOGQ5NTI3ODZjNmQ2OTZjNzljMmRiYzIzOWRkNGU5MWI0NjcyOWQ3M2EyN2ZiNTdlOQ==";
    private readonly ZatcaUblInvoiceGenerator _ubl;
    private readonly ZatcaInvoiceHashService _hash;
    private readonly IZatcaCryptographicStampProvider? _stampProvider;
    private readonly IZatcaInvoiceXmlSigner? _xmlSigner;

    public ZatcaInvoiceBuilder(ZatcaUblInvoiceGenerator? ubl = null, ZatcaInvoiceHashService? hash = null,
        IZatcaCryptographicStampProvider? stampProvider = null, IZatcaInvoiceXmlSigner? xmlSigner = null)
    { _ubl = ubl ?? new ZatcaUblInvoiceGenerator(); _hash = hash ?? new ZatcaInvoiceHashService(); _stampProvider = stampProvider; _xmlSigner = xmlSigner; }

    public ZatcaInvoiceBuildResult Build(ZatcaInvoiceDraft draft, string previousInvoiceHashBase64)
    {
        if (draft == null || draft.Lines == null || draft.Lines.Count == 0) throw new ArgumentException("Invoice and invoice lines are required.", nameof(draft));
        if (string.IsNullOrWhiteSpace(previousInvoiceHashBase64)) previousInvoiceHashBase64 = FirstInvoicePreviousHash;
        var subtotal = draft.Lines.Sum(x => x.NetAmountMinor); var tax = draft.Lines.Sum(x => x.TaxAmountMinor);
        var isSimplified = draft.Kind == ZatcaInvoiceKind.SimplifiedTaxInvoice;
        // QR tags 1-5 are present in the XML submitted for clearance/reporting.
        var basicQr = ZatcaQrCodeEncoder.EncodeBasic(draft.Seller.LegalName, draft.Seller.VatNumber,
            draft.IssueDateTime, checked(subtotal + tax), tax, 2);
        var unsignedXml = _ubl.Generate(draft, previousInvoiceHashBase64, basicQr);
        var digest = _hash.ComputeHash(unsignedXml);
        var finalQr = basicQr;
        ZatcaCryptographicStamp? stamp = null;
        if (isSimplified)
        {
            if (_stampProvider == null || _xmlSigner == null) throw new InvalidOperationException("Simplified Phase-2 reporting requires an onboarded EGS ECDSA P-256 stamp provider and a complete XAdES XML signer.");
            stamp = _stampProvider.CreateStamp(digest, true);
            finalQr = ZatcaQrCodeEncoder.EncodePhase2(draft.Seller.LegalName, draft.Seller.VatNumber,
                draft.IssueDateTime, checked(subtotal + tax), tax, digest, stamp, true, 2);
        }
        var xml = _ubl.Generate(draft, previousInvoiceHashBase64, finalQr);
        if (stamp != null) xml = _xmlSigner!.ApplyCryptographicStamp(xml, digest, stamp);
        var finalDigest = _hash.ComputeHash(xml);
        if (!digest.SequenceEqual(finalDigest)) throw new InvalidOperationException("Invoice digest changed after QR generation; the excluded QR transform is inconsistent.");
        return new ZatcaInvoiceBuildResult(xml, Convert.ToBase64String(finalDigest), previousInvoiceHashBase64, finalQr, draft.Uuid);
    }
}

using System;
using System.Collections.Generic;

namespace Monarch.Core.Zatca;

public enum ZatcaInvoiceKind { StandardTaxInvoice, SimplifiedTaxInvoice, CreditNote, DebitNote }
public enum ZatcaTaxCategory { StandardRated, ZeroRated, Exempt, OutOfScope }
public enum ZatcaSubmissionStatus { Prepared, Pending, Cleared, Reported, Rejected, Failed }

public sealed record ZatcaAddress(string StreetName, string BuildingNumber, string City, string PostalCode,
    string District, string CountryCode = "SA");
public sealed record ZatcaParty(string LegalName, string VatNumber, ZatcaAddress Address, string? SchemeId = null,
    string? SchemeValue = null);
public sealed record ZatcaInvoiceLine(string Id, string Description, decimal Quantity, string UnitCode,
    long NetAmountMinor, long TaxAmountMinor, int TaxRateBasisPoints, ZatcaTaxCategory TaxCategory,
    string? TaxExemptionReasonCode = null, string? TaxExemptionReason = null);
public sealed record ZatcaInvoiceDraft(Guid CompanyId, Guid BranchId, Guid EgsUnitId, Guid Uuid,
    string InvoiceNumber, DateTimeOffset IssueDateTime, ZatcaInvoiceKind Kind, string CurrencyCode,
    ZatcaParty Seller, ZatcaParty? Buyer, IReadOnlyList<ZatcaInvoiceLine> Lines,
    string? PreviousInvoiceHashBase64 = null, string? PaymentMeansCode = null, string? Note = null,
    long InvoiceCounterValue = 1);
public sealed record ZatcaInvoiceBuildResult(string Xml, string InvoiceHashBase64,
    string PreviousInvoiceHashBase64, string QrPayloadBase64, Guid Uuid);
public sealed record ZatcaCryptographicStamp(byte[] Signature, byte[] PublicKey, byte[] CertificateAuthoritySignature);
public sealed record ZatcaApiCredentials(string ClientId, string ClientSecret);
public sealed record ZatcaApiResult(bool Success, bool Accepted, string Status, string? ClearedInvoiceXml,
    string? QrPayloadBase64, string? ValidationErrors, string RawResponse);
public sealed record ZatcaChainRecord(Guid Id, Guid CompanyId, Guid BranchId, Guid EgsUnitId, Guid Uuid,
    string InvoiceNumber, string InvoiceHashBase64, string PreviousInvoiceHashBase64,
    string InvoiceKind, string SubmissionStatus, DateTimeOffset CreatedUtc);

public sealed record ZatcaSubmissionDashboard(int Pending, int Processing, int Failed, int Rejected, int Cleared, int Reported);
public sealed record ZatcaSubmissionHistoryItem(Guid RecordId, Guid EgsUnitId, Guid Uuid, string InvoiceNumber,
    string InvoiceKind, string Status, int Attempts, string? LastResponse, DateTimeOffset CreatedUtc, DateTimeOffset? ProcessedUtc);

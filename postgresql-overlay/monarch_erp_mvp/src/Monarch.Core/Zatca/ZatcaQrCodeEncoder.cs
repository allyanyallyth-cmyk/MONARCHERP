using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Monarch.Core.Zatca;

/// <summary>Builds the ZATCA TLV payload. Tags 1-5 are UTF-8 text; tags 6-9 are binary data.</summary>
public static class ZatcaQrCodeEncoder
{
    public static string EncodeBasic(string sellerName, string vatNumber, DateTimeOffset issueDateTime,
        long invoiceTotalMinor, long vatTotalMinor, int currencyScale = 2)
    {
        ValidateCommon(sellerName, vatNumber, invoiceTotalMinor, vatTotalMinor, currencyScale);
        using var stream = new MemoryStream();
        WriteText(stream, 1, sellerName.Trim());
        WriteText(stream, 2, vatNumber.Trim());
        WriteText(stream, 3, issueDateTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        WriteText(stream, 4, FormatMinor(invoiceTotalMinor, currencyScale));
        WriteText(stream, 5, FormatMinor(vatTotalMinor, currencyScale));
        var encoded = Convert.ToBase64String(stream.ToArray());
        if (encoded.Length > 700) throw new InvalidOperationException("ZATCA QR payload exceeds 700 Base64 characters.");
        return encoded;
    }

    /// <summary>Builds the Phase-2 QR payload with hash, ECDSA signature, public key and (simplified only) CA signature.</summary>
    public static string EncodePhase2(string sellerName, string vatNumber, DateTimeOffset issueDateTime,
        long invoiceTotalMinor, long vatTotalMinor, byte[] invoiceHash, ZatcaCryptographicStamp stamp,
        bool simplifiedInvoice, int currencyScale = 2)
    {
        if (invoiceHash is null || invoiceHash.Length != 32) throw new ArgumentException("SHA-256 hash must contain 32 bytes.", nameof(invoiceHash));
        if (stamp is null || stamp.Signature is null || stamp.Signature.Length == 0 || stamp.PublicKey is null || stamp.PublicKey.Length == 0)
            throw new ArgumentException("A valid ECDSA signature and public key are required.", nameof(stamp));
        ValidateCommon(sellerName, vatNumber, invoiceTotalMinor, vatTotalMinor, currencyScale);
        using var stream = new MemoryStream();
        WriteText(stream, 1, sellerName.Trim()); WriteText(stream, 2, vatNumber.Trim());
        WriteText(stream, 3, issueDateTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        WriteText(stream, 4, FormatMinor(invoiceTotalMinor, currencyScale)); WriteText(stream, 5, FormatMinor(vatTotalMinor, currencyScale));
        WriteBinary(stream, 6, invoiceHash); WriteBinary(stream, 7, stamp.Signature); WriteBinary(stream, 8, stamp.PublicKey);
        if (simplifiedInvoice)
        {
            if (stamp.CertificateAuthoritySignature is null || stamp.CertificateAuthoritySignature.Length == 0)
                throw new ArgumentException("Simplified invoices require the CA signature of the cryptographic stamp public key.", nameof(stamp));
            WriteBinary(stream, 9, stamp.CertificateAuthoritySignature);
        }
        var encoded = Convert.ToBase64String(stream.ToArray());
        if (encoded.Length > 700) throw new InvalidOperationException("ZATCA QR payload exceeds 700 Base64 characters.");
        return encoded;
    }

    private static void ValidateCommon(string seller, string vat, long total, long tax, int scale)
    {
        if (string.IsNullOrWhiteSpace(seller)) throw new ArgumentException("Seller name is required.", nameof(seller));
        if (string.IsNullOrWhiteSpace(vat) || vat.Length != 15 || vat[0] != '3' || vat[14] != '3')
            throw new ArgumentException("Saudi VAT number must contain 15 digits and start/end with 3.", nameof(vat));
        for (var i = 0; i < vat.Length; i++) if (vat[i] < '0' || vat[i] > '9') throw new ArgumentException("VAT number must contain digits only.", nameof(vat));
        if (total < 0 || tax < 0 || tax > total) throw new ArgumentOutOfRangeException(nameof(total));
        if (scale is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(scale));
    }
    private static string FormatMinor(long minor, int scale)
    {
        decimal value = minor;
        for (var i = 0; i < scale; i++) value /= 10m;
        return value.ToString("F" + scale, CultureInfo.InvariantCulture);
    }
    private static void WriteText(Stream stream, byte tag, string value) => WriteBinary(stream, tag, Encoding.UTF8.GetBytes(value));
    private static void WriteBinary(Stream stream, byte tag, byte[] value)
    {
        if (value.Length > byte.MaxValue) throw new ArgumentException("A TLV value cannot exceed 255 bytes.");
        stream.WriteByte(tag); stream.WriteByte((byte)value.Length); stream.Write(value, 0, value.Length);
    }
}

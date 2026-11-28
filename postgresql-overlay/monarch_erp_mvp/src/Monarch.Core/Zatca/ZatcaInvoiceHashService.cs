using System;
using System.IO;
using System.Security.Cryptography;
using System.Xml;
using System.Security.Cryptography.Xml;

namespace Monarch.Core.Zatca;

/// <summary>
/// Computes the invoice digest after excluding UBLExtensions, cac:Signature and the QR reference,
/// then canonicalizing and applying SHA-256. The platform standard names C14N 1.1; this initial
/// implementation uses the .NET C14N transform and emits no xml:id/xml:base nodes, but MUST be
/// compared byte-for-byte with the current ZATCA SDK before production onboarding.
/// </summary>
public sealed class ZatcaInvoiceHashService
{
    public byte[] ComputeHash(string invoiceXml)
    {
        if (string.IsNullOrWhiteSpace(invoiceXml)) throw new ArgumentException("Invoice XML is required.", nameof(invoiceXml));
        var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        document.LoadXml(invoiceXml);
        var ns = new XmlNamespaceManager(document.NameTable);
        ns.AddNamespace("ext", "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2");
        ns.AddNamespace("cac", "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2");
        ns.AddNamespace("cbc", "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2");
        Remove(document.SelectNodes("//ext:UBLExtensions", ns));
        Remove(document.SelectNodes("//cac:Signature", ns));
        foreach (XmlNode node in document.SelectNodes("//cac:AdditionalDocumentReference[cbc:ID='QR']", ns) ?? throw new InvalidOperationException("Invalid UBL document."))
            node.ParentNode?.RemoveChild(node);
        var canonical = new XmlDsigC14NTransform(false);
        canonical.LoadInput(document);
        using var stream = (Stream)canonical.GetOutput(typeof(Stream));
        using var sha = SHA256.Create();
        return sha.ComputeHash(stream);
    }

    public string ComputeHashBase64(string invoiceXml) => Convert.ToBase64String(ComputeHash(invoiceXml));
    private static void Remove(XmlNodeList? nodes)
    {
        if (nodes == null) return;
        for (var i = nodes.Count - 1; i >= 0; i--) nodes[i]?.ParentNode?.RemoveChild(nodes[i]!);
    }
}

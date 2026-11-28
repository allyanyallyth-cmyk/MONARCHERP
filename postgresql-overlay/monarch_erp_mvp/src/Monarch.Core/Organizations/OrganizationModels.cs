namespace Monarch.Core.Organizations;

public sealed class CompanySummary
{
    public Guid Id { get; }
    public string Code { get; }
    public string LegalName { get; }
    public string DisplayName { get; }
    public string CountryCode { get; }
    public string BaseCurrencyCode { get; }
    public string TimeZoneId { get; }
    public bool IsActive { get; }

    public CompanySummary(Guid id, string code, string legalName, string displayName,
        string countryCode, string baseCurrencyCode, string timeZoneId, bool isActive)
    { Id=id; Code=code; LegalName=legalName; DisplayName=displayName; CountryCode=countryCode;
      BaseCurrencyCode=baseCurrencyCode; TimeZoneId=timeZoneId; IsActive=isActive; }
}

public sealed class BranchSummary
{
    public Guid Id { get; }
    public Guid CompanyId { get; }
    public string Code { get; }
    public string Name { get; }
    public string? Address { get; }
    public bool IsHeadOffice { get; }
    public bool IsActive { get; }
    public BranchSummary(Guid id, Guid companyId, string code, string name, string? address, bool isHeadOffice, bool isActive)
    { Id=id; CompanyId=companyId; Code=code; Name=name; Address=address; IsHeadOffice=isHeadOffice; IsActive=isActive; }
}

public sealed class CompanyAccessSummary
{
    public Guid CompanyId { get; }
    public string CompanyName { get; }
    public string RoleCode { get; }
    public bool IsActive { get; }
    public CompanyAccessSummary(Guid companyId, string companyName, string roleCode, bool isActive)
    { CompanyId=companyId; CompanyName=companyName; RoleCode=roleCode; IsActive=isActive; }
}

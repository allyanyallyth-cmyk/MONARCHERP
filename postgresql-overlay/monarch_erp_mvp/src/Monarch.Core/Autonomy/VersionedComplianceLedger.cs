using System;
using System.Collections.Generic;
using System.Linq;

namespace Monarch.Core.Autonomy;

public sealed record VersionedCarbonFactor(string ActivityCode, string Unit, decimal KgCo2ePerUnit, string Source,
    string Version, DateTimeOffset EffectiveFromUtc, DateTimeOffset? EffectiveToUtc, bool Approved);
public sealed record VersionedTaxRule(string Jurisdiction, string RuleCode, string Version, decimal Rate,
    DateTimeOffset EffectiveFromUtc, DateTimeOffset? EffectiveToUtc, bool Approved, string OfficialSource);
public sealed record CarbonDocumentSnapshot(string ActivityCode, decimal Quantity, decimal KgCo2e,
    string FactorVersion, DateTimeOffset TransactionUtc, string Source);

/// <summary>Append-only in-memory domain selector. Production persistence must enforce unique versions and immutable approved rows.</summary>
public sealed class VersionedComplianceLedger
{
    private readonly List<VersionedCarbonFactor> _factors = new();
    private readonly List<VersionedTaxRule> _taxRules = new();
    private readonly List<CarbonDocumentSnapshot> _snapshots = new();
    public void AddCarbonFactor(VersionedCarbonFactor factor)
    {
        if (factor == null || string.IsNullOrWhiteSpace(factor.Version) || string.IsNullOrWhiteSpace(factor.Source) || factor.KgCo2ePerUnit < 0 ||
            (factor.EffectiveToUtc.HasValue && factor.EffectiveToUtc <= factor.EffectiveFromUtc)) throw new ArgumentException("Invalid carbon factor version.");
        if (_factors.Any(x => x.ActivityCode == factor.ActivityCode && x.Version == factor.Version)) throw new InvalidOperationException("Carbon factor versions are immutable and unique.");
        _factors.Add(factor);
    }
    public void AddTaxRule(VersionedTaxRule rule)
    {
        if (rule == null || string.IsNullOrWhiteSpace(rule.OfficialSource) || string.IsNullOrWhiteSpace(rule.Version) || rule.Rate < 0 ||
            (rule.EffectiveToUtc.HasValue && rule.EffectiveToUtc <= rule.EffectiveFromUtc)) throw new ArgumentException("Invalid tax rule version.");
        if (_taxRules.Any(x => x.Jurisdiction == rule.Jurisdiction && x.RuleCode == rule.RuleCode && x.Version == rule.Version)) throw new InvalidOperationException("Tax rule versions are immutable and unique.");
        _taxRules.Add(rule);
    }
    public VersionedCarbonFactor ResolveCarbonFactor(string activityCode, DateTimeOffset transactionUtc)
    {
        var matches = _factors.Where(x => x.ActivityCode == activityCode && x.Approved && x.EffectiveFromUtc <= transactionUtc && (!x.EffectiveToUtc.HasValue || transactionUtc < x.EffectiveToUtc)).OrderByDescending(x => x.EffectiveFromUtc).ToArray();
        if (matches.Length == 0) throw new InvalidOperationException("No approved carbon factor is effective for the transaction date.");
        return matches[0];
    }
    public VersionedTaxRule ResolveTaxRule(string jurisdiction, string ruleCode, DateTimeOffset transactionUtc)
    {
        var matches = _taxRules.Where(x => x.Jurisdiction == jurisdiction && x.RuleCode == ruleCode && x.Approved && x.EffectiveFromUtc <= transactionUtc && (!x.EffectiveToUtc.HasValue || transactionUtc < x.EffectiveToUtc)).OrderByDescending(x => x.EffectiveFromUtc).ToArray();
        if (matches.Length == 0) throw new InvalidOperationException("No approved tax rule is effective for the transaction date.");
        return matches[0];
    }
    public CarbonDocumentSnapshot RecordCarbonDocument(string activityCode, decimal quantity, DateTimeOffset transactionUtc, string source)
    {
        if (quantity < 0 || string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Quantity and source are required.");
        var factor = ResolveCarbonFactor(activityCode, transactionUtc);
        var snapshot = new CarbonDocumentSnapshot(activityCode, quantity, quantity * factor.KgCo2ePerUnit, factor.Version, transactionUtc, source);
        _snapshots.Add(snapshot); return snapshot;
    }
    public IReadOnlyList<CarbonDocumentSnapshot> ListCarbonSnapshots() => _snapshots.AsReadOnly();
}

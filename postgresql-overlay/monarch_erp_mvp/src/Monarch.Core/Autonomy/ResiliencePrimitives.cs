using System;

namespace Monarch.Core.Autonomy;

public enum CircuitState { Closed, Open, HalfOpen }

/// <summary>Thread-safe, process-local circuit breaker. Persist state externally if cross-process coordination is required.</summary>
public sealed class CircuitBreaker
{
    private readonly object _sync = new(); private readonly int _failureThreshold; private readonly TimeSpan _openDuration;
    private int _failures; private DateTimeOffset _openedAt;
    public CircuitBreaker(int failureThreshold, TimeSpan openDuration)
    { if (failureThreshold < 1 || openDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(); _failureThreshold = failureThreshold; _openDuration = openDuration; }
    public CircuitState State { get { lock (_sync) { if (_failures >= _failureThreshold && DateTimeOffset.UtcNow - _openedAt >= _openDuration) return CircuitState.HalfOpen; return _failures >= _failureThreshold ? CircuitState.Open : CircuitState.Closed; } } }
    public bool TryEnter()
    { lock (_sync) { if (_failures < _failureThreshold) return true; if (DateTimeOffset.UtcNow - _openedAt < _openDuration) return false; _failures = _failureThreshold - 1; return true; } }
    public void RecordSuccess() { lock (_sync) _failures = 0; }
    public void RecordFailure() { lock (_sync) { _failures++; if (_failures >= _failureThreshold) _openedAt = DateTimeOffset.UtcNow; } }
}

public sealed record CarbonFactor(string ActivityCode, string Unit, decimal KgCo2ePerUnit, string Source, string Version, DateTimeOffset EffectiveFromUtc, bool IsApproved);
public sealed record CarbonFootprint(string ActivityCode, decimal Quantity, decimal KgCo2e, string FactorVersion, bool IsEstimate);

/// <summary>Calculates traceable estimates from approved factor snapshots; it never changes legal/tax rules.</summary>
public sealed class GreenLedgerCalculator
{
    public CarbonFootprint Calculate(string activityCode, decimal quantity, CarbonFactor factor)
    {
        if (quantity < 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (!factor.IsApproved) throw new InvalidOperationException("Unapproved emission factor cannot be used for reporting.");
        if (!string.Equals(activityCode, factor.ActivityCode, StringComparison.Ordinal)) throw new ArgumentException("Activity/factor mismatch.");
        return new CarbonFootprint(activityCode, quantity, quantity * factor.KgCo2ePerUnit, factor.Version, true);
    }
}

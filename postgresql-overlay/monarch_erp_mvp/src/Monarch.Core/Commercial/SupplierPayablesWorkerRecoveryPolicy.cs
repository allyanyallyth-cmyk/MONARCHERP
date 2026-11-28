using Microsoft.Data.Sqlite;

namespace Monarch.Core.Commercial;

/// <summary>Classifies worker failures and recommends operator/retry handling without scheduling work.</summary>
public enum SupplierPayablesFailureKind
{
    TransientInfrastructure = 1,
    RequiresInvestigation = 2
}

public sealed record SupplierPayablesRecoveryAssessment(
    SupplierPayablesFailureKind Kind,
    string ReasonCode,
    TimeSpan? RecommendedDelay,
    bool ShouldAlertOperator);

/// <summary>
/// Pure recovery guidance for the supplier AP outbox. It deliberately does not retry,
/// release leases, acknowledge events, or change accounting state. The durable outbox
/// remains the authority for whether an event can be claimed again.
/// </summary>
public sealed class SupplierPayablesWorkerRecoveryPolicy
{
    public const int DefaultAlertThreshold = 5;
    public static readonly TimeSpan BaseRetryDelay = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(15);

    public SupplierPayablesRecoveryAssessment Assess(
        Exception exception, int attempt, int consecutiveFailures,
        int alertThreshold = DefaultAlertThreshold)
    {
        if (exception is null) throw new ArgumentNullException(nameof(exception));
        if (attempt < 1) throw new ArgumentOutOfRangeException(nameof(attempt));
        if (consecutiveFailures < 1) throw new ArgumentOutOfRangeException(nameof(consecutiveFailures));
        if (alertThreshold < 1) throw new ArgumentOutOfRangeException(nameof(alertThreshold));

        var (transient, code) = Classify(exception);
        var alert = !transient || consecutiveFailures >= alertThreshold;
        return new SupplierPayablesRecoveryAssessment(
            transient ? SupplierPayablesFailureKind.TransientInfrastructure : SupplierPayablesFailureKind.RequiresInvestigation,
            code,
            transient ? RecommendedDelay(attempt) : null,
            alert);
    }

    private static (bool Transient, string Code) Classify(Exception exception)
    {
        if (exception is SqliteException sqlite)
        {
            // SQLITE_BUSY, SQLITE_LOCKED and SQLITE_IOERR are potentially recoverable.
            return sqlite.SqliteErrorCode switch
            {
                5 => (true, "sqlite_busy"),
                6 => (true, "sqlite_locked"),
                10 => (true, "sqlite_io_error"),
                _ => (false, "sqlite_nontransient")
            };
        }
        if (exception is TimeoutException) return (true, "operation_timeout");
        if (exception is IOException) return (true, "io_error");
        return (false, "unclassified_failure");
    }

    private static TimeSpan RecommendedDelay(int attempt)
    {
        var ticks = BaseRetryDelay.Ticks;
        // Saturating exponential backoff; never overflow and never exceed the cap.
        for (var i = 1; i < attempt && ticks < MaximumRetryDelay.Ticks; i++)
            ticks = ticks > MaximumRetryDelay.Ticks / 2 ? MaximumRetryDelay.Ticks : ticks * 2;
        return TimeSpan.FromTicks(Math.Min(ticks, MaximumRetryDelay.Ticks));
    }
}

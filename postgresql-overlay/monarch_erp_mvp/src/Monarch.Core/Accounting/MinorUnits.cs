namespace Monarch.Core.Accounting;

/// <summary>Converts user-entered decimal amounts to integer minor units using an explicit currency exponent.</summary>
public static class MinorUnits
{
    public static long FromMajor(decimal amount, int exponent)
    {
        if (exponent is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(exponent));
        var factor = Pow10(exponent);
        var scaled = amount * factor;
        if (scaled != decimal.Truncate(scaled))
            throw new ArgumentException($"Amount has more than {exponent} fractional digits.", nameof(amount));
        return checked((long)scaled);
    }

    public static decimal ToMajor(long amountMinor, int exponent)
    {
        if (exponent is < 0 or > 4) throw new ArgumentOutOfRangeException(nameof(exponent));
        return amountMinor / (decimal)Pow10(exponent);
    }

    private static long Pow10(int exponent)
    {
        long result = 1;
        for (var i = 0; i < exponent; i++) result = checked(result * 10);
        return result;
    }
}

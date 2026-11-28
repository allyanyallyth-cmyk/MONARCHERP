#if NET461
using System.Globalization;

namespace System
{
    /// <summary>Small DateOnly compatibility type for the .NET Framework 4.6.1 desktop target.</summary>
    public readonly struct DateOnly : IComparable<DateOnly>, IEquatable<DateOnly>
    {
        private readonly DateTime _value;
        public static DateOnly MinValue => new DateOnly(DateTime.MinValue);
        public DateOnly(int year, int month, int day) : this(new DateTime(year, month, day)) { }
        private DateOnly(DateTime value) { _value = value.Date; }
        public static DateOnly ParseExact(string text, string format, IFormatProvider provider)
            => new DateOnly(DateTime.ParseExact(text, format, provider, DateTimeStyles.None));
        public static DateOnly FromDateTime(DateTime value) => new DateOnly(value);
        public override string ToString()
            => _value.ToString(CultureInfo.CurrentCulture);
        public string ToString(string format)
            => _value.ToString(format, CultureInfo.CurrentCulture);
        public string ToString(string format, IFormatProvider provider)
            => _value.ToString(format, provider);
        public int DayNumber => (int)(_value - DateTime.MinValue).TotalDays;
        public DateOnly AddDays(int value) => new DateOnly(_value.AddDays(value));
        public int CompareTo(DateOnly other) => _value.CompareTo(other._value);
        public bool Equals(DateOnly other) => _value == other._value;
        public override bool Equals(object obj) => obj is DateOnly other && Equals(other);
        public override int GetHashCode() => _value.GetHashCode();
        public static bool operator ==(DateOnly left, DateOnly right) => left.Equals(right);
        public static bool operator !=(DateOnly left, DateOnly right) => !left.Equals(right);
        public static bool operator <(DateOnly left, DateOnly right) => left._value < right._value;
        public static bool operator >(DateOnly left, DateOnly right) => left._value > right._value;
        public static bool operator <=(DateOnly left, DateOnly right) => left._value <= right._value;
        public static bool operator >=(DateOnly left, DateOnly right) => left._value >= right._value;
    }
}

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif

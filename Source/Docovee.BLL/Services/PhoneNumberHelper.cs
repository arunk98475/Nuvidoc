namespace Docovee.BLL.Services;

/// <summary>
/// Normalizes phone numbers for matching: digits only, last 10 numerals
/// (ignores '-', '(', ')', spaces, country code, etc.).
/// </summary>
public static class PhoneNumberHelper
{
    /// <summary>Trim and strip everything except 0-9.</summary>
    public static string DigitsOnly(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return string.Empty;

        return new string(phone.Trim().Where(char.IsDigit).ToArray());
    }

    public static string? NormalizeLast10(string? phone)
    {
        var digits = DigitsOnly(phone);
        if (digits.Length == 11 && digits[0] == '1')
            digits = digits[1..];
        if (digits.Length < 10)
            return null;

        return digits.Length == 10 ? digits : digits[^10..];
    }

    public static bool Matches(string? left, string? right)
    {
        var a = NormalizeLast10(left);
        var b = NormalizeLast10(right);
        return a != null && a == b;
    }

    /// <summary>Formats as (XXX) XXX-XXXX using the last 10 digits when possible.</summary>
    public static string? FormatUsDisplay(string? phone)
    {
        var normalized = NormalizeLast10(phone);
        if (normalized == null)
            return string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();

        return $"({normalized[..3]}) {normalized[3..6]}-{normalized[6..]}";
    }
}

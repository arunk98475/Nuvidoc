using System.Text.RegularExpressions;

namespace Docovee.BLL.Security;

/// <summary>
/// Strips common identifiers from text before vendor LLM prompts (Anthropic).
/// Tokenizes rather than inventing data; not a guarantee against all PHI.
/// </summary>
public static class PhiPromptSanitizer
{
    private static readonly Regex EmailRegex = new(
        @"\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PhoneRegex = new(
        @"(?:\+?1[\s\-.]*)?(?:\(?\d{3}\)?[\s\-.]*)\d{3}[\s\-.]*\d{4}\b",
        RegexOptions.Compiled);

    private static readonly Regex DobRegex = new(
        @"\b(?:0?[1-9]|1[0-2])[\/\-.](?:0?[1-9]|[12]\d|3[01])[\/\-.](?:19|20)\d{2}\b" +
        @"|\b(?:19|20)\d{2}[\/\-.](?:0?[1-9]|1[0-2])[\/\-.](?:0?[1-9]|[12]\d|3[01])\b" +
        @"|\b(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\s+\d{1,2},?\s+(?:19|20)\d{2}\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MemberIdRegex = new(
        @"\b(?:member\s*(?:id|#|number)|subscriber\s*(?:id|#)|policy\s*(?:id|#|number)|group\s*(?:id|#|number))\s*[:#]?\s*[A-Z0-9\-]{4,}\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StreetAddressRegex = new(
        @"\b\d{1,6}\s+[A-Z0-9][A-Z0-9\s\.\-']{2,40}\s(?:st|street|ave|avenue|rd|road|blvd|boulevard|ln|lane|dr|drive|ct|court|way|pl|place|cir|circle|hwy|highway|pkwy|parkway)\b\.?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SsnLikeRegex = new(
        @"\b\d{3}-\d{2}-\d{4}\b",
        RegexOptions.Compiled);

    private static readonly string[] ClinicalHints =
    [
        "pain", "tooth", "teeth", "gum", "cavity", "abscess", "swelling", "bleeding",
        "infection", "implant", "extraction", "root canal", "orthodont", "braces",
        "invisalign", "symptom", "diagnosis", "prescription", "medication", "allergy",
        "pregnant", "diabetes", "chief complaint", "medical issue", "hurt", "ache"
    ];

    public static string Deidentify(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        var result = text;
        result = EmailRegex.Replace(result, "[EMAIL]");
        result = PhoneRegex.Replace(result, "[PHONE]");
        result = DobRegex.Replace(result, "[DOB]");
        result = MemberIdRegex.Replace(result, "[MEMBER_ID]");
        result = StreetAddressRegex.Replace(result, "[ADDRESS]");
        result = SsnLikeRegex.Replace(result, "[SSN]");
        return result;
    }

    /// <summary>
    /// True when text looks like clinical triage / patient free-text that should not be web-searched.
    /// </summary>
    public static bool MayContainClinicalOrPatientContent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (text.Contains("[EMAIL]", StringComparison.Ordinal)
            || text.Contains("[PHONE]", StringComparison.Ordinal)
            || text.Contains("[DOB]", StringComparison.Ordinal)
            || text.Contains("[MEMBER_ID]", StringComparison.Ordinal)
            || text.Contains("[ADDRESS]", StringComparison.Ordinal)
            || text.Contains("[SSN]", StringComparison.Ordinal))
        {
            return true;
        }

        if (EmailRegex.IsMatch(text) || PhoneRegex.IsMatch(text) || DobRegex.IsMatch(text)
            || MemberIdRegex.IsMatch(text) || StreetAddressRegex.IsMatch(text))
        {
            return true;
        }

        var lower = text.ToLowerInvariant();
        foreach (var hint in ClinicalHints)
        {
            if (lower.Contains(hint, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}

namespace nuvidoc.Services;

/// <summary>Persists mobile JWT and signed-in patient info.</summary>
public static class AuthSession
{
    private const string TokenKey = "patient_access_token";
    private const string ExpiresKey = "patient_token_expires";
    private const string SignedInKey = "patient_signed_in";
    private const string EmailKey = "patient_email";
    private const string NameKey = "patient_full_name";
    private const string PatientIdKey = "patient_id";

    public static bool IsSignedIn => Preferences.Default.Get(SignedInKey, false);

    public static string? AccessToken
    {
        get
        {
            var token = Preferences.Default.Get(TokenKey, string.Empty);
            if (string.IsNullOrWhiteSpace(token))
                return null;

            var expiresRaw = Preferences.Default.Get(ExpiresKey, string.Empty);
            if (DateTime.TryParse(expiresRaw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var expires)
                && expires < DateTime.UtcNow.AddMinutes(-1))
            {
                Clear();
                return null;
            }

            return token;
        }
    }

    public static void SaveLogin(string email, string? fullName, int? patientId, string? accessToken, DateTime? expiresAt)
    {
        Preferences.Default.Set(SignedInKey, true);
        Preferences.Default.Set(EmailKey, email);
        if (!string.IsNullOrWhiteSpace(fullName))
            Preferences.Default.Set(NameKey, fullName);
        if (patientId is > 0)
            Preferences.Default.Set(PatientIdKey, patientId.Value);
        if (!string.IsNullOrWhiteSpace(accessToken))
            Preferences.Default.Set(TokenKey, accessToken);
        if (expiresAt.HasValue)
            Preferences.Default.Set(ExpiresKey, expiresAt.Value.ToUniversalTime().ToString("O"));
    }

    public static void Clear()
    {
        Preferences.Default.Remove(SignedInKey);
        Preferences.Default.Remove(EmailKey);
        Preferences.Default.Remove(NameKey);
        Preferences.Default.Remove(PatientIdKey);
        Preferences.Default.Remove(TokenKey);
        Preferences.Default.Remove(ExpiresKey);
        Preferences.Default.Remove("patient_account_created");
    }
}

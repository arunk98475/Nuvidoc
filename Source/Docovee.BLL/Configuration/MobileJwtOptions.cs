namespace Docovee.BLL.Configuration;

public class MobileJwtOptions
{
    public const string SectionName = "MobileJwt";

    public string Issuer { get; set; } = "NuviDoc";
    public string Audience { get; set; } = "NuviDoc.Mobile";
    /// <summary>HMAC signing key — set a long random value in production.</summary>
    public string SigningKey { get; set; } = "CHANGE-ME-NUVIDOC-MOBILE-JWT-SIGNING-KEY-32+";
    public int ExpiresHours { get; set; } = 720;
}

namespace Docovee.BLL.Configuration;

public class AdminOptions
{
    public const string SectionName = "Admin";
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "Admin@123";
}

public static class AppSettingKeys
{
    public const string DoctorSearchResultCount = "DoctorSearchResultCount";
    public const string PromotedDoctorIds = "PromotedDoctorIds";
    public const string MaxAiQuestions = "MaxAiQuestions";
    public const string ReviewEligibleDaysAfterConfirmed = "ReviewEligibleDaysAfterConfirmed";
    /// <summary>One-time flag: Houston dentist homepage + SEO landing pages seeded.</summary>
    public const string HoustonMarketingSeeded = "HoustonMarketingSeeded";

    public const string FooterFacebookUrl = "FooterFacebookUrl";
    public const string FooterInstagramUrl = "FooterInstagramUrl";
    public const string FooterTwitterUrl = "FooterTwitterUrl";
    public const string FooterLinkedInUrl = "FooterLinkedInUrl";
    public const string FooterAppStoreUrl = "FooterAppStoreUrl";
    public const string FooterPlayStoreUrl = "FooterPlayStoreUrl";
    public const string FooterTermsPdfUrl = "FooterTermsPdfUrl";
    public const string FooterPrivacyPdfUrl = "FooterPrivacyPdfUrl";
    public const string FooterConsumerHealthPdfUrl = "FooterConsumerHealthPdfUrl";
    public const string FooterPrivacyChoicesPdfUrl = "FooterPrivacyChoicesPdfUrl";

    public const string DefaultPerVisitFeeCents = "DefaultPerVisitFeeCents";
    public const string FreeVisitCount = "FreeVisitCount";
    /// <summary>Minimum QualityScore (0–100) required before a doctor can enable sponsorship.</summary>
    public const string MinQualityScoreForSponsorship = "MinQualityScoreForSponsorship";
    /// <summary>Minimum Google review count required before a doctor can enable sponsorship.</summary>
    public const string MinGoogleReviewCountForSponsorship = "MinGoogleReviewCountForSponsorship";
    public const string SponsorshipBillingAmountCents = "SponsorshipBillingAmountCents";
    public const string SponsorshipBillingInterval = "SponsorshipBillingInterval";
    public const string SponsorshipBillingCustomDays = "SponsorshipBillingCustomDays";
}

namespace Docovee.BLL.Configuration;

public class AdminOptions
{
    public const string SectionName = "Admin";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string SMS { get; set; } = string.Empty;
    public string WhatsApp { get; set; } = string.Empty;

    /// <summary>Legacy appsettings key; used when <see cref="SMS"/> is empty.</summary>
    public string PhoneNumber { get; set; } = string.Empty;

    /// <summary>Legacy appsettings key; used when <see cref="WhatsApp"/> is empty.</summary>
    public string WhatsAppNumber { get; set; } = string.Empty;

    public string ResolvedSms =>
        string.IsNullOrWhiteSpace(SMS) ? PhoneNumber.Trim() : SMS.Trim();

    public string ResolvedWhatsApp =>
        string.IsNullOrWhiteSpace(WhatsApp) ? WhatsAppNumber.Trim() : WhatsApp.Trim();

    public bool RequiresOtp =>
        !string.IsNullOrWhiteSpace(Email)
        || !string.IsNullOrWhiteSpace(ResolvedSms)
        || !string.IsNullOrWhiteSpace(ResolvedWhatsApp);
}

public static class AppSettingKeys
{
    public const string DoctorSearchResultCount = "DoctorSearchResultCount";
    public const string PromotedDoctorIds = "PromotedDoctorIds";
    public const string MaxAiQuestions = "MaxAiQuestions";
    public const string ReviewEligibleDaysAfterConfirmed = "ReviewEligibleDaysAfterConfirmed";
    /// <summary>When true, Nuvi asks budget preference and search may rank by practice procedure fees.</summary>
    public const string EnableProcedureCostConsideration = "EnableProcedureCostConsideration";
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
    /// <summary>When true, per-visit billing runs only after the patient is marked as showed.</summary>
    public const string VisitBillingChargeOnlyIfPatientShowed = "VisitBillingChargeOnlyIfPatientShowed";
    /// <summary>Minimum QualityScore (0–100) required before a doctor can enable sponsorship.</summary>
    public const string MinQualityScoreForSponsorship = "MinQualityScoreForSponsorship";
    /// <summary>Minimum Google review count required before a doctor can enable sponsorship.</summary>
    public const string MinGoogleReviewCountForSponsorship = "MinGoogleReviewCountForSponsorship";
    public const string SponsorshipBillingAmountCents = "SponsorshipBillingAmountCents";
    public const string SponsorshipBillingInterval = "SponsorshipBillingInterval";
    public const string SponsorshipBillingCustomDays = "SponsorshipBillingCustomDays";
    /// <summary>When true, per-booking sponsorship is charged only after the patient is marked as showed.</summary>
    public const string SponsorshipBillingChargeOnlyIfPatientShowed = "SponsorshipBillingChargeOnlyIfPatientShowed";

    public const string BookingReminderEnabled = "BookingReminderEnabled";
    public const string BookingReminderIntervalDays = "BookingReminderIntervalDays";
    public const string BookingReminderStopAfterMonths = "BookingReminderStopAfterMonths";
    public const string BookingReminderEnableWhatsApp = "BookingReminderEnableWhatsApp";
    public const string BookingReminderEnableEmail = "BookingReminderEnableEmail";
    public const string BookingReminderEnableSms = "BookingReminderEnableSms";
    /// <summary>UTC timestamp (round-trip) of the last booking-reminder worker cycle.</summary>
    public const string BookingReminderLastRunUtc = "BookingReminderLastRunUtc";
    /// <summary>Reminders sent on the last worker cycle.</summary>
    public const string BookingReminderLastRunSentCount = "BookingReminderLastRunSentCount";

    public const string PatientAutoCloseInactiveEnabled = "PatientAutoCloseInactiveEnabled";
    public const string PatientAutoCloseInactiveMonths = "PatientAutoCloseInactiveMonths";
    public const string PatientAutoDeleteClosedEnabled = "PatientAutoDeleteClosedEnabled";
    public const string PatientAutoDeleteClosedMonths = "PatientAutoDeleteClosedMonths";

    /// <summary>When true, Nuvi sends an email OTP during new-patient signup after email is entered.</summary>
    public const string EnableNuviEmailVerificationForNewPatients = "EnableNuviEmailVerificationForNewPatients";
    /// <summary>When true, Nuvi sends an SMS OTP during new-patient signup after phone is entered.</summary>
    public const string EnableNuviPhoneVerificationForNewPatients = "EnableNuviPhoneVerificationForNewPatients";
}

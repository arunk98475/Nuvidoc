namespace nuvidoc.Flow;

/// <summary>Stages from AppSrc/nuvidoc/Flow_Instructions.md.md (OpenAI / Figma patient flow).</summary>
public enum PatientFlowStage
{
    GuestWelcome,
    ExistingWelcome,
    Concern,
    FirstVisit,
    ReusePriorTriage,
    Urgency,
    InsuranceStatus,
    InsurancePlan,
    TravelPreference,
    DistancePreference,
    DoctorExperience,
    TopSchool,
    ReviewsImportance,
    PreferredLanguage,
    BedsideManner,
    HolisticVsConventional,
    DeepDivePermission,
    DeepDiveFreeText,
    DoctorMatching,
    DoctorSelection,
    BookingPreference,
    AccountPermission,
    AppointmentDays,
    AppointmentTimes,
    CallingOffices,
    BookingResult,
    Complete
}

namespace Docovee.DS.Models;

public enum NuviConversationStage
{
    Greeting,
    Triage,
    ImplantQualification,
    Logistics,
    MomentumBridge,
    DeepDivePermission,
    AccountCreation,
    DeepDive,
    RecommendationReveal,
    DoctorExplore,
    CallingConsent,
    CallingOffices,
    BookingInitiation,
    Confirmation,
    Complete,
    /// <summary>Registered patient selecting an appointment to cancel via chat.</summary>
    CancelBooking,
    /// <summary>Registered patient rescheduling an appointment via chat.</summary>
    RescheduleBooking,
    /// <summary>After a successful cancel, offer Yes/No to start a new booking.</summary>
    PostCancelNewBooking
}

public enum RescheduleBookingStep
{
    None,
    SelectAppointment,
    SelectWindow,
    ConfirmCall
}

public enum CallingConsentStep
{
    None,
    AskCallPermission,
    AskMoreQuestions,
    AskAllOrTop,
    AskPreference
}

public enum CallOfficeScope
{
    None,
    TopOne,
    All,
    Selected
}

public enum CallOfficePreference
{
    None,
    Dentist,
    DateAndTime
}

public enum AccountCreationStep
{
    Name,
    Email,
    Phone,
    DateOfBirth,
    Password,
    ConfirmPassword,
    LoginPassword
}

public enum DeepDiveFollowUpStep
{
    None,
    AwaitingLanguageSelection,
    AwaitingWildcardConcern
}

public class SearchContextData
{
    public NuviConversationStage Stage { get; set; } = NuviConversationStage.Greeting;
    /// <summary>0 = awaiting first-visit question; 1 = awaiting Yes/No; 2 = awaiting username; 3 = awaiting password.</summary>
    public int GreetingStep { get; set; }
    public int TriageQuestionCount { get; set; }
    public int ImplantQualStep { get; set; }
    public int LogisticsStep { get; set; }
    public bool? ImplantIntentQualified { get; set; }
    public bool? ImplantTimingQualified { get; set; }
    public string? ImplantPayerType { get; set; }
    public bool? ImplantFinancingQualified { get; set; }
    /// <summary>True once implant screening has been passed and logistics may proceed.</summary>
    public bool ImplantQualificationComplete { get; set; }
    public string? VisitPreference { get; set; }
    public string? UrgencyPreference { get; set; }
    public string? LocationPreference { get; set; }
    /// <summary>Last browser/device GPS captured on a chat request (fallback when ZIP geocode fails or ZIP is skipped).</summary>
    public double? BrowserLatitude { get; set; }
    public double? BrowserLongitude { get; set; }
    public string? InsurancePreference { get; set; }
    public string? InsuranceCategory { get; set; }
    public AccountCreationStep AccountStep { get; set; } = AccountCreationStep.Name;
    public string? PendingFullName { get; set; }
    public string? PendingUsername { get; set; }
    public string? PendingEmail { get; set; }
    public string? PendingPhone { get; set; }
    public string? PendingPassword { get; set; }
    public bool IsExistingAccountLogin { get; set; }
    public bool SkipAccountCreation { get; set; }
    public string? LastKnownLocation { get; set; }
    public bool HasPriorDeepDiveAnswers { get; set; }
    public List<PollingAnswerEntry>? SavedDeepDiveAnswers { get; set; }
    public bool SkipDeepDive { get; set; }
    public DeepDiveFollowUpStep DeepDiveFollowUp { get; set; }
    public string? LanguagePreference { get; set; }
    public string? WildcardConcern { get; set; }
    public DateOnly? PatientDateOfBirth { get; set; }
    public List<PollingAnswerEntry> PollingAnswers { get; set; } = new();
    public int QuestionsAsked { get; set; }
    public int? CurrentPollingQuestionId { get; set; }
    public bool PollingComplete { get; set; }
    public List<int>? MatchedDoctorIds { get; set; }
    /// <summary>Doctors who already received a concierge recommendation in this session.</summary>
    public List<int> RecommendedDoctorIds { get; set; } = new();
    public int? SelectedDoctorId { get; set; }
    public bool BookingConfirmed { get; set; }
    public string? PendingNormalizedAnswer { get; set; }
    public bool AwaitingMatchSearch { get; set; }
    public CallingConsentStep CallingStep { get; set; } = CallingConsentStep.None;
    public CallOfficeScope CallScope { get; set; } = CallOfficeScope.None;
    public CallOfficePreference CallPreference { get; set; } = CallOfficePreference.None;
    /// <summary>Doctor IDs the user has checked in the list when CallScope == Selected.</summary>
    public List<int>? CallDoctorIds { get; set; }
    /// <summary>Chip labels → appointment ids while Stage is CancelBooking.</summary>
    public List<CancelAppointmentChoice>? CancelAppointmentChoices { get; set; }
    public RescheduleBookingStep RescheduleStep { get; set; } = RescheduleBookingStep.None;
    public List<CancelAppointmentChoice>? RescheduleAppointmentChoices { get; set; }
    public int? RescheduleSelectedAppointmentId { get; set; }
    public string? RescheduleUrgencyPreference { get; set; }
}

public class CancelAppointmentChoice
{
    public int AppointmentId { get; set; }
    public string Label { get; set; } = string.Empty;
}

public class PollingAnswerEntry
{
    public int QuestionId { get; set; }
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public int MatchWeight { get; set; }
}

public class PatientPreferenceProfile
{
    public string? VisitPreference { get; set; }
    public string? LocationPreference { get; set; }
    public string? UrgencyPreference { get; set; }
    public string? InsurancePreference { get; set; }
    public string? InsuranceCategory { get; set; }
    public string? LanguagePreference { get; set; }
    public string? WildcardConcern { get; set; }
    public List<PollingAnswerEntry>? DeepDiveAnswers { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

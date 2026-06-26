namespace Docovee.BLL.Models;

public enum NuviConversationStage
{
    Greeting,
    Triage,
    Logistics,
    MomentumBridge,
    AccountCreation,
    DeepDive,
    RecommendationReveal,
    DoctorExplore,
    BookingInitiation,
    Confirmation,
    Complete
}

public enum AccountCreationStep
{
    Name,
    Email,
    Phone,
    Password
}

public class SearchContextData
{
    public NuviConversationStage Stage { get; set; } = NuviConversationStage.Greeting;
    public int TriageQuestionCount { get; set; }
    public int LogisticsStep { get; set; }
    public string? VisitPreference { get; set; }
    public string? UrgencyPreference { get; set; }
    public string? LocationPreference { get; set; }
    public string? InsurancePreference { get; set; }
    public AccountCreationStep AccountStep { get; set; } = AccountCreationStep.Name;
    public string? PendingFullName { get; set; }
    public string? PendingEmail { get; set; }
    public string? PendingPhone { get; set; }
    public List<PollingAnswerEntry> PollingAnswers { get; set; } = new();
    public int QuestionsAsked { get; set; }
    public int? CurrentPollingQuestionId { get; set; }
    public bool PollingComplete { get; set; }
    public List<int>? MatchedDoctorIds { get; set; }
    public int? SelectedDoctorId { get; set; }
    public bool BookingConfirmed { get; set; }
}

public class PollingAnswerEntry
{
    public int QuestionId { get; set; }
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
}

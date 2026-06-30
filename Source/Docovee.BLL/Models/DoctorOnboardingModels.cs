namespace Docovee.BLL.Models;

public enum DoctorOnboardingStage
{
    Name,
    Email,
    Phone,
    Password,
    ConfirmPassword,
    Questions,
    Complete
}

public record DoctorOnboardingQuestion(
    int Id,
    string Category,
    string Question,
    string AnswerType,
    string OptionsHint,
    bool Required);

public class DoctorOnboardingContextData
{
    public DoctorOnboardingStage Stage { get; set; } = DoctorOnboardingStage.Name;
    public int CurrentQuestionIndex { get; set; }
    public int? DoctorId { get; set; }
    public string? PendingName { get; set; }
    public string? PendingEmail { get; set; }
    public string? PendingPhone { get; set; }
    public string? PendingPassword { get; set; }
    public Dictionary<int, string> Answers { get; set; } = new();
}

public class DoctorOnboardingMessageRequest
{
    public Guid? SessionKey { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class DoctorOnboardingMessageResponse
{
    public Guid SessionKey { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty;
    public IReadOnlyList<string>? Options { get; set; }
    public bool UsePasswordInput { get; set; }
    public bool FlowComplete { get; set; }
    public bool SignedIn { get; set; }
    public int? QuestionNumber { get; set; }
    public int? TotalQuestions { get; set; }
    public int ProfileCompletionPercent { get; set; }
}

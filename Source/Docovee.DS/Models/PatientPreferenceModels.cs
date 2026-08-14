namespace Docovee.DS.Models;

public class PatientPreferenceQuestionVm
{
    public int QuestionId { get; set; }
    public string Question { get; set; } = string.Empty;
    public IReadOnlyList<string>? Options { get; set; }
    public bool IsLanguage { get; set; }
    public bool IsWildcard { get; set; }
    public bool IsYesNo { get; set; }
    public string Answer { get; set; } = string.Empty;
    public string? FollowUp { get; set; }
}

public class PatientPreferenceAnswerInput
{
    public int QuestionId { get; set; }
    public string? Answer { get; set; }
    public string? FollowUp { get; set; }
}

public class PatientPreferencePageModel
{
    public IReadOnlyList<PatientPreferenceQuestionVm> Questions { get; set; } = Array.Empty<PatientPreferenceQuestionVm>();
    public IReadOnlyList<string> Languages { get; set; } = Array.Empty<string>();
}

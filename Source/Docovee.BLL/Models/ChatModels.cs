namespace Docovee.BLL.Models;

public class ChatMessageRequest
{
    public Guid? SessionKey { get; set; }
    public string Message { get; set; } = string.Empty;
    public int? SelectedDoctorId { get; set; }
    public string? Action { get; set; }
}

public class ChatMessageResponse
{
    public Guid SessionKey { get; set; }
    public string Text { get; set; } = string.Empty;
    public bool Done { get; set; }
    public bool FlowComplete { get; set; }
    public bool AwaitingPollingAnswer { get; set; }
    public int? CurrentPollingQuestionId { get; set; }
    public string? Specialty { get; set; }
    public string? Urgency { get; set; }
    public string? Notes { get; set; }
    public string? Stage { get; set; }
    public IReadOnlyList<string>? Options { get; set; }
    public bool ShowLoading { get; set; }
    public string? FollowUpText { get; set; }
    public bool UsePasswordInput { get; set; }
    public bool SignedIn { get; set; }
    public IReadOnlyList<DoctorDto>? DoctorCards { get; set; }
    public DoctorDetailDto? SelectedDoctor { get; set; }
}

public class DoctorDetailDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Specialty { get; set; } = string.Empty;
    public string? PracticeName { get; set; }
    public string Location { get; set; } = string.Empty;
    public string? PhotoUrl { get; set; }
    public string AvatarInitials { get; set; } = string.Empty;
    public int MatchScore { get; set; }
    public string? MatchReason { get; set; }
    public string? SummaryOfReviews { get; set; }
    public string? Niche { get; set; }
    public int? YearsOfPractice { get; set; }
    public string? OfficePhoneNumber { get; set; }
    public string? OfficeHours { get; set; }
    public decimal GoogleRating { get; set; }
    public int GoogleReviewCount { get; set; }
}

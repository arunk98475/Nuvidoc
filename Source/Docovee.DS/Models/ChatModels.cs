namespace Docovee.DS.Models;

public class ChatMessageRequest
{
    public Guid? SessionKey { get; set; }
    public string Message { get; set; } = string.Empty;
    public int? SelectedDoctorId { get; set; }
    public string? Action { get; set; }
    /// <summary>Browser/device GPS when the patient allows location access.</summary>
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    /// <summary>Doctor IDs the patient has checked in the in-chat list box.</summary>
    public List<int>? SelectedDoctorIds { get; set; }
}

public class ChatRecordContactViewRequest
{
    public Guid SessionKey { get; set; }
    public int DoctorId { get; set; }
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
    public bool AwaitingMatchSearch { get; set; }
    public string? FollowUpText { get; set; }
    public IReadOnlyList<string>? LanguageOptions { get; set; }
    public bool AwaitingLanguageSelection { get; set; }
    public bool AwaitingWildcardConcern { get; set; }
    public string? PollingQuestionKind { get; set; }
    public string? InputPlaceholder { get; set; }
    public bool UsePasswordInput { get; set; }
    public bool OptionsOnly { get; set; }
    public bool SignedIn { get; set; }
    public IReadOnlyList<DoctorDto>? DoctorCards { get; set; }
    public DoctorDetailDto? SelectedDoctor { get; set; }

    /// <summary>ElevenLabs conversation id when a voice call was placed.</summary>
    public string? ConversationId { get; set; }
    public string? CallSid { get; set; }
    public int? CallingDoctorId { get; set; }
    public string? CallingDoctorName { get; set; }
    public string? VoiceCallStatus { get; set; }
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

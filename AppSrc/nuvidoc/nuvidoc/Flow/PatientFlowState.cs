using Docovee.DS.Models;

namespace nuvidoc.Flow;

public sealed class PatientFlowState
{
    public bool IsSignedIn { get; set; }
    public bool HasPriorTriage { get; set; }
    public PatientFlowStage Stage { get; set; } = PatientFlowStage.GuestWelcome;

    public string? Concern { get; set; }
    public bool IsFirstVisit { get; set; } = true;
    public string? Urgency { get; set; }
    public bool HasInsurance { get; set; }
    public string? InsurancePlan { get; set; }
    public string? TravelPreference { get; set; }
    public string? DistancePreference { get; set; }
    public string? DoctorExperience { get; set; }
    public string? TopSchoolPreference { get; set; }
    public string? ReviewsImportance { get; set; }
    public string? PreferredLanguage { get; set; }
    public string? BedsideManner { get; set; }
    public string? HolisticPreference { get; set; }
    public bool WantsDeepDive { get; set; }
    public string? DeepDiveNotes { get; set; }

    public List<DoctorDto> RankedDoctors { get; set; } = new();
    public List<int> SelectedDoctorIds { get; set; } = new();
    public string? BookingPreference { get; set; }
    public bool NeedsAccount { get; set; } = true;
    public bool AccountCreated { get; set; }
    public string? PreferredDays { get; set; }
    public string? PreferredTimes { get; set; }
    public bool BookingSucceeded { get; set; }
    public string? BookedSummary { get; set; }
}

public sealed class PatientFlowPrompt
{
    public string Text { get; init; } = "";
    public IReadOnlyList<string>? Options { get; init; }
    public bool FreeTextAllowed { get; init; } = true;
    public bool ShowDoctorCards { get; init; }
    public bool NavigateToRegistration { get; init; }
    public bool StartCallingSimulation { get; init; }
    public bool FlowComplete { get; init; }
    public string ProgressLabel { get; init; } = "";
}

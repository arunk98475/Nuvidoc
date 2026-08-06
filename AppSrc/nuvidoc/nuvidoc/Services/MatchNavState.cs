using Docovee.DS.Models;

namespace nuvidoc.Services;

/// <summary>
/// Passes match-search session data between MainPage, SearchResultPage, and DoctorProfilePage
/// without stuffing large objects into Shell query strings.
/// </summary>
public sealed class MatchNavState
{
    public Guid? SessionKey { get; set; }

    public IReadOnlyList<DoctorDto>? DoctorCards { get; set; }

    public string? RevealText { get; set; }

    public int? SelectedDoctorId { get; set; }

    public string? MatchReason { get; set; }

    public string? NuviComment { get; set; }

    public string? SelectedDoctorName { get; set; }

    /// <summary>Card snapshot for immediate profile display before full API load.</summary>
    public DoctorDto? SelectedDoctorCard { get; set; }

    /// <summary>True until SearchResultPage finishes match_search (or loads existing cards).</summary>
    public bool NeedsSearch { get; set; }

    public void BeginSearch(Guid sessionKey)
    {
        SessionKey = sessionKey;
        DoctorCards = null;
        RevealText = null;
        SelectedDoctorId = null;
        MatchReason = null;
        NuviComment = null;
        SelectedDoctorName = null;
        SelectedDoctorCard = null;
        NeedsSearch = true;
    }

    public void SetResults(IReadOnlyList<DoctorDto>? cards, string? revealText)
    {
        DoctorCards = cards;
        RevealText = revealText;
        NeedsSearch = false;
    }

    /// <summary>Select a doctor for the profile page. Nuvi comment is loaded asynchronously on that page.</summary>
    public void SelectDoctor(DoctorDto doctor)
    {
        SelectedDoctorId = doctor.Id;
        SelectedDoctorName = doctor.Name;
        SelectedDoctorCard = doctor;
        MatchReason = doctor.MatchReason;
        NuviComment = null;
    }
}

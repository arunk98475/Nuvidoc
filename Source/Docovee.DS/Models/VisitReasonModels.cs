namespace Docovee.DS.Models;

public class VisitReasonCategoryPreference
{
    public string Key { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int NewPatientMinutes { get; set; } = 45;
    public int ExistingPatientMinutes { get; set; } = 45;
    public List<string> PopularSelectedKeys { get; set; } = new();
}

public class VisitReasonPreferencesInput
{
    public List<VisitReasonCategoryPreference> Categories { get; set; } = new();
}

public class VisitReasonCategoryViewModel
{
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int NewPatientMinutes { get; set; }
    public int ExistingPatientMinutes { get; set; }
    public IReadOnlyList<VisitReasonPopularViewModel> PopularItems { get; set; } = Array.Empty<VisitReasonPopularViewModel>();
}

public class VisitReasonPopularViewModel
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Selected { get; set; }
}

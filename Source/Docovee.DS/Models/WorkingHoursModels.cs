namespace Docovee.DS.Models;

public class WorkingHoursBlockInput
{
    public string StartTime { get; set; } = "09:00";
    public string EndTime { get; set; } = "17:00";
    public List<int> LocationIds { get; set; } = new();
}

public class WorkingHoursDayInput
{
    public string Day { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public List<WorkingHoursBlockInput> Blocks { get; set; } = new();
}

public class WorkingHoursInput
{
    public List<WorkingHoursDayInput> Days { get; set; } = new();
}

public class WorkingHoursLocationOption
{
    public int Id { get; set; }
    public string Label { get; set; } = string.Empty;
}

public class WorkingHoursPageModel
{
    public string DoctorDisplayName { get; set; } = string.Empty;
    public WorkingHoursInput Hours { get; set; } = new();
    public IReadOnlyList<WorkingHoursLocationOption> Locations { get; set; } = Array.Empty<WorkingHoursLocationOption>();
}

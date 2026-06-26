namespace Docovee.DS.Entities;

public class PollingQuestion
{
    public int Id { get; set; }
    public string Question { get; set; } = string.Empty;
    public string? ValidationHint { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

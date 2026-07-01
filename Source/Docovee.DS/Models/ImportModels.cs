namespace Docovee.DS.Models;

public class ImportProgress
{
    public int TotalRows { get; set; }
    public int ProcessedRows { get; set; }
    public int Imported { get; set; }
    public int Failed { get; set; }
    public bool Complete { get; set; }
    public List<string> Errors { get; set; } = new();
    public int Percent => TotalRows > 0 ? (int)Math.Round(ProcessedRows * 100.0 / TotalRows) : 0;
}

public class ImportJobStatus
{
    public string JobId { get; set; } = string.Empty;
    public int TotalRows { get; set; }
    public int ProcessedRows { get; set; }
    public int Imported { get; set; }
    public int Failed { get; set; }
    public bool Complete { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> Errors { get; set; } = new();
    public int Percent => TotalRows > 0 ? (int)Math.Round(ProcessedRows * 100.0 / TotalRows) : (Complete ? 100 : 0);
}

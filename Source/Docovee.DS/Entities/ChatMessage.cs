namespace Docovee.DS.Entities;

public class ChatMessage
{
    public int Id { get; set; }
    public int SearchSessionId { get; set; }
    public SearchSession SearchSession { get; set; } = null!;
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

namespace Docovee.DS.Entities;

public class PatientNurtureSend
{
    public int Id { get; set; }
    public int PatientId { get; set; }
    public Patient Patient { get; set; } = null!;
    /// <summary>Days since registration for this send step (e.g. 30, 60, 90).</summary>
    public int StepDay { get; set; }
    /// <summary>Sms, WhatsApp, or Email.</summary>
    public string Channel { get; set; } = string.Empty;
    public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;
}

public static class PatientNurtureChannels
{
    public const string Sms = "Sms";
    public const string WhatsApp = "WhatsApp";
    public const string Email = "Email";
}

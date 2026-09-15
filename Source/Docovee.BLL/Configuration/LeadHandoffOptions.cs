namespace Docovee.BLL.Configuration;

public class LeadHandoffOptions
{
    public const string SectionName = "LeadHandoff";

    /// <summary>
    /// When true (default), Nuvi chat shares leads via SMS/email instead of voice dialing.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

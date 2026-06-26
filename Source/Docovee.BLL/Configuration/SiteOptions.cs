namespace Docovee.BLL.Configuration;

public class SiteOptions
{
    public const string SectionName = "Site";
    public string Name { get; set; } = "NuviDoc";
}

public class ChatBotOptions
{
    public const string SectionName = "ChatBot";
    public string Name { get; set; } = "Nuvi";
}

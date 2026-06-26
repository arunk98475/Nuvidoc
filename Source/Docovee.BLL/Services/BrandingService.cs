using Docovee.BLL.Configuration;
using Microsoft.Extensions.Options;

namespace Docovee.BLL.Services;

public interface IBrandingService
{
    string SiteName { get; }
    string SiteNameLower { get; }
    string ChatBotName { get; }
    string ChatBotInitial { get; }
}

public class BrandingService : IBrandingService
{
    public BrandingService(IOptions<SiteOptions> siteOptions, IOptions<ChatBotOptions> chatBotOptions)
    {
        SiteName = siteOptions.Value.Name;
        SiteNameLower = SiteName.ToLowerInvariant();
        ChatBotName = chatBotOptions.Value.Name;
        ChatBotInitial = string.IsNullOrEmpty(ChatBotName) ? "N" : char.ToUpperInvariant(ChatBotName[0]).ToString();
    }

    public string SiteName { get; }
    public string SiteNameLower { get; }
    public string ChatBotName { get; }
    public string ChatBotInitial { get; }
}

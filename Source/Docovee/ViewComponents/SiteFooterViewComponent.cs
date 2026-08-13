using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;

namespace Docovee.ViewComponents;

public class SiteFooterViewComponent : ViewComponent
{
    private readonly IAppSettingsService _settings;

    public SiteFooterViewComponent(IAppSettingsService settings) => _settings = settings;

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var model = await _settings.GetSiteSettingsAsync();
        return View(model);
    }
}

using Docovee.BLL.Services;
using Docovee.DS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Admin.HomePage;

public class IndexModel : PageModel
{
    private readonly IHomePageContentService _home;
    private readonly IBrandingService _branding;
    private readonly IPublicDoctorService _doctors;

    public IndexModel(
        IHomePageContentService home,
        IBrandingService branding,
        IPublicDoctorService doctors)
    {
        _home = home;
        _branding = branding;
        _doctors = doctors;
    }

    [BindProperty]
    public HomePageContentModel Input { get; set; } = new();

    public IReadOnlyList<FeaturedDoctorCardDto> FeaturedDoctors { get; set; } = Array.Empty<FeaturedDoctorCardDto>();
    public string? SuccessMessage { get; set; }

    public async Task OnGetAsync()
    {
        Input = await _home.GetForEditAsync();
        Input = HomePageContentService.Resolve(Input, _branding.SiteName, _branding.ChatBotName);
        FeaturedDoctors = await _doctors.GetFeaturedAsync(3);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await _home.SaveAsync(Input);
        SuccessMessage = "Homepage saved. Changes are live on the public site.";
        Input = await _home.GetForEditAsync();
        Input = HomePageContentService.Resolve(Input, _branding.SiteName, _branding.ChatBotName);
        FeaturedDoctors = await _doctors.GetFeaturedAsync(3);
        return Page();
    }

    public async Task<IActionResult> OnPostLoadDefaultsAsync()
    {
        Input = HomePageContentService.Defaults(_branding.SiteName, _branding.ChatBotName);
        FeaturedDoctors = await _doctors.GetFeaturedAsync(3);
        return Page();
    }
}

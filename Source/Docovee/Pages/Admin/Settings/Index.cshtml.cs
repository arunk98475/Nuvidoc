using Docovee.BLL.Configuration;
using Docovee.BLL.Services;
using Docovee.DS.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Docovee.Pages.Admin.Settings;

public class IndexModel : PageModel
{
    private const long MaxPdfBytes = 10L * 1024 * 1024;
    private readonly IAppSettingsService _settingsService;
    private readonly UploadOptions _uploads;

    public IndexModel(IAppSettingsService settingsService, IOptions<UploadOptions> uploads)
    {
        _settingsService = settingsService;
        _uploads = uploads.Value;
    }

    [BindProperty]
    public SiteSettingsModel Input { get; set; } = new();

    [BindProperty]
    public IFormFile? TermsPdf { get; set; }

    [BindProperty]
    public IFormFile? PrivacyPdf { get; set; }

    [BindProperty]
    public IFormFile? ConsumerHealthPdf { get; set; }

    [BindProperty]
    public IFormFile? PrivacyChoicesPdf { get; set; }

    [BindProperty]
    public bool RemoveTermsPdf { get; set; }

    [BindProperty]
    public bool RemovePrivacyPdf { get; set; }

    [BindProperty]
    public bool RemoveConsumerHealthPdf { get; set; }

    [BindProperty]
    public bool RemovePrivacyChoicesPdf { get; set; }

    public string? SuccessMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        Input = await _settingsService.GetSiteSettingsAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var current = await _settingsService.GetSiteSettingsAsync();
        Input.PromotedDoctorIds = current.PromotedDoctorIds;

        Input.TermsPdfUrl = await ResolvePdfAsync(TermsPdf, RemoveTermsPdf, current.TermsPdfUrl);
        if (ErrorMessage != null) return await ReloadWithErrorAsync();
        Input.PrivacyPdfUrl = await ResolvePdfAsync(PrivacyPdf, RemovePrivacyPdf, current.PrivacyPdfUrl);
        if (ErrorMessage != null) return await ReloadWithErrorAsync();
        Input.ConsumerHealthPdfUrl = await ResolvePdfAsync(ConsumerHealthPdf, RemoveConsumerHealthPdf, current.ConsumerHealthPdfUrl);
        if (ErrorMessage != null) return await ReloadWithErrorAsync();
        Input.PrivacyChoicesPdfUrl = await ResolvePdfAsync(PrivacyChoicesPdf, RemovePrivacyChoicesPdf, current.PrivacyChoicesPdfUrl);
        if (ErrorMessage != null) return await ReloadWithErrorAsync();

        await _settingsService.SaveSiteSettingsAsync(Input);
        SuccessMessage = "Settings saved successfully.";
        Input = await _settingsService.GetSiteSettingsAsync();
        return Page();
    }

    private async Task<IActionResult> ReloadWithErrorAsync()
    {
        var posted = Input;
        Input = await _settingsService.GetSiteSettingsAsync();
        Input.FacebookUrl = posted.FacebookUrl;
        Input.InstagramUrl = posted.InstagramUrl;
        Input.TwitterUrl = posted.TwitterUrl;
        Input.LinkedInUrl = posted.LinkedInUrl;
        Input.AppStoreUrl = posted.AppStoreUrl;
        Input.PlayStoreUrl = posted.PlayStoreUrl;
        Input.DoctorSearchResultCount = posted.DoctorSearchResultCount;
        Input.MaxAiQuestions = posted.MaxAiQuestions;
        Input.ReviewEligibleDaysAfterConfirmed = posted.ReviewEligibleDaysAfterConfirmed;
        return Page();
    }

    private async Task<string> ResolvePdfAsync(IFormFile? file, bool remove, string currentUrl)
    {
        if (remove)
            return string.Empty;

        if (file is null || file.Length == 0)
            return currentUrl ?? string.Empty;

        if (file.Length > MaxPdfBytes)
        {
            ErrorMessage = "PDF files must be 10 MB or smaller.";
            return currentUrl ?? string.Empty;
        }

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext != ".pdf")
        {
            ErrorMessage = "Legal documents must be PDF files.";
            return currentUrl ?? string.Empty;
        }

        var physical = _uploads.LegalPdfsPhysicalPath;
        if (string.IsNullOrWhiteSpace(physical))
        {
            ErrorMessage = "Legal upload folder is not configured.";
            return currentUrl ?? string.Empty;
        }

        Directory.CreateDirectory(physical);
        var fileName = $"{Guid.NewGuid():N}.pdf";
        var dest = Path.Combine(physical, fileName);
        await using (var stream = System.IO.File.Create(dest))
            await file.CopyToAsync(stream);

        return $"{_uploads.LegalPdfsPublicPath.TrimEnd('/')}/{fileName}";
    }
}

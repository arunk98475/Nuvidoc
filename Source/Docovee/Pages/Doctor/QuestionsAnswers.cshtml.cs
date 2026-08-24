using System.Security.Claims;
using Docovee.BLL.Auth;
using Docovee.BLL.Services;
using Docovee.DS.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Doctor;

[Authorize(Roles = AuthRoles.Doctor)]
public class QuestionsAnswersModel : PageModel
{
    private readonly IProfileService _profileService;

    public QuestionsAnswersModel(IProfileService profileService)
    {
        _profileService = profileService;
    }

    public DoctorQaPageModel QaPage { get; private set; } = new();
    public bool Saved { get; private set; }
    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(bool? saved = null, CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return RedirectToPage("/Account/Login");

        Saved = saved == true;
        QaPage = await _profileService.GetDoctorQaAsync(doctorId, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var doctorId))
            return RedirectToPage("/Account/Login");

        var answers = new Dictionary<int, string>();
        foreach (var key in Request.Form.Keys)
        {
            if (key.StartsWith("qa_", StringComparison.Ordinal)
                && int.TryParse(key["qa_".Length..], out var qId))
            {
                answers[qId] = Request.Form[key].FirstOrDefault() ?? "";
            }
        }

        var (success, error) = await _profileService.SaveDoctorQaAsync(doctorId, answers, cancellationToken);
        if (!success)
        {
            ErrorMessage = error;
            QaPage = await _profileService.GetDoctorQaAsync(doctorId, cancellationToken);
            return Page();
        }

        return RedirectToPage(new { saved = true });
    }
}

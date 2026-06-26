using Docovee.BLL.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Admin.PollingQuestions;

public class IndexModel : PageModel
{
    private readonly IPollingQuestionService _pollingService;

    public IndexModel(IPollingQuestionService pollingService) => _pollingService = pollingService;

    public IReadOnlyList<PollingQuestionDto> Questions { get; set; } = Array.Empty<PollingQuestionDto>();

    public async Task OnGetAsync() =>
        Questions = await _pollingService.GetAllAsync();

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        await _pollingService.DeleteAsync(id);
        return RedirectToPage();
    }
}

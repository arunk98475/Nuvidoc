using Docovee.DS.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Admin.PollingQuestions;

public class EditModel : PageModel
{
    private readonly IPollingQuestionService _pollingService;

    public EditModel(IPollingQuestionService pollingService) => _pollingService = pollingService;

    [BindProperty]
    public PollingQuestionEditModel Input { get; set; } = new();

    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var q = await _pollingService.GetForEditAsync(id);
        if (q == null) return NotFound();
        Input = q;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        if (Input.Id == 0)
            Input.Id = id;

        var (success, error) = await _pollingService.UpdateAsync(Input);
        if (!success)
        {
            ErrorMessage = error;
            return Page();
        }
        return RedirectToPage("Index");
    }
}

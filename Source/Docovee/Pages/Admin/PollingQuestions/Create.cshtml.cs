using Docovee.BLL.Models;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Admin.PollingQuestions;

public class CreateModel : PageModel
{
    private readonly IPollingQuestionService _pollingService;

    public CreateModel(IPollingQuestionService pollingService) => _pollingService = pollingService;

    [BindProperty]
    public PollingQuestionEditModel Input { get; set; } = new();

    public string? ErrorMessage { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        var (success, error) = await _pollingService.CreateAsync(Input);
        if (!success)
        {
            ErrorMessage = error;
            return Page();
        }
        return RedirectToPage("Index");
    }
}

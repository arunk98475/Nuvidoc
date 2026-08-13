using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Admin;

public class IndexModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Admin/Dashboard/Index");
}

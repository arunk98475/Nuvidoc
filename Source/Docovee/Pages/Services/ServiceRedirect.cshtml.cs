using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Services;

/// <summary>
/// Redirects legacy/mistyped /service/{slug} to the real /services/{slug} route.
/// </summary>
public class ServiceRedirectModel : PageModel
{
    public IActionResult OnGet(string slug) =>
        RedirectPermanent($"/services/{slug}");
}

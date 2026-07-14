using Docovee.BLL.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Doctor;

[Authorize(Roles = AuthRoles.Doctor)]
public class FeaturesModel : PageModel
{
    public void OnGet()
    {
    }
}

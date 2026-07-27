using Docovee.BLL.Services;
using Docovee.DS.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Admin.Content;

public class IndexModel : PageModel
{
    private readonly IContentPageService _content;

    public IndexModel(IContentPageService content) => _content = content;

    public IReadOnlyList<ContentPage> Pages { get; set; } = Array.Empty<ContentPage>();
    public string TypeFilter { get; set; } = "";

    public async Task OnGetAsync(string? type)
    {
        TypeFilter = type ?? "";
        var all = await _content.GetAllAsync();
        Pages = string.IsNullOrWhiteSpace(TypeFilter)
            ? all
            : all.Where(p => p.PageType == TypeFilter).ToList();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        await _content.DeleteAsync(id);
        return RedirectToPage();
    }
}

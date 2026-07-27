using Docovee.BLL.Services;
using Docovee.DS.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Services;

public class ServicePageModel : PageModel
{
    private readonly IContentPageService _content;

    public ServicePageModel(IContentPageService content) => _content = content;

    public ContentPage? Post { get; set; }

    public async Task<IActionResult> OnGetAsync(string slug)
    {
        Post = await _content.GetBySlugAsync(slug);
        if (Post is null || !Post.IsPublished || Post.PageType != "service")
            return NotFound();

        return Page();
    }
}

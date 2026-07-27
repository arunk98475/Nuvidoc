using Docovee.BLL.Services;
using Docovee.DS.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Blog;

public class PostModel : PageModel
{
    private readonly IContentPageService _content;

    public PostModel(IContentPageService content) => _content = content;

    public ContentPage? Post { get; set; }

    public async Task<IActionResult> OnGetAsync(string slug)
    {
        Post = await _content.GetBySlugAsync(slug);
        if (Post is null || !Post.IsPublished || Post.PageType != "blog")
            return NotFound();

        return Page();
    }
}

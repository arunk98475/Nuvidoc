using Docovee.BLL.Services;
using Docovee.DS.Entities;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Docovee.Pages.Blog;

public class IndexModel : PageModel
{
    private readonly IContentPageService _content;

    public IndexModel(IContentPageService content) => _content = content;

    public IReadOnlyList<ContentPage> Posts { get; set; } = Array.Empty<ContentPage>();

    public async Task OnGetAsync() =>
        Posts = await _content.GetPublishedByTypeAsync("blog");
}

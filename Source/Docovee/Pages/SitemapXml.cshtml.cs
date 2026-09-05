using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Docovee.BLL.Configuration;
using Docovee.BLL.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Docovee.Pages;

public class SitemapXmlModel : PageModel
{
    private readonly IContentPageService _content;
    private readonly EmailOptions _email;

    public SitemapXmlModel(IContentPageService content, IOptions<EmailOptions> email)
    {
        _content = content;
        _email = email.Value;
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var baseUrl = ResolveBaseUrl();
        var urls = new List<(string Loc, DateTime? LastMod, string Changefreq, string Priority)>();

        void Add(string path, DateTime? lastMod = null, string changefreq = "weekly", string priority = "0.7")
            => urls.Add(($"{baseUrl}{path}", lastMod, changefreq, priority));

        Add("/", changefreq: "daily", priority: "1.0");
        Add("/services", priority: "0.9");
        Add("/blog", priority: "0.8");
        Add("/Privacy", changefreq: "yearly", priority: "0.3");
        Add("/Legal/CommunityStandards", changefreq: "yearly", priority: "0.3");
        Add("/Account/Register/Doctor", changefreq: "monthly", priority: "0.6");
        Add("/sitemap", changefreq: "monthly", priority: "0.2");

        var services = await _content.GetPublishedByTypeAsync("service", ct);
        foreach (var page in services)
            Add($"/services/{page.Slug}", page.UpdatedAtUtc, "weekly", "0.8");

        var posts = await _content.GetPublishedByTypeAsync("blog", ct);
        foreach (var page in posts)
            Add($"/blog/{page.Slug}", page.UpdatedAtUtc, "monthly", "0.6");

        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var urlset = new XElement(ns + "urlset",
            urls.Select(u =>
            {
                var el = new XElement(ns + "url",
                    new XElement(ns + "loc", u.Loc),
                    new XElement(ns + "changefreq", u.Changefreq),
                    new XElement(ns + "priority", u.Priority));
                if (u.LastMod.HasValue)
                {
                    el.Add(new XElement(ns + "lastmod",
                        u.LastMod.Value.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
                }
                return el;
            }));

        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), urlset);
        using var ms = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            OmitXmlDeclaration = false
        };
        using (var writer = XmlWriter.Create(ms, settings))
            doc.Save(writer);

        var xml = Encoding.UTF8.GetString(ms.ToArray());
        return Content(xml, "application/xml; charset=utf-8");
    }

    private string ResolveBaseUrl()
    {
        var configured = (_email.PublicBaseUrl ?? "").Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var req = HttpContext.Request;
        return $"{req.Scheme}://{req.Host.Value}".TrimEnd('/');
    }
}

using Docovee.BLL.Configuration;
using Docovee.BLL.Services;
using Docovee.DS;
using Docovee.DS.Entities;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Data;

/// <summary>
/// Seeds Houston-first dentist messaging on the homepage and SEO service landing pages.
/// Runs once (gated by AppSettingKeys.HoustonMarketingSeeded).
/// </summary>
public static class HoustonMarketingSeed
{
    public static async Task EnsureAsync(DocoveeDbContext db, string siteName = "NuviDoc", string chatBotName = "Nuvi", CancellationToken ct = default)
    {
        var flag = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == AppSettingKeys.HoustonMarketingSeeded, ct);
        if (flag?.Value == "1")
        {
            // Still ensure SEO pages exist even if homepage was already seeded
            await EnsureSeoPagesAsync(db, siteName, chatBotName, ct);
            return;
        }

        await UpsertHomePageAsync(db, siteName, chatBotName, ct);
        await EnsureSeoPagesAsync(db, siteName, chatBotName, ct);

        if (flag is null)
        {
            db.AppSettings.Add(new AppSetting
            {
                Key = AppSettingKeys.HoustonMarketingSeeded,
                Value = "1"
            });
        }
        else
        {
            flag.Value = "1";
        }

        await db.SaveChangesAsync(ct);
        Console.WriteLine("[NuviDoc DB] Houston marketing homepage + SEO pages seeded.");
    }

    private static async Task UpsertHomePageAsync(DocoveeDbContext db, string siteName, string chatBotName, CancellationToken ct)
    {
        var defaults = HomePageContentService.Defaults(siteName, chatBotName);
        var row = await db.HomePageContents.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (row is null)
        {
            row = new HomePageContent { Id = 1 };
            db.HomePageContents.Add(row);
        }

        row.MetaDescription = defaults.MetaDescription;
        row.HeroEyebrow = defaults.HeroEyebrow;
        row.HeroHeadlineHtml = defaults.HeroHeadlineHtml;
        row.HeroSubtext = defaults.HeroSubtext;
        row.Stat1Num = defaults.Stat1Num;
        row.Stat1Label = defaults.Stat1Label;
        row.Stat2Num = defaults.Stat2Num;
        row.Stat2Label = defaults.Stat2Label;
        row.Stat3Num = defaults.Stat3Num;
        row.Stat3Label = defaults.Stat3Label;
        row.Stat4Num = defaults.Stat4Num;
        row.Stat4Label = defaults.Stat4Label;
        row.InsuranceTitle = defaults.InsuranceTitle;
        row.WhyEyebrow = defaults.WhyEyebrow;
        row.WhyHeadlineHtml = defaults.WhyHeadlineHtml;
        row.Why1Title = defaults.Why1Title;
        row.Why1Body = defaults.Why1Body;
        row.Why2Title = defaults.Why2Title;
        row.Why2Body = defaults.Why2Body;
        row.Why3Title = defaults.Why3Title;
        row.Why3Body = defaults.Why3Body;
        row.VisitEyebrow = defaults.VisitEyebrow;
        row.VisitHeadlineHtml = defaults.VisitHeadlineHtml;
        row.SpecialtyEyebrow = defaults.SpecialtyEyebrow;
        row.SpecialtyHeadlineHtml = defaults.SpecialtyHeadlineHtml;
        row.SpecialtyBody = defaults.SpecialtyBody;
        row.DoctorsEyebrow = defaults.DoctorsEyebrow;
        row.DoctorsHeadlineHtml = defaults.DoctorsHeadlineHtml;
        row.DoctorsSubtitle = defaults.DoctorsSubtitle;
        row.HowEyebrow = defaults.HowEyebrow;
        row.HowHeadlineHtml = defaults.HowHeadlineHtml;
        row.How1Title = defaults.How1Title;
        row.How1Body = defaults.How1Body;
        row.How2Title = defaults.How2Title;
        row.How2Body = defaults.How2Body;
        row.How3Title = defaults.How3Title;
        row.How3Body = defaults.How3Body;
        row.CtaEyebrow = defaults.CtaEyebrow;
        row.CtaHeadlineHtml = defaults.CtaHeadlineHtml;
        row.CtaSubtext = defaults.CtaSubtext;
        row.CtaButtonText = defaults.CtaButtonText;
        row.CtaNote = defaults.CtaNote;
        row.UpdatedAtUtc = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    private static async Task EnsureSeoPagesAsync(DocoveeDbContext db, string siteName, string chatBotName, CancellationToken ct)
    {
        var pages = BuildSeoPages(siteName, chatBotName);
        foreach (var page in pages)
        {
            var existing = await db.ContentPages.FirstOrDefaultAsync(p => p.Slug == page.Slug, ct);
            if (existing is null)
            {
                db.ContentPages.Add(page);
            }
            else if (!existing.IsPublished)
            {
                // Re-publish seeded SEO pages if they were left as draft
                existing.IsPublished = true;
                existing.UpdatedAtUtc = DateTime.UtcNow;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static List<ContentPage> BuildSeoPages(string siteName, string chatBotName) =>
    [
        new ContentPage
        {
            PageType = "service",
            Slug = "emergency-dentist-houston",
            Title = "Emergency Dentist in Houston",
            MetaDescription = "Need an emergency dentist in Houston? Find same-day urgent dental care near you with " + siteName + " — free for patients.",
            Excerpt = "Tooth pain, broken tooth, or dental emergency? Match with a Houston emergency dentist fast.",
            IsPublished = true,
            BodyHtml = $"""
                <h2>Emergency dentist near you in Houston</h2>
                <p>When you have severe tooth pain, a knocked-out tooth, swelling, or a dental injury, you need an <strong>emergency dentist in Houston</strong> — not a waiting list. {siteName} helps you find urgent care options across the Houston metro (Baytown to Katy).</p>
                <h2>When to seek emergency dental care</h2>
                <ul>
                  <li>Severe or throbbing tooth pain</li>
                  <li>Swelling in the face or gums</li>
                  <li>Broken, cracked, or knocked-out tooth</li>
                  <li>Abscess or infection signs</li>
                  <li>Lost filling or crown with pain</li>
                </ul>
                <h2>How {siteName} helps</h2>
                <p>Tell {chatBotName} what's going on. We'll match you with Houston dentists who can help with urgent needs. Call the office directly from the profile, or request a visit online when booking is available.</p>
                <p><a href="/">Find an emergency dentist in Houston →</a></p>
                """,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        },
        new ContentPage
        {
            PageType = "service",
            Slug = "same-day-dentist-houston",
            Title = "Same-Day Dentist in Houston",
            MetaDescription = "Looking for same-day dentistry in Houston? Find dentists with open availability for same-day treatment and care with " + siteName + ".",
            Excerpt = "Same-day dental visits in Houston — cleanings, fillings, and urgent care when you need it today.",
            IsPublished = true,
            BodyHtml = $"""
                <h2>Same-day dentistry in Houston</h2>
                <p>Busy schedule? {siteName} connects you with Houston dentists who offer <strong>same-day treatment</strong> and same-day care — so you don't wait weeks for a simple visit.</p>
                <h2>Common same-day appointments</h2>
                <ul>
                  <li>Same-day dental exam &amp; cleaning</li>
                  <li>Urgent tooth pain relief</li>
                  <li>Broken tooth or lost filling</li>
                  <li>New patient same-day openings</li>
                </ul>
                <h2>Find a same-day dentist near me</h2>
                <p>Tell {chatBotName} you need a same-day visit. We'll prioritize Houston matches who can see you sooner.</p>
                <p><a href="/">Find a same-day Houston dentist →</a></p>
                """,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        },
        new ContentPage
        {
            PageType = "service",
            Slug = "dentist-near-me-houston",
            Title = "Dentist Near Me in Houston",
            MetaDescription = "Find a dentist near me in Houston. " + siteName + " matches you with convenient local dentists across Houston ZIP codes — free for patients.",
            Excerpt = "Convenient Houston dentists near your ZIP — match by location, insurance, and what you need.",
            IsPublished = true,
            BodyHtml = $"""
                <h2>Dentist near me — Houston</h2>
                <p>Searching “dentist near me”? {siteName} is built for Houston convenience: match by ZIP, visit reason, and preferences across the Baytown–Katy corridor.</p>
                <h2>Why local matters</h2>
                <ul>
                  <li>Less drive time for cleanings and follow-ups</li>
                  <li>Easier same-day or emergency access</li>
                  <li>Dentists who know Houston insurance networks</li>
                </ul>
                <h2>Start with your ZIP</h2>
                <p>Chat with {chatBotName}, share your Houston ZIP (or skip), and get matched with nearby dentists you can call or book.</p>
                <p><a href="/">Find a dentist near me in Houston →</a></p>
                """,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        },
        new ContentPage
        {
            PageType = "service",
            Slug = "best-dentists-houston",
            Title = "Best Dentists in Houston",
            MetaDescription = "Discover some of the best dentists in Houston. " + siteName + " matches you with top-rated local dentists based on your needs — free for patients.",
            Excerpt = "Compare highly rated Houston dentists and find the best fit for your smile — not just the loudest ad.",
            IsPublished = true,
            BodyHtml = $"""
                <h2>Best dentists in Houston</h2>
                <p>As a dental marketplace, {siteName} helps patients find <strong>best dentists in Houston</strong> matched to their situation — reviews, specialty, location, and availability — not paid placement alone.</p>
                <h2>What “best” means for you</h2>
                <ul>
                  <li>Strong patient reviews and ratings</li>
                  <li>Right specialty (general, implants, cosmetic, pediatric)</li>
                  <li>Convenient Houston location</li>
                  <li>Insurance and visit-reason fit</li>
                </ul>
                <h2>Find your best match</h2>
                <p>Tell {chatBotName} what you need. We'll recommend Houston dentists ranked by fit.</p>
                <p><a href="/">Find the best dentist in Houston →</a></p>
                """,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        },
        new ContentPage
        {
            PageType = "service",
            Slug = "dental-implants-houston",
            Title = "Best Implant Dentist in Houston",
            MetaDescription = "Looking for the best implant dentist in Houston? Find dental implant specialists near you with " + siteName + " — free for patients.",
            Excerpt = "Dental implants in Houston — match with implant-focused dentists for consults and treatment.",
            IsPublished = true,
            BodyHtml = $"""
                <h2>Dental implants in Houston</h2>
                <p>Need implants or a consult with an experienced implant dentist? {siteName} matches you with Houston providers who offer implant dentistry.</p>
                <h2>Why patients choose implant specialists</h2>
                <ul>
                  <li>Replace missing teeth with a lasting solution</li>
                  <li>Restore chewing and confidence</li>
                  <li>Work with dentists experienced in implant workflows</li>
                </ul>
                <h2>Find an implant dentist near you</h2>
                <p>Tell {chatBotName} you're interested in dental implants. We'll match Houston dentists who list implants as a focus.</p>
                <p><a href="/">Find an implant dentist in Houston →</a></p>
                """,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        }
    ];
}

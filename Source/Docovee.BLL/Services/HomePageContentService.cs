using Docovee.DS;
using Docovee.DS.Entities;
using Docovee.DS.Models;
using Microsoft.EntityFrameworkCore;

namespace Docovee.BLL.Services;

public interface IHomePageContentService
{
    Task<HomePageContentModel> GetResolvedAsync(CancellationToken ct = default);
    Task<HomePageContentModel> GetForEditAsync(CancellationToken ct = default);
    Task SaveAsync(HomePageContentModel model, CancellationToken ct = default);
}

public class HomePageContentService : IHomePageContentService
{
    private readonly DocoveeDbContext _db;

    public HomePageContentService(DocoveeDbContext db) => _db = db;

    public static HomePageContentModel Defaults(string siteName, string chatBotName) => new()
    {
        MetaDescription = $"Find the right dentist near you. {siteName} matches you with top-rated local dentists based on your needs.",
        HeroEyebrow = "Find the Best Doctor Near Me · Book Online Today",
        HeroHeadlineHtml = "Find a doctor<br>you can <em>trust.</em>",
        HeroSubtext = $"Tell {chatBotName} what's going on. Your personal AI concierge finds the doctor who truly fits you — not just any doctor, the right one.",

        Stat1Num = "3 min",
        Stat1Label = "Average time to match",
        Stat2Num = "Growing",
        Stat2Label = "Dentist network across the US",
        Stat3Num = "98%",
        Stat3Label = "Patient satisfaction rate",
        Stat4Num = "$0",
        Stat4Label = "Cost to patients — always free",

        InsuranceTitle = "Works with major dental insurance plans",

        WhyEyebrow = $"Why {siteName}",
        WhyHeadlineHtml = "Finding a dentist shouldn't feel like<br><em>searching in the dark.</em>",
        Why1Title = "Matched, not just listed",
        Why1Body = $"Most finders show you a list. {siteName} understands your situation and finds the dentist who's actually right for you — needs, location, and preferences.",
        Why2Title = "Book in minutes, not days",
        Why2Body = "No hold music. No calling three offices. See real availability and request a visit instantly.",
        Why3Title = "Reviews you can trust",
        Why3Body = "Reviews from verified patients who actually visited. We track what matters: did they listen, were they on time, would patients go back.",

        VisitEyebrow = "Common Visit Reasons",
        VisitHeadlineHtml = "What brings you in <em>today?</em>",

        SpecialtyEyebrow = "Dental care",
        SpecialtyHeadlineHtml = "Dentists.<br><em>One place.</em>",
        SpecialtyBody = $"We're focused on dentistry first. Tell {chatBotName} what you need — general care, ortho, implants, or emergency relief — and we'll match you.",

        DoctorsEyebrow = "Top Dentists",
        DoctorsHeadlineHtml = "Real dentists.<br><em>Real results.</em>",
        DoctorsSubtitle = "Highly rated providers in our network — tap a card to view profile, contact info, and reviews.",

        HowEyebrow = "No more guessing",
        HowHeadlineHtml = "We do the work.<br><em>You just show up.</em>",
        How1Title = "Tell us what's wrong",
        How1Body = "Describe what's going on in your own words — “my molar hurts when I bite” works perfectly.",
        How2Title = "We find your best match",
        How2Body = "A few quick follow-ups — urgency, location, preferences — then we rank dentists by fit. Not by who paid the most.",
        How3Title = "Book and we handle the rest",
        How3Body = "Request a visit online. The office confirms, and you're on the calendar.",

        CtaEyebrow = "Start Now — It's Free",
        CtaHeadlineHtml = "The right dentist is<br><em>closer than you think.</em>",
        CtaSubtext = "Tell us what's going on. We'll handle matching and booking. Always free for patients.",
        CtaButtonText = "Find My Dentist Now →",
        CtaNote = "Free for patients · No account required to start",
    };

    public async Task<HomePageContentModel> GetResolvedAsync(CancellationToken ct = default)
    {
        var saved = await GetForEditAsync(ct);
        return MergeWithDefaults(saved, Defaults("NuviDoc", "Nuvi"));
    }

    public async Task<HomePageContentModel> GetForEditAsync(CancellationToken ct = default)
    {
        try
        {
            var row = await _db.HomePageContents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1, ct);
            if (row is null) return new HomePageContentModel();
            return MapFromEntity(row);
        }
        catch (Exception ex) when (IsMissingTable(ex))
        {
            return new HomePageContentModel();
        }
    }

    public async Task SaveAsync(HomePageContentModel model, CancellationToken ct = default)
    {
        try
        {
            var row = await _db.HomePageContents.FirstOrDefaultAsync(x => x.Id == 1, ct);
            if (row is null)
            {
                row = new HomePageContent { Id = 1 };
                _db.HomePageContents.Add(row);
            }

            ApplyToEntity(row, model);
            row.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsMissingTable(ex))
        {
            throw new InvalidOperationException(
                "The home_page_content table is missing. Restart the app so schema startup can create it, then try again.",
                ex);
        }
    }

    public static HomePageContentModel Resolve(HomePageContentModel saved, string siteName, string chatBotName) =>
        MergeWithDefaults(saved, Defaults(siteName, chatBotName));

    private static HomePageContentModel MapFromEntity(HomePageContent row) => new()
    {
        MetaDescription = row.MetaDescription ?? "",
        HeroEyebrow = row.HeroEyebrow ?? "",
        HeroHeadlineHtml = row.HeroHeadlineHtml ?? "",
        HeroSubtext = row.HeroSubtext ?? "",
        Stat1Num = row.Stat1Num ?? "",
        Stat1Label = row.Stat1Label ?? "",
        Stat2Num = row.Stat2Num ?? "",
        Stat2Label = row.Stat2Label ?? "",
        Stat3Num = row.Stat3Num ?? "",
        Stat3Label = row.Stat3Label ?? "",
        Stat4Num = row.Stat4Num ?? "",
        Stat4Label = row.Stat4Label ?? "",
        InsuranceTitle = row.InsuranceTitle ?? "",
        WhyEyebrow = row.WhyEyebrow ?? "",
        WhyHeadlineHtml = row.WhyHeadlineHtml ?? "",
        Why1Title = row.Why1Title ?? "",
        Why1Body = row.Why1Body ?? "",
        Why2Title = row.Why2Title ?? "",
        Why2Body = row.Why2Body ?? "",
        Why3Title = row.Why3Title ?? "",
        Why3Body = row.Why3Body ?? "",
        VisitEyebrow = row.VisitEyebrow ?? "",
        VisitHeadlineHtml = row.VisitHeadlineHtml ?? "",
        SpecialtyEyebrow = row.SpecialtyEyebrow ?? "",
        SpecialtyHeadlineHtml = row.SpecialtyHeadlineHtml ?? "",
        SpecialtyBody = row.SpecialtyBody ?? "",
        DoctorsEyebrow = row.DoctorsEyebrow ?? "",
        DoctorsHeadlineHtml = row.DoctorsHeadlineHtml ?? "",
        DoctorsSubtitle = row.DoctorsSubtitle ?? "",
        HowEyebrow = row.HowEyebrow ?? "",
        HowHeadlineHtml = row.HowHeadlineHtml ?? "",
        How1Title = row.How1Title ?? "",
        How1Body = row.How1Body ?? "",
        How2Title = row.How2Title ?? "",
        How2Body = row.How2Body ?? "",
        How3Title = row.How3Title ?? "",
        How3Body = row.How3Body ?? "",
        CtaEyebrow = row.CtaEyebrow ?? "",
        CtaHeadlineHtml = row.CtaHeadlineHtml ?? "",
        CtaSubtext = row.CtaSubtext ?? "",
        CtaButtonText = row.CtaButtonText ?? "",
        CtaNote = row.CtaNote ?? "",
    };

    private static void ApplyToEntity(HomePageContent row, HomePageContentModel model)
    {
        row.MetaDescription = NullIfEmpty(model.MetaDescription);
        row.HeroEyebrow = NullIfEmpty(model.HeroEyebrow);
        row.HeroHeadlineHtml = NullIfEmpty(model.HeroHeadlineHtml);
        row.HeroSubtext = NullIfEmpty(model.HeroSubtext);
        row.Stat1Num = NullIfEmpty(model.Stat1Num);
        row.Stat1Label = NullIfEmpty(model.Stat1Label);
        row.Stat2Num = NullIfEmpty(model.Stat2Num);
        row.Stat2Label = NullIfEmpty(model.Stat2Label);
        row.Stat3Num = NullIfEmpty(model.Stat3Num);
        row.Stat3Label = NullIfEmpty(model.Stat3Label);
        row.Stat4Num = NullIfEmpty(model.Stat4Num);
        row.Stat4Label = NullIfEmpty(model.Stat4Label);
        row.InsuranceTitle = NullIfEmpty(model.InsuranceTitle);
        row.WhyEyebrow = NullIfEmpty(model.WhyEyebrow);
        row.WhyHeadlineHtml = NullIfEmpty(model.WhyHeadlineHtml);
        row.Why1Title = NullIfEmpty(model.Why1Title);
        row.Why1Body = NullIfEmpty(model.Why1Body);
        row.Why2Title = NullIfEmpty(model.Why2Title);
        row.Why2Body = NullIfEmpty(model.Why2Body);
        row.Why3Title = NullIfEmpty(model.Why3Title);
        row.Why3Body = NullIfEmpty(model.Why3Body);
        row.VisitEyebrow = NullIfEmpty(model.VisitEyebrow);
        row.VisitHeadlineHtml = NullIfEmpty(model.VisitHeadlineHtml);
        row.SpecialtyEyebrow = NullIfEmpty(model.SpecialtyEyebrow);
        row.SpecialtyHeadlineHtml = NullIfEmpty(model.SpecialtyHeadlineHtml);
        row.SpecialtyBody = NullIfEmpty(model.SpecialtyBody);
        row.DoctorsEyebrow = NullIfEmpty(model.DoctorsEyebrow);
        row.DoctorsHeadlineHtml = NullIfEmpty(model.DoctorsHeadlineHtml);
        row.DoctorsSubtitle = NullIfEmpty(model.DoctorsSubtitle);
        row.HowEyebrow = NullIfEmpty(model.HowEyebrow);
        row.HowHeadlineHtml = NullIfEmpty(model.HowHeadlineHtml);
        row.How1Title = NullIfEmpty(model.How1Title);
        row.How1Body = NullIfEmpty(model.How1Body);
        row.How2Title = NullIfEmpty(model.How2Title);
        row.How2Body = NullIfEmpty(model.How2Body);
        row.How3Title = NullIfEmpty(model.How3Title);
        row.How3Body = NullIfEmpty(model.How3Body);
        row.CtaEyebrow = NullIfEmpty(model.CtaEyebrow);
        row.CtaHeadlineHtml = NullIfEmpty(model.CtaHeadlineHtml);
        row.CtaSubtext = NullIfEmpty(model.CtaSubtext);
        row.CtaButtonText = NullIfEmpty(model.CtaButtonText);
        row.CtaNote = NullIfEmpty(model.CtaNote);
    }

    private static bool IsMissingTable(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException!)
        {
            var msg = e.Message ?? "";
            if (msg.Contains("home_page_content", StringComparison.OrdinalIgnoreCase)
                && (msg.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("does not exist", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private static HomePageContentModel MergeWithDefaults(HomePageContentModel saved, HomePageContentModel d) => new()
    {
        MetaDescription = Coalesce(saved.MetaDescription, d.MetaDescription),
        HeroEyebrow = Coalesce(saved.HeroEyebrow, d.HeroEyebrow),
        HeroHeadlineHtml = Coalesce(saved.HeroHeadlineHtml, d.HeroHeadlineHtml),
        HeroSubtext = Coalesce(saved.HeroSubtext, d.HeroSubtext),
        Stat1Num = Coalesce(saved.Stat1Num, d.Stat1Num),
        Stat1Label = Coalesce(saved.Stat1Label, d.Stat1Label),
        Stat2Num = Coalesce(saved.Stat2Num, d.Stat2Num),
        Stat2Label = Coalesce(saved.Stat2Label, d.Stat2Label),
        Stat3Num = Coalesce(saved.Stat3Num, d.Stat3Num),
        Stat3Label = Coalesce(saved.Stat3Label, d.Stat3Label),
        Stat4Num = Coalesce(saved.Stat4Num, d.Stat4Num),
        Stat4Label = Coalesce(saved.Stat4Label, d.Stat4Label),
        InsuranceTitle = Coalesce(saved.InsuranceTitle, d.InsuranceTitle),
        WhyEyebrow = Coalesce(saved.WhyEyebrow, d.WhyEyebrow),
        WhyHeadlineHtml = Coalesce(saved.WhyHeadlineHtml, d.WhyHeadlineHtml),
        Why1Title = Coalesce(saved.Why1Title, d.Why1Title),
        Why1Body = Coalesce(saved.Why1Body, d.Why1Body),
        Why2Title = Coalesce(saved.Why2Title, d.Why2Title),
        Why2Body = Coalesce(saved.Why2Body, d.Why2Body),
        Why3Title = Coalesce(saved.Why3Title, d.Why3Title),
        Why3Body = Coalesce(saved.Why3Body, d.Why3Body),
        VisitEyebrow = Coalesce(saved.VisitEyebrow, d.VisitEyebrow),
        VisitHeadlineHtml = Coalesce(saved.VisitHeadlineHtml, d.VisitHeadlineHtml),
        SpecialtyEyebrow = Coalesce(saved.SpecialtyEyebrow, d.SpecialtyEyebrow),
        SpecialtyHeadlineHtml = Coalesce(saved.SpecialtyHeadlineHtml, d.SpecialtyHeadlineHtml),
        SpecialtyBody = Coalesce(saved.SpecialtyBody, d.SpecialtyBody),
        DoctorsEyebrow = Coalesce(saved.DoctorsEyebrow, d.DoctorsEyebrow),
        DoctorsHeadlineHtml = Coalesce(saved.DoctorsHeadlineHtml, d.DoctorsHeadlineHtml),
        DoctorsSubtitle = Coalesce(saved.DoctorsSubtitle, d.DoctorsSubtitle),
        HowEyebrow = Coalesce(saved.HowEyebrow, d.HowEyebrow),
        HowHeadlineHtml = Coalesce(saved.HowHeadlineHtml, d.HowHeadlineHtml),
        How1Title = Coalesce(saved.How1Title, d.How1Title),
        How1Body = Coalesce(saved.How1Body, d.How1Body),
        How2Title = Coalesce(saved.How2Title, d.How2Title),
        How2Body = Coalesce(saved.How2Body, d.How2Body),
        How3Title = Coalesce(saved.How3Title, d.How3Title),
        How3Body = Coalesce(saved.How3Body, d.How3Body),
        CtaEyebrow = Coalesce(saved.CtaEyebrow, d.CtaEyebrow),
        CtaHeadlineHtml = Coalesce(saved.CtaHeadlineHtml, d.CtaHeadlineHtml),
        CtaSubtext = Coalesce(saved.CtaSubtext, d.CtaSubtext),
        CtaButtonText = Coalesce(saved.CtaButtonText, d.CtaButtonText),
        CtaNote = Coalesce(saved.CtaNote, d.CtaNote),
    };

    private static string Coalesce(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

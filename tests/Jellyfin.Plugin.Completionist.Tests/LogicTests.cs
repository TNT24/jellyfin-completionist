using System.Text.Json.Nodes;
using Jellyfin.Plugin.Completionist.Notifications;
using Jellyfin.Plugin.Completionist.Scanning;
using Xunit;

namespace Jellyfin.Plugin.Completionist.Tests;

public class MissingFinderTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
    private static readonly FinderOptions Defaults = new(IncludeSpecials: false, ReportWholeSeasons: true, AiredBefore: Now);

    private static GuideEpisode Ep(int s, int e, DateTime? aired) => new(s, e, $"S{s}E{e}", aired);

    private static DateTime Ago(int days) => Now.AddDays(-days);

    [Fact]
    public void FindsSingleMissingEpisode()
    {
        var guide = new[] { Ep(1, 1, Ago(30)), Ep(1, 2, Ago(23)), Ep(1, 3, Ago(16)) };
        var local = new[] { new LocalEpisode(1, 1), new LocalEpisode(1, 3) };

        var r = MissingFinder.Compare(local, guide, Defaults);

        Assert.False(r.NumberingMismatch);
        Assert.Empty(r.MissingSeasons);
        Assert.Equal((1, 2), (r.MissingEpisodes.Single().Season, r.MissingEpisodes.Single().Episode));
    }

    [Fact]
    public void UnairedEpisodesAreNotMissing()
    {
        var guide = new[] { Ep(1, 1, Ago(10)), Ep(1, 2, Now.AddDays(3)), Ep(1, 3, null) };
        var local = new[] { new LocalEpisode(1, 1) };

        Assert.Empty(MissingFinder.Compare(local, guide, Defaults).MissingEpisodes);
    }

    [Fact]
    public void UndatedEpisodeBeforeAnAiredOneCountsAsAired()
    {
        var guide = new[] { Ep(1, 1, Ago(100)), Ep(1, 2, null), Ep(1, 3, Ago(80)) };
        var local = new[] { new LocalEpisode(1, 1), new LocalEpisode(1, 3) };

        Assert.Equal(2, MissingFinder.Compare(local, guide, Defaults).MissingEpisodes.Single().Episode);
    }

    [Fact]
    public void WholeSeasonIsReportedOnce()
    {
        var guide = new[] { Ep(1, 1, Ago(400)), Ep(2, 1, Ago(30)), Ep(2, 2, Ago(23)) };
        var local = new[] { new LocalEpisode(1, 1) };

        var r = MissingFinder.Compare(local, guide, Defaults);

        Assert.Equal((2, 2), r.MissingSeasons.Single());
        Assert.Empty(r.MissingEpisodes);
    }

    [Fact]
    public void WholeSeasonsCanBeTurnedOff()
    {
        var guide = new[] { Ep(1, 1, Ago(400)), Ep(1, 2, Ago(390)), Ep(2, 1, Ago(30)) };
        var local = new[] { new LocalEpisode(1, 1) };

        var r = MissingFinder.Compare(local, guide, Defaults with { ReportWholeSeasons = false });

        Assert.Empty(r.MissingSeasons);
        Assert.Equal(2, r.MissingEpisodes.Single().Episode);
    }

    [Fact]
    public void GraceDaysDelayRecentEpisodes()
    {
        var guide = new[] { Ep(1, 1, Ago(10)), Ep(1, 2, Now.AddHours(-12)) };
        var local = new[] { new LocalEpisode(1, 1) };

        Assert.Empty(MissingFinder.Compare(local, guide, Defaults with { AiredBefore = Now.AddDays(-2) }).MissingEpisodes);
        Assert.Single(MissingFinder.Compare(local, guide, Defaults).MissingEpisodes);
    }

    [Fact]
    public void MultiEpisodeFileCoversItsRange()
    {
        var guide = new[] { Ep(1, 1, Ago(30)), Ep(1, 2, Ago(30)), Ep(1, 3, Ago(23)) };
        var local = new[] { new LocalEpisode(1, 1, 2), new LocalEpisode(1, 3) };

        Assert.Empty(MissingFinder.Compare(local, guide, Defaults).MissingEpisodes);
    }

    [Fact]
    public void SpecialsOnlyWhenAskedFor()
    {
        var guide = new[] { Ep(0, 1, Ago(50)), Ep(1, 1, Ago(30)) };
        var local = new[] { new LocalEpisode(1, 1), new LocalEpisode(0, 2) };

        Assert.Empty(MissingFinder.Compare(local, guide, Defaults).MissingEpisodes);
        Assert.Equal(0, MissingFinder.Compare(local, guide, Defaults with { IncludeSpecials = true }).MissingEpisodes.Single().Season);
    }

    [Fact]
    public void NothingMatchingMeansNumberingMismatch()
    {
        // Anime in absolute order: the library has S01E30..E32, the guide splits them into seasons.
        var guide = new[] { Ep(1, 1, Ago(400)), Ep(2, 1, Ago(300)), Ep(2, 2, Ago(290)) };
        var local = new[] { new LocalEpisode(1, 30), new LocalEpisode(1, 31), new LocalEpisode(1, 32) };

        var r = MissingFinder.Compare(local, guide, Defaults);

        Assert.True(r.NumberingMismatch);
        Assert.Empty(r.MissingEpisodes);
        Assert.Empty(r.MissingSeasons);
    }

    [Fact]
    public void GapsAreFoundWithoutAGuide()
    {
        var local = new[] { new LocalEpisode(1, 1), new LocalEpisode(1, 2), new LocalEpisode(1, 5), new LocalEpisode(2, 2) };

        var gaps = MissingFinder.FindGaps(local, false);

        Assert.Equal(new[] { (1, 3), (1, 4), (2, 1) }, gaps.Select(g => (g.Season, g.Episode)));
    }

    [Fact]
    public void RangesCollapse()
    {
        Assert.Equal(new[] { (1, 3), (5, 5), (7, 8) }, MissingFinder.ToRanges(new[] { 8, 1, 2, 3, 5, 7 }));
    }
}

public class NewItemTests
{
    private static readonly DateTime First = new(2026, 10, 1, 5, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 10, 27, 5, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Show = Guid.NewGuid();

    private static SeriesReport Report(IEnumerable<int> seasons, IEnumerable<(int S, int E)> episodes, DateTime seen) => new()
    {
        SeriesId = Show,
        Name = "Show",
        Source = "TVmaze",
        Seasons = seasons.Select(s => new MissingSeason { Season = s, EpisodeCount = 10, FirstSeen = seen }).ToList(),
        Episodes = episodes.Select(e => new MissingEpisode { Season = e.S, Episode = e.E, FirstSeen = seen }).ToList(),
    };

    [Fact]
    public void OnlyNewlyMissingItemsAreNew()
    {
        var previous = Report([], [(1, 2)], First);
        var current = Report([], [(1, 2), (1, 9)], default);

        CompletionistScanner.StampFirstSeen(current, previous, Now);
        var changes = CompletionistScanner.FindNew(new ScanReport { Series = [current] }, Now);

        Assert.Equal(First, current.Episodes[0].FirstSeen);
        Assert.Equal(9, changes.Single().Episodes.Single().Episode);
    }

    [Fact]
    public void EpisodesOfAWhollyMissingSeasonAreNotNew()
    {
        // Season 3 was missing as a whole; the user got half of it, so the rest is now listed per episode.
        var previous = Report([3], [], First);
        var current = Report([], [(3, 6), (3, 7)], default);

        CompletionistScanner.StampFirstSeen(current, previous, Now);

        Assert.Empty(CompletionistScanner.FindNew(new ScanReport { Series = [current] }, Now));
    }

    [Fact]
    public void FilledGapsAreResolved()
    {
        var previous = new ScanReport { Series = [Report([4], [(1, 2), (1, 3)], First)] };
        var current = new ScanReport { Series = [Report([], [(1, 3)], First)] };

        var resolved = CompletionistScanner.FindResolved(previous, current, [Show]);

        Assert.Equal(new[] { "Season 4", "S01E02" }, resolved.Single().Labels);
    }

    [Fact]
    public void ShowsNotCheckedThisTimeAreNotResolved()
    {
        var previous = new ScanReport { Series = [Report([], [(1, 2)], First)] };

        Assert.Empty(CompletionistScanner.FindResolved(previous, new ScanReport(), []));
    }
}

public class DiscordMessageTests
{
    private static SeriesChange Change(string name, int episodes)
    {
        var series = new SeriesReport { SeriesId = Guid.NewGuid(), Name = name, Year = 2020, Source = "TVmaze" };
        var eps = Enumerable.Range(1, episodes)
            .Select(e => new MissingEpisode { Season = 1, Episode = e, Title = "Episode " + e, AirDate = new DateTime(2026, 10, 20) })
            .ToList();
        return new SeriesChange(series, [], eps);
    }

    [Fact]
    public void NothingNewSendsNothing()
    {
        Assert.Empty(DiscordMessageBuilder.BuildChanges([], [], [], string.Empty));
    }

    [Fact]
    public void AllClearSaysWhatIsStillMissing()
    {
        var report = new ScanReport { SeriesChecked = 12, Series = [Change("Show", 3).Series] };
        report.Series[0].Episodes.AddRange(Change("Show", 3).Episodes);

        var content = DiscordMessageBuilder.BuildNothingNew(report)["content"]!.GetValue<string>();

        Assert.Contains("Nothing new missing", content);
        Assert.Contains("Checked 12 shows", content);
        Assert.Contains("Still missing from before: 3 episodes", content);
        Assert.Contains("Your library is complete", DiscordMessageBuilder.BuildNothingNew(new ScanReport { SeriesChecked = 1 })["content"]!.GetValue<string>());
    }

    [Fact]
    public void ShortListsNameEachEpisode()
    {
        var msg = DiscordMessageBuilder.BuildChanges([Change("Show", 2)], [], [], string.Empty).Single();
        var description = msg["embeds"]![0]!["description"]!.GetValue<string>();

        Assert.Contains("`S01E01` Episode 1 · aired 20 Oct 2026", description);
        Assert.Contains("2 new missing episodes", msg["content"]!.GetValue<string>());
    }

    [Fact]
    public void LongListsUseRanges()
    {
        var description = DiscordMessageBuilder.BuildChanges([Change("Show", 20)], [], [], string.Empty)
            .Single()["embeds"]![0]!["description"]!.GetValue<string>();

        Assert.Equal("**Season 1**: E01–E20 (20 episodes)", description);
    }

    [Fact]
    public void ManyShowsAreSplitAcrossMessages()
    {
        var changes = Enumerable.Range(1, 25).Select(i => Change("Show " + i, 3)).ToList();

        var messages = DiscordMessageBuilder.BuildChanges(changes, [], [], string.Empty);

        Assert.Equal(3, messages.Count);
        Assert.All(messages, m => Assert.True(((JsonArray)m["embeds"]!).Count <= 10));
        Assert.NotNull(messages[0]["content"]);
        Assert.Null(messages[1]["content"]);
    }

    [Fact]
    public void PublicUrlAddsLinkAndPoster()
    {
        var change = Change("Show", 1);
        var embed = DiscordMessageBuilder.BuildChanges([change], [], [], "https://jf.example.com/").Single()["embeds"]![0]!;
        var id = change.Series.SeriesId.ToString("N");

        Assert.Equal($"https://jf.example.com/web/#/details?id={id}", embed["url"]!.GetValue<string>());
        Assert.Equal($"https://jf.example.com/Items/{id}/Images/Primary?maxHeight=300", embed["thumbnail"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public void NobodyGetsPinged()
    {
        var msg = DiscordMessageBuilder.BuildChanges([Change("@everyone", 1)], [], [], string.Empty).Single();
        Assert.Empty((JsonArray)msg["allowed_mentions"]!["parse"]!);
    }

    [Theory]
    [InlineData("https://discord.com/api/webhooks/123/abc", true)]
    [InlineData("https://discordapp.com/api/webhooks/123/abc", true)]
    [InlineData("http://discord.com/api/webhooks/123/abc", false)]
    [InlineData("https://example.com/api/webhooks/123/abc", false)]
    [InlineData("https://discord.com.evil.net/api/webhooks/1/a", false)]
    public void OnlyDiscordWebhooksAreAccepted(string url, bool ok)
    {
        Assert.Equal(ok, DiscordNotifier.IsWebhookUrl(url));
    }
}

public class GuideParsingTests
{
    [Fact]
    public void TvmazeSpecialsWithoutNumbersAreSkipped()
    {
        const string Json = """
            [
              {"name":"Pilot","season":1,"number":1,"airstamp":"2008-01-21T03:00:00+00:00","airdate":"2008-01-20"},
              {"name":"Special","season":5,"number":null,"airstamp":"2013-08-01T00:00:00+00:00"},
              {"name":"TBA","season":6,"number":1,"airstamp":null,"airdate":""}
            ]
            """;

        var eps = GuideClient.ParseTvmazeEpisodes(Json);

        Assert.Equal(2, eps.Count);
        Assert.Equal(new DateTime(2008, 1, 21, 3, 0, 0, DateTimeKind.Utc), eps[0].AirDate);
        Assert.Null(eps[1].AirDate);
    }

    [Fact]
    public void TmdbSeasonIsParsed()
    {
        const string Json = """{"episodes":[{"season_number":0,"episode_number":3,"name":"Behind the scenes","air_date":"2015-05-01"}]}""";

        var ep = GuideClient.ParseTmdbSeason(Json).Single();

        Assert.Equal((0, 3, "Behind the scenes"), (ep.Season, ep.Episode, ep.Title));
        Assert.Equal(new DateTime(2015, 5, 1, 0, 0, 0, DateTimeKind.Utc), ep.AirDate);
    }
}

public class FilmTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

    private static readonly CollectionPart[] Trilogy =
    [
        new(1, "One", new DateTime(2001, 1, 1)),
        new(2, "Two", new DateTime(2004, 1, 1)),
        new(3, "Three", new DateTime(2007, 1, 1)),
        new(4, "Four (in cinemas)", Now.AddDays(-20)),
        new(5, "Five (announced)", null),
    ];

    [Fact]
    public void OnlyReleasedFilmsYouDontOwnAreMissing()
    {
        var missing = MissingFinder.FindMissingFilms(Trilogy, new HashSet<int> { 1, 3 }, new HashSet<int>(), Now.AddDays(-90));

        Assert.Equal(new[] { 2 }, missing.Select(m => m.TmdbId));
    }

    [Fact]
    public void ZeroGraceIncludesFilmsStillInCinemas()
    {
        var missing = MissingFinder.FindMissingFilms(Trilogy, new HashSet<int> { 1, 2, 3 }, new HashSet<int>(), Now);

        Assert.Equal(new[] { 4 }, missing.Select(m => m.TmdbId));
    }

    [Fact]
    public void IgnoredFilmsAreLeftOut()
    {
        Assert.Empty(MissingFinder.FindMissingFilms(Trilogy, new HashSet<int> { 1, 3 }, new HashSet<int> { 2 }, Now.AddDays(-90)));
    }

    [Fact]
    public void NewAndResolvedFilms()
    {
        var first = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var old = new CollectionReport
        {
            TmdbCollectionId = "10",
            Name = "Saga",
            Films = [new MissingFilm { TmdbId = 2, Title = "Two", FirstSeen = first }, new MissingFilm { TmdbId = 3, Title = "Three", FirstSeen = first }],
        };
        var current = new CollectionReport
        {
            TmdbCollectionId = "10",
            Name = "Saga",
            Films = [new MissingFilm { TmdbId = 2, Title = "Two" }, new MissingFilm { TmdbId = 4, Title = "Four" }],
        };

        CompletionistScanner.StampFirstSeen(current, old, Now);
        var report = new ScanReport { Collections = [current] };

        Assert.Equal(4, CompletionistScanner.FindNewFilms(report, Now).Single().Films.Single().TmdbId);
        Assert.Equal(new[] { "Three" }, CompletionistScanner.FindResolvedFilms(new ScanReport { Collections = [old] }, report, ["10"]).Single().Labels);
        Assert.Empty(CompletionistScanner.FindResolvedFilms(new ScanReport { Collections = [old] }, report, []));
    }

    [Fact]
    public void TmdbCollectionIsParsed()
    {
        const string Json = """{"id":10,"name":"Star Wars Collection","parts":[{"id":11,"title":"Star Wars","release_date":"1977-05-25"},{"id":99,"title":"Untitled","release_date":""}]}""";

        var c = GuideClient.ParseTmdbCollection(Json);

        Assert.Equal("Star Wars Collection", c.Name);
        Assert.Equal(new DateTime(1977, 5, 25, 0, 0, 0, DateTimeKind.Utc), c.Parts[0].ReleaseDate);
        Assert.Null(c.Parts[1].ReleaseDate);
    }

    [Fact]
    public void DiscordFilmMessageLinksToTmdbWithoutPublicUrl()
    {
        var collection = new CollectionReport
        {
            TmdbCollectionId = "10",
            Name = "Saga",
            OwnedCount = 2,
            ReleasedCount = 3,
            Films = [new MissingFilm { TmdbId = 2, Title = "Two", ReleaseDate = new DateTime(2004, 5, 1) }],
        };

        var msg = DiscordMessageBuilder.BuildChanges([], [new CollectionChange(collection, collection.Films)], [], string.Empty).Single();
        var embed = msg["embeds"]![0]!;

        Assert.Contains("1 new missing film** in 1 collection", msg["content"]!.GetValue<string>());
        Assert.Equal("`2004` Two · released 1 May 2004", embed["description"]!.GetValue<string>());
        Assert.Equal("https://www.themoviedb.org/collection/10", embed["url"]!.GetValue<string>());
        Assert.Equal("You have 2 of 3 released films", embed["footer"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void HeadlineCombinesShowsAndFilms()
    {
        var series = new SeriesReport { SeriesId = Guid.NewGuid(), Name = "Show", Source = "TVmaze" };
        var show = new SeriesChange(series, [], [new MissingEpisode { Season = 1, Episode = 2 }]);
        var collection = new CollectionReport { TmdbCollectionId = "10", Name = "Saga", Films = [new MissingFilm { TmdbId = 2, Title = "Two" }] };

        var msg = DiscordMessageBuilder.BuildChanges([show], [new CollectionChange(collection, collection.Films)], [], string.Empty).Single();

        Assert.Equal("📺 **1 new missing episode and 1 new missing film** in 1 show and 1 collection", msg["content"]!.GetValue<string>());
    }
}

public class WeeklySummaryTests
{
    private static readonly DateTime Now = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);

    private static ScanReport Report() => new()
    {
        SeriesChecked = 40,
        CollectionsChecked = 6,
        Series =
        [
            new SeriesReport
            {
                SeriesId = Guid.NewGuid(),
                Name = "Old Show",
                Year = 2001,
                Seasons = [new MissingSeason { Season = 3, EpisodeCount = 10, FirstSeen = Now.AddDays(-30) }],
                Episodes = [new MissingEpisode { Season = 1, Episode = 2, FirstSeen = Now.AddDays(-30) }],
            },
            new SeriesReport
            {
                SeriesId = Guid.NewGuid(),
                Name = "New Show",
                Episodes = [new MissingEpisode { Season = 2, Episode = 5, FirstSeen = Now.AddDays(-2) }, new MissingEpisode { Season = 2, Episode = 6, FirstSeen = Now.AddDays(-1) }],
            },
        ],
        Collections =
        [
            new CollectionReport { TmdbCollectionId = "1", Name = "Back to the Future Collection", OwnedCount = 2, ReleasedCount = 3, Films = [new MissingFilm { TmdbId = 2, Title = "Part II", FirstSeen = Now.AddDays(-3) }] },
        ],
        Filled =
        [
            new FilledEntry { Time = Now.AddDays(-1), Name = "Old Show", Items = 3, Seasons = 1 },
            new FilledEntry { Time = Now.AddDays(-2), Name = "Shrek Collection", IsCollection = true, Items = 1 },
            new FilledEntry { Time = Now.AddDays(-10), Name = "Too old", Items = 50 },
        ],
    };

    private static string Description(JsonObject msg, int embed) => msg["embeds"]![embed]!["description"]!.GetValue<string>();

    [Fact]
    public void CountsTheWeek()
    {
        var msg = DiscordMessageBuilder.BuildWeeklySummary(Report(), Now, string.Empty);
        var week = Description(msg, 0);
        Assert.Contains("**Newly missing:** 2 episodes and 1 film", week);
        Assert.Contains("**Turned up:** 3 episodes, 1 season and 1 film", week);
        Assert.Contains("**Old Show** (4)", week);
        Assert.DoesNotContain("Too old", week);
        Assert.Equal("📊 **Completionist weekly summary** · 25 Oct – 1 Nov 2026", msg["content"]!.GetValue<string>());
    }

    [Fact]
    public void ListsWhatsStillMissing()
    {
        var msg = DiscordMessageBuilder.BuildWeeklySummary(Report(), Now, string.Empty);
        var still = Description(msg, 1);
        Assert.StartsWith("3 episodes, 1 season and 1 film.", still);
        Assert.Contains("**Old Show (2001)**: Season 3, 1 episode", still);
        Assert.Contains("**Back to the Future Collection**: 2 of 3, missing 1 film", still);
        Assert.Equal("Checked 40 shows and 6 collections · full list in Dashboard → Completionist", msg["embeds"]![1]!["footer"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void QuietWeekAndCompleteLibrary()
    {
        var msg = DiscordMessageBuilder.BuildWeeklySummary(new ScanReport { SeriesChecked = 3 }, Now, string.Empty);
        Assert.Contains("**Newly missing:** nothing", Description(msg, 0));
        Assert.Contains("**Turned up:** nothing", Description(msg, 0));
        Assert.Equal("Nothing. Your library is complete.", Description(msg, 1));
    }

    [Fact]
    public void LinksWhenServerAddressIsSet()
    {
        var report = Report();
        var id = report.Series[0].SeriesId;
        var still = Description(DiscordMessageBuilder.BuildWeeklySummary(report, Now, "https://jf.example.com/"), 1);
        Assert.Contains($"[**Old Show (2001)**](https://jf.example.com/web/#/details?id={id:N})", still);
    }

    [Fact]
    public void FilledHistory_AddsThisCheckAndDropsOldEntries()
    {
        var history = new List<FilledEntry> { new() { Time = Now.AddDays(-9), Name = "Gone" }, new() { Time = Now.AddDays(-3), Name = "Kept" } };
        var shows = new List<ResolvedChange> { new(Guid.NewGuid(), "Show", ["Season 2", "S01E04", "S01E05"]) };
        var films = new List<ResolvedChange> { new(null, "Collection", ["A film"]) };

        var kept = CompletionistScanner.KeepFilled(history, shows, films, Now);

        Assert.Equal(["Kept", "Show", "Collection"], kept.Select(f => f.Name));
        Assert.Equal((1, 2, false), (kept[1].Seasons, kept[1].Items, kept[1].IsCollection));
        Assert.Equal((0, 1, true), (kept[2].Seasons, kept[2].Items, kept[2].IsCollection));
    }
}

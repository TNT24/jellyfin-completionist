using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Completionist.Configuration;

/// <summary>A film collection or film the user chose to ignore, with its name for the settings page.</summary>
public class IgnoredItem
{
    /// <summary>Gets or sets the TMDb id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the name shown on the settings page.</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Plugin settings, edited from Dashboard → Plugins → Completionist.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the Discord webhook URL. Empty turns Discord notifications off.</summary>
    public string DiscordWebhookUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether Discord is also told when missing episodes or films turn up.</summary>
    public bool NotifyResolved { get; set; }

    /// <summary>Gets or sets a value indicating whether Discord is told when a check finds nothing new missing.</summary>
    public bool NotifyNothingNew { get; set; }

    /// <summary>
    /// Gets or sets the address Discord can reach the server on (e.g. https://jellyfin.example.com).
    /// When set, notifications link to the show and include its poster. Empty leaves both out.
    /// </summary>
    public string PublicServerUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a TMDb API key (v3 key or v4 read access token). Needed for film collections.
    /// For TV it's optional: TMDb is asked first, which matches the episode numbering Jellyfin's default
    /// metadata uses; otherwise only TVmaze is used.
    /// </summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the library ids to check. Empty means all TV and movie libraries.</summary>
    public string[] LibraryIds { get; set; } = [];

    /// <summary>Gets or sets the ids of series the user chose to ignore.</summary>
    public string[] IgnoredSeriesIds { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether TV shows are checked.</summary>
    public bool CheckShows { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether specials (season 0) count.</summary>
    public bool IncludeSpecials { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether seasons you have no episodes of are reported.
    /// Off suits libraries where only some seasons of a show are kept on purpose.
    /// </summary>
    public bool ReportWholeSeasons { get; set; } = true;

    /// <summary>Gets or sets how many days after airing an episode counts as missing, to give downloads time.</summary>
    public int GraceDays { get; set; } = 2;

    /// <summary>
    /// Gets or sets a value indicating whether shows no episode guide knows are still checked for gaps
    /// in their numbering (having episodes 1, 2 and 4 means 3 is missing).
    /// </summary>
    public bool CheckGapsWithoutGuide { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether film collections (TMDb franchises you own at least one film of)
    /// are checked for missing films. Needs <see cref="TmdbApiKey"/>.
    /// </summary>
    public bool CheckFilmCollections { get; set; } = true;

    /// <summary>
    /// Gets or sets how many days after its release a film counts as missing. TMDb's date is usually the
    /// cinema release, and films typically reach streaming and disc a few months later.
    /// </summary>
    public int FilmGraceDays { get; set; } = 90;

    /// <summary>Gets or sets film collections the user chose to ignore.</summary>
    public IgnoredItem[] IgnoredCollections { get; set; } = [];

    /// <summary>Gets or sets single films the user chose to ignore.</summary>
    public IgnoredItem[] IgnoredFilms { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether a check runs after each library scan.</summary>
    public bool CheckOnLibraryScan { get; set; } = true;
}

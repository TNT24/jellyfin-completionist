using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Completionist.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Completionist;

/// <summary>
/// Finds aired TV seasons and episodes and released films from your collections that are missing from the library, and reports new ones to Discord.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the current plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Completionist";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("aceeaf3d-1c56-410f-af3b-0f4f8fa40746");

    /// <inheritdoc />
    public override string Description => "Finds missing TV seasons, episodes and collection films, and notifies you about new ones.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = "Completionist",
                EnableInMainMenu = true,
                MenuIcon = "fact_check",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        ];
    }
}

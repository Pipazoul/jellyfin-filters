using System;
using System.Collections.Generic;
using System.Globalization;
using JellyfinPluginDurationFilter.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace JellyfinPluginDurationFilter
{
    /// <summary>
    /// The Duration Filter plugin. Adds a Min/Max runtime filter (in minutes) to the
    /// jellyfin-web library filter panel.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Plugin"/> class.
        /// </summary>
        /// <param name="applicationPaths">Server application paths.</param>
        /// <param name="xmlSerializer">XML serializer used to persist configuration.</param>
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        /// <summary>
        /// Gets the singleton instance of the plugin.
        /// </summary>
        public static Plugin? Instance { get; private set; }

        /// <inheritdoc />
        public override string Name => "Duration Filter";

        /// <inheritdoc />
        public override string Description =>
            "Adds a Min/Max runtime filter (in minutes) to the library filter panel.";

        /// <inheritdoc />
        public override Guid Id => new Guid("957ba055-7b9a-4191-972a-59879fd73ee3");

        /// <inheritdoc />
        public IEnumerable<PluginPageInfo> GetPages()
        {
            yield return new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace),
            };
        }
    }
}

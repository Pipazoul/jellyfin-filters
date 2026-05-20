using MediaBrowser.Model.Plugins;

namespace JellyfinPluginDurationFilter.Configuration
{
    /// <summary>
    /// Persisted configuration for the Duration Filter plugin.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Gets or sets the default value (in minutes) pre-filled into the "Min duration"
        /// input. This only seeds the input the first time a library is opened; it is not
        /// applied automatically. 0 means "no minimum".
        /// </summary>
        public int DefaultMinMinutes { get; set; }

        /// <summary>
        /// Gets or sets the default value (in minutes) pre-filled into the "Max duration"
        /// input. 0 means "no maximum".
        /// </summary>
        public int DefaultMaxMinutes { get; set; }

        /// <summary>
        /// Gets or sets a comma-separated list of library (collection folder) IDs the
        /// filter should be enabled in. An empty string enables it in every video library.
        /// </summary>
        public string EnabledLibraryIds { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether a small chip is shown while a duration
        /// filter is active, giving the user a one-click way to clear it.
        /// </summary>
        public bool ShowChip { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether the plugin may patch
        /// <c>jellyfin-web/index.html</c> directly on disk when the File Transformation
        /// plugin is not installed. Disable this if your web root is read-only.
        /// </summary>
        public bool UseDirectInjectionFallback { get; set; } = true;
    }
}

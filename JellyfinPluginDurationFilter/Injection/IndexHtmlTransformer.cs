using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JellyfinPluginDurationFilter.Configuration;

namespace JellyfinPluginDurationFilter.Injection
{
    /// <summary>
    /// Payload passed to <see cref="IndexHtmlTransformer.TransformIndexHtml"/> by the
    /// File Transformation plugin. The plugin serialises <c>{ "contents": "..." }</c>
    /// into this type, so the single <see cref="Contents"/> property is all that is needed.
    /// </summary>
    public class IndexHtmlPayload
    {
        /// <summary>
        /// Gets or sets the current contents of the file being transformed.
        /// </summary>
        public string Contents { get; set; } = string.Empty;
    }

    /// <summary>
    /// Builds the client-side injection block and applies it to <c>index.html</c>.
    /// Used both by the File Transformation plugin (in-memory, preferred) and by the
    /// direct-edit fallback in <see cref="WebInjectionService"/>.
    /// </summary>
    public static class IndexHtmlTransformer
    {
        /// <summary>HTML comment marking the start of the injected block.</summary>
        public const string MarkerStart = "<!-- duration-filter:start -->";

        /// <summary>HTML comment marking the end of the injected block.</summary>
        public const string MarkerEnd = "<!-- duration-filter:end -->";

        private static readonly Regex ExistingBlockRegex = new Regex(
            Regex.Escape(MarkerStart) + ".*?" + Regex.Escape(MarkerEnd),
            RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>
        /// File Transformation plugin callback. Receives a file's current contents and,
        /// when it is the jellyfin-web <c>index.html</c> document, returns it with the
        /// Duration Filter client assets injected before <c>&lt;/body&gt;</c>.
        /// </summary>
        /// <param name="payload">The transformation payload supplied by the File Transformation plugin.</param>
        /// <returns>The transformed HTML, or the input unchanged if it is not an HTML document.</returns>
        public static string TransformIndexHtml(IndexHtmlPayload payload)
        {
            var contents = payload?.Contents ?? string.Empty;
            try
            {
                // The File Transformation plugin matches our file-name pattern as a
                // regex, and in "index.html" the '.' is a wildcard - so it also routes
                // webpack chunks whose name contains "index-html" here, e.g.
                // jellyfin-web's video player chunk
                // "playback-video-index-html.<hash>.chunk.js". Those are JavaScript:
                // only ever transform a genuine HTML document, otherwise we would
                // append our <script>/<style> block to a JS file and break it.
                if (!LooksLikeHtmlDocument(contents))
                {
                    return contents;
                }

                return InjectInto(contents);
            }
            catch (Exception)
            {
                // Never break the page: if anything goes wrong, return the original markup.
                return contents;
            }
        }

        /// <summary>
        /// Inserts (or refreshes) the injection block in the supplied HTML document.
        /// The operation is idempotent: a previously injected block is removed first.
        /// </summary>
        /// <param name="html">The HTML document to transform.</param>
        /// <returns>The transformed HTML.</returns>
        public static string InjectInto(string html)
        {
            if (string.IsNullOrEmpty(html))
            {
                return html;
            }

            // Remove any previously injected block so re-runs / upgrades stay clean.
            html = ExistingBlockRegex.Replace(html, string.Empty);

            var block = BuildInjectionBlock();

            // Insert just before the last </body>. Content with no </body> is not a
            // document we recognise - return it untouched rather than appending markup.
            var bodyIndex = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (bodyIndex < 0)
            {
                return html;
            }

            return html.Substring(0, bodyIndex) + block + html.Substring(bodyIndex);
        }

        /// <summary>
        /// Returns true if the supplied HTML already contains an injected block.
        /// </summary>
        /// <param name="html">The HTML document to inspect.</param>
        /// <returns><c>true</c> if a block is present.</returns>
        public static bool IsInjected(string html) =>
            !string.IsNullOrEmpty(html) && html.Contains(MarkerStart, StringComparison.Ordinal);

        /// <summary>
        /// Removes a previously injected block from the supplied HTML, leaving it untouched
        /// if no block is present.
        /// </summary>
        /// <param name="html">The HTML document to clean.</param>
        /// <returns>The HTML without the injected block.</returns>
        public static string StripInjection(string html) =>
            string.IsNullOrEmpty(html) ? html : ExistingBlockRegex.Replace(html, string.Empty);

        /// <summary>
        /// Builds the full injection block: marker comments, runtime config, CSS and the client script.
        /// </summary>
        /// <returns>The HTML block to inject.</returns>
        public static string BuildInjectionBlock()
        {
            // Defensive: keep an accidental closing tag inside the assets from
            // terminating the <style>/<script> element early.
            var css = ReadResource("Web.durationFilter.css")
                .Replace("</style", "<\\/style", StringComparison.OrdinalIgnoreCase);
            var js = ReadResource("Web.durationFilter.js")
                .Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);

            var sb = new StringBuilder();
            sb.Append('\n').Append(MarkerStart).Append('\n');
            sb.Append("<style id=\"duration-filter-style\">").Append(css).Append("</style>\n");
            sb.Append("<script id=\"duration-filter-config\">window.__JELLYFIN_DURATION_FILTER__=")
              .Append(BuildClientConfigJson())
              .Append(";</script>\n");
            sb.Append("<script id=\"duration-filter-script\">").Append(js).Append("\n//# sourceURL=durationFilter.js\n</script>\n");
            sb.Append(MarkerEnd).Append('\n');
            return sb.ToString();
        }

        /// <summary>
        /// Serialises the client-facing slice of the plugin configuration to JSON.
        /// </summary>
        /// <returns>A JSON object literal.</returns>
        public static string BuildClientConfigJson()
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

            var enabledLibraries = (config.EnabledLibraryIds ?? string.Empty)
                .Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(id => id.Trim())
                .Where(id => id.Length > 0)
                .ToArray();

            var payload = new Dictionary<string, object>
            {
                ["defaultMin"] = Math.Max(0, config.DefaultMinMinutes),
                ["defaultMax"] = Math.Max(0, config.DefaultMaxMinutes),
                ["enabledLibraryIds"] = enabledLibraries,
                ["version"] = typeof(IndexHtmlTransformer).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            };

            return JsonSerializer.Serialize(payload);
        }

        /// <summary>
        /// Determines whether the supplied content is an HTML document, as opposed to a
        /// JavaScript or CSS asset that the File Transformation plugin routed here via a
        /// loose regex match. A genuine document starts with a doctype or an
        /// <c>&lt;html&gt;</c> tag; a webpack bundle starts with JavaScript even when it
        /// embeds HTML template strings further in.
        /// </summary>
        /// <param name="content">The content to inspect.</param>
        /// <returns><c>true</c> if the content looks like an HTML document.</returns>
        private static bool LooksLikeHtmlDocument(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            var trimmed = content.TrimStart();
            return trimmed.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Reads an embedded text resource by the suffix of its manifest name.
        /// </summary>
        /// <param name="suffix">The resource name suffix, e.g. <c>Web.durationFilter.js</c>.</param>
        /// <returns>The resource contents.</returns>
        private static string ReadResource(string suffix)
        {
            var assembly = typeof(IndexHtmlTransformer).Assembly;
            var name = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

            if (name is null)
            {
                throw new InvalidOperationException(
                    string.Format(CultureInfo.InvariantCulture, "Embedded resource '{0}' not found.", suffix));
            }

            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException(
                    string.Format(CultureInfo.InvariantCulture, "Embedded resource stream '{0}' was null.", name));
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
    }
}

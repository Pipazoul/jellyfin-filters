using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellyfinPluginDurationFilter.Injection
{
    /// <summary>
    /// Hosted service that gets the Duration Filter client script loaded by jellyfin-web.
    ///
    /// Preferred path: register an in-memory transformation with the
    /// <c>File Transformation</c> plugin (https://github.com/IAmParadox27/jellyfin-plugin-file-transformation),
    /// which rewrites <c>index.html</c> as it is served without touching the file system.
    ///
    /// Fallback path: if that plugin is not installed, patch <c>jellyfin-web/index.html</c>
    /// directly on disk (can be disabled in the plugin settings).
    /// </summary>
    public class WebInjectionService : IHostedService
    {
        // A stable id for our registration with the File Transformation plugin.
        private const string TransformationId = "2c8f4d6a-1b3e-4a7c-9d2f-6e8a0b1c3d5f";

        private readonly ILogger<WebInjectionService> _logger;
        private readonly IServerApplicationPaths _appPaths;

        private bool _directEditApplied;

        /// <summary>
        /// Initializes a new instance of the <see cref="WebInjectionService"/> class.
        /// </summary>
        /// <param name="logger">Logger.</param>
        /// <param name="appPaths">Server application paths (used to locate the web root).</param>
        public WebInjectionService(ILogger<WebInjectionService> logger, IServerApplicationPaths appPaths)
        {
            _logger = logger;
            _appPaths = appPaths;
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (TryRegisterWithFileTransformation())
                {
                    _logger.LogInformation(
                        "Duration Filter: registered index.html transformation with the File Transformation plugin.");

                    // If an earlier run used the on-disk fallback, clean that stale
                    // patch now that the (cleaner) File Transformation path is active.
                    try
                    {
                        RemoveDirectEdit();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Duration Filter: could not clean a stale index.html patch.");
                    }

                    return Task.CompletedTask;
                }

                var config = Plugin.Instance?.Configuration;
                if (config is { UseDirectInjectionFallback: true })
                {
                    ApplyDirectEdit();
                }
                else
                {
                    _logger.LogWarning(
                        "Duration Filter: the File Transformation plugin was not found and the direct-edit "
                        + "fallback is disabled. The filter UI will not be injected. Install the File "
                        + "Transformation plugin or enable the fallback in the plugin settings.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Duration Filter: failed to inject the client script.");
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
        {
            // If we patched index.html on disk, undo it on shutdown so an uninstall
            // (stop, then a restart without this plugin) leaves the web root clean.
            if (_directEditApplied)
            {
                try
                {
                    RemoveDirectEdit();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Duration Filter: failed to remove the index.html patch on shutdown.");
                }
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Attempts to register an index.html transformation with the File Transformation plugin.
        /// All cross-plugin access is done by reflection because the two plugins load into
        /// separate <see cref="AssemblyLoadContext"/> instances.
        /// </summary>
        /// <returns><c>true</c> if the registration succeeded.</returns>
        private bool TryRegisterWithFileTransformation()
        {
            // The File Transformation plugin exposes a static entry point named
            // "Jellyfin.Plugin.FileTransformation.PluginInterface".
            Type? pluginInterface = AssemblyLoadContext.All
                .SelectMany(ctx => ctx.Assemblies)
                .Where(a => a.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) == true)
                .Select(a => a.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface"))
                .FirstOrDefault(t => t is not null);

            if (pluginInterface is null)
            {
                return false;
            }

            try
            {
                MethodInfo? register = pluginInterface.GetMethod(
                    "RegisterTransformation",
                    BindingFlags.Public | BindingFlags.Static);
                if (register is null)
                {
                    _logger.LogWarning("Duration Filter: File Transformation plugin found but RegisterTransformation is missing.");
                    return false;
                }

                // RegisterTransformation(JObject payload). We must build a JObject of the
                // *exact* type the plugin expects (its own Newtonsoft.Json copy), so we
                // resolve that type from the method signature and call its static
                // Parse(string) factory. This avoids referencing Newtonsoft.Json at all.
                Type jObjectType = register.GetParameters()[0].ParameterType;
                MethodInfo? parse = jObjectType.GetMethod(
                    "Parse",
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null,
                    types: new[] { typeof(string) },
                    modifiers: null);
                if (parse is null)
                {
                    _logger.LogWarning("Duration Filter: could not resolve JObject.Parse on the File Transformation payload type.");
                    return false;
                }

                object payload = parse.Invoke(null, new object[] { BuildTransformationPayloadJson() })!;
                register.Invoke(null, new[] { payload });
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Duration Filter: File Transformation plugin found but registration failed; will try the fallback.");
                return false;
            }
        }

        /// <summary>
        /// Builds the JSON registration payload understood by the File Transformation plugin.
        /// </summary>
        /// <returns>A JSON object string.</returns>
        private static string BuildTransformationPayloadJson()
        {
            var assembly = typeof(IndexHtmlTransformer).Assembly;
            var payload = new Dictionary<string, string?>
            {
                ["id"] = TransformationId,

                // The File Transformation plugin keys its transformation pipeline by this
                // string. It first does an *exact* dictionary lookup on the requested path
                // and only treats keys as regexes when no exact key matches. jellyfin-web
                // requests its document as exactly "index.html", so this MUST be the literal
                // "index.html": an escaped-regex form such as "index\.html" becomes a
                // separate key that loses the exact-match lookup to any other plugin that
                // registered the literal string (e.g. Jellyfin Enhanced), and our
                // transformation would then never run.
                ["fileNamePattern"] = "index.html",

                ["callbackAssembly"] = assembly.FullName,
                ["callbackClass"] = typeof(IndexHtmlTransformer).FullName,
                ["callbackMethod"] = nameof(IndexHtmlTransformer.TransformIndexHtml),
            };

            return JsonSerializer.Serialize(payload);
        }

        /// <summary>
        /// Patches <c>jellyfin-web/index.html</c> on disk.
        /// </summary>
        private void ApplyDirectEdit()
        {
            var indexPath = GetIndexHtmlPath();
            if (indexPath is null)
            {
                _logger.LogWarning("Duration Filter: could not locate jellyfin-web/index.html; UI was not injected.");
                return;
            }

            var original = File.ReadAllText(indexPath, Encoding.UTF8);
            var patched = IndexHtmlTransformer.InjectInto(original);

            if (!string.Equals(original, patched, StringComparison.Ordinal))
            {
                File.WriteAllText(indexPath, patched, new UTF8Encoding(false));
            }

            _directEditApplied = true;
            _logger.LogInformation(
                "Duration Filter: patched index.html directly at '{Path}'. Install the File Transformation "
                + "plugin for a cleaner, file-system-free injection.",
                indexPath);
        }

        /// <summary>
        /// Removes a previously applied direct patch from <c>index.html</c>.
        /// </summary>
        private void RemoveDirectEdit()
        {
            var indexPath = GetIndexHtmlPath();
            if (indexPath is null || !File.Exists(indexPath))
            {
                return;
            }

            var current = File.ReadAllText(indexPath, Encoding.UTF8);
            if (!IndexHtmlTransformer.IsInjected(current))
            {
                return;
            }

            var cleaned = IndexHtmlTransformer.StripInjection(current);
            if (!string.Equals(current, cleaned, StringComparison.Ordinal))
            {
                File.WriteAllText(indexPath, cleaned, new UTF8Encoding(false));
                _logger.LogInformation("Duration Filter: removed the index.html patch.");
            }
        }

        /// <summary>
        /// Resolves the absolute path to <c>index.html</c> inside the jellyfin-web root.
        /// </summary>
        /// <returns>The path, or <c>null</c> if it could not be found.</returns>
        private string? GetIndexHtmlPath()
        {
            var webPath = _appPaths.WebPath;
            if (string.IsNullOrEmpty(webPath))
            {
                return null;
            }

            var indexPath = Path.Combine(webPath, "index.html");
            return File.Exists(indexPath) ? indexPath : null;
        }
    }
}

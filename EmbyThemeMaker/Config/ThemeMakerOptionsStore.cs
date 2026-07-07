using EmbyThemeMaker.UIBaseClasses.Store;
using MediaBrowser.Common;
using MediaBrowser.Model.Logging;

namespace EmbyThemeMaker.Config
{
    /// <summary>
    /// Persists <see cref="ThemeMakerOptions"/> as JSON under Emby's plugin configuration path
    /// (via the vendored SimpleFileStore). Single source of truth for the plugin's settings.
    /// </summary>
    public class ThemeMakerOptionsStore : SimpleFileStore<ThemeMakerOptions>
    {
        public ThemeMakerOptionsStore(IApplicationHost applicationHost, ILogger logger, string pluginFullName)
            : base(applicationHost, logger, pluginFullName)
        {
        }
    }
}

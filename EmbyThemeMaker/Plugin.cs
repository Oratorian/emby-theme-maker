using System;
using System.Collections.Generic;
using EmbyThemeMaker.Config;
using EmbyThemeMaker.UI;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI;

namespace EmbyThemeMaker
{
    /// <summary>
    /// Entry point for the Theme Maker plugin. Uses Emby's code-driven UI (IHasUIPages) so the
    /// settings page renders natively (no HTML/JS). The generation logic runs from Scheduled Tasks.
    /// </summary>
    public class Plugin : BasePlugin, IHasUIPages, IHasPluginConfiguration
    {
        private readonly ThemeMakerOptionsStore _optionsStore;
        private List<IPluginUIPageController> _pages;

        public static Plugin Instance { get; private set; }

        public Plugin(IServerApplicationHost applicationHost, ILogManager logManager)
        {
            var logger = logManager.GetLogger(Name);
            _optionsStore = new ThemeMakerOptionsStore(applicationHost, logger, Name);
            Instance = this;
            logger.Info("[ThemeMaker] plugin loaded. Run it from Dashboard -> Scheduled Tasks: " +
                        "\"Theme Maker: Generate\" or \"Theme Maker: Preview (read-only)\".");
        }

        public override string Name => "Theme Maker";

        public override string Description =>
            "Generate per-series theme videos and music from Emby intro markers, using the " +
            "server's built-in ffmpeg. Runs from Scheduled Tasks.";

        public override Guid Id => new Guid("3132e1e1-5884-4478-8b20-b1db7de27b25");

        /// <summary>Live, persisted settings — the single source of truth for the plugin.</summary>
        public ThemeMakerOptions Options => _optionsStore.GetOptions();

        public IReadOnlyCollection<IPluginUIPageController> UIPageControllers
        {
            get
            {
                if (_pages == null)
                {
                    _pages = new List<IPluginUIPageController>
                    {
                        new ThemeMakerPageController(GetPluginInfo(), _optionsStore)
                    };
                }

                return _pages.AsReadOnly();
            }
        }

        // IHasPluginConfiguration — settings live in ThemeMakerOptionsStore; the legacy
        // BasePluginConfiguration is unused but present to satisfy the interface.
        public Type ConfigurationType => typeof(ThemeMakerPageController);

        public BasePluginConfiguration Configuration { get; } = new BasePluginConfiguration();

        public void UpdateConfiguration(BasePluginConfiguration configuration)
        {
        }

        public void SetStartupInfo(Action<string> directoryCreateFn)
        {
        }
    }
}

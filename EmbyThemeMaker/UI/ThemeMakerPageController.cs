using System.Threading.Tasks;
using EmbyThemeMaker.Config;
using EmbyThemeMaker.UIBaseClasses;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;

namespace EmbyThemeMaker.UI
{
    /// <summary>
    /// Declares the settings page and its placement. EnableInMainMenu gives the plugin its own
    /// left-menu entry; IsMainConfigPage=true makes the Plugins-grid "Settings" link open it too.
    /// </summary>
    internal class ThemeMakerPageController : ControllerBase
    {
        private readonly PluginInfo _pluginInfo;
        private readonly ThemeMakerOptionsStore _store;

        public ThemeMakerPageController(PluginInfo pluginInfo, ThemeMakerOptionsStore store)
            : base(pluginInfo.Id)
        {
            _pluginInfo = pluginInfo;
            _store = store;
            PageInfo = new PluginPageInfo
            {
                Name = "ThemeMaker",
                DisplayName = "Theme Maker",
                EnableInMainMenu = true,
                MenuIcon = "movie",
                IsMainConfigPage = true
            };
        }

        public override PluginPageInfo PageInfo { get; }

        public override Task<IPluginUIView> CreateDefaultPageView()
        {
            IPluginUIView view = new ThemeMakerPageView(_pluginInfo, _store);
            return Task.FromResult(view);
        }
    }
}

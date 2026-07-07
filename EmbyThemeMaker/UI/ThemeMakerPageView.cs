using System.Threading.Tasks;
using EmbyThemeMaker.Config;
using EmbyThemeMaker.UIBaseClasses.Views;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;

namespace EmbyThemeMaker.UI
{
    /// <summary>
    /// The settings page view. Binds the persisted <see cref="ThemeMakerOptions"/> as its content
    /// and writes it back on Save.
    /// </summary>
    internal class ThemeMakerPageView : PluginPageView
    {
        private readonly ThemeMakerOptionsStore _store;

        public ThemeMakerPageView(PluginInfo pluginInfo, ThemeMakerOptionsStore store)
            : base(pluginInfo.Id)
        {
            _store = store;
            ContentData = store.GetOptions();
        }

        public ThemeMakerOptions Options => ContentData as ThemeMakerOptions;

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            _store.SetOptions(Options);
            return base.OnSaveCommand(itemId, commandId, data);
        }
    }
}

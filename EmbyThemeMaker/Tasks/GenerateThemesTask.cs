using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;

namespace EmbyThemeMaker.Tasks
{
    /// <summary>
    /// Scheduled task that actually encodes theme files according to the plugin settings.
    /// Emby discovers IScheduledTask implementations automatically and injects their dependencies.
    /// </summary>
    public class GenerateThemesTask : ThemeTaskBase
    {
        public GenerateThemesTask(ILibraryManager libraryManager, IItemRepository itemRepository,
                                  IFfmpegManager ffmpegManager, ILogManager logManager)
            : base(libraryManager, itemRepository, ffmpegManager, logManager)
        {
        }

        public override string Name => "Theme Maker: Generate";

        public override string Key => "ThemeMakerGenerate";

        public override string Description =>
            "Cut each series' intro span into a theme video/music with the built-in ffmpeg, " +
            "per the Theme Maker settings.";

        protected override bool Preview => false;
    }
}

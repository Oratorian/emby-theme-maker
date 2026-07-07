using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;

namespace EmbyThemeMaker.Tasks
{
    /// <summary>
    /// Read-only scheduled task: logs what "Generate" WOULD do (source episode, intro span, and
    /// whether a theme already exists) without encoding anything. The plugin equivalent of the
    /// old --list / --dry-run modes.
    /// </summary>
    public class PreviewThemesTask : ThemeTaskBase
    {
        public PreviewThemesTask(ILibraryManager libraryManager, IItemRepository itemRepository,
                                 IFfmpegManager ffmpegManager, ILogManager logManager)
            : base(libraryManager, itemRepository, ffmpegManager, logManager)
        {
        }

        public override string Name => "Theme Maker: Preview (read-only)";

        public override string Key => "ThemeMakerPreview";

        public override string Description =>
            "Report what Theme Maker would generate for each series (source episode, intro span, " +
            "already-has-theme) without writing any files.";

        protected override bool Preview => true;
    }
}

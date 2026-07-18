using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyThemeMaker.Theme;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace EmbyThemeMaker.Tasks
{
    /// <summary>
    /// Shared plumbing for the two Theme Maker scheduled tasks (generate / preview): dependency
    /// wiring, running the engine, and logging a readable summary. Subclasses set the name/key and
    /// the preview flag.
    /// </summary>
    public abstract class ThemeTaskBase : IScheduledTask, IConfigurableScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly IFfmpegManager _ffmpegManager;
        protected readonly ILogger Logger;

        protected ThemeTaskBase(ILibraryManager libraryManager, IItemRepository itemRepository,
                                IFfmpegManager ffmpegManager, ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _ffmpegManager = ffmpegManager;
            Logger = logManager.GetLogger("ThemeMaker");
        }

        public abstract string Name { get; }
        public abstract string Key { get; }
        public abstract string Description { get; }
        protected abstract bool Preview { get; }

        public string Category => "Theme Maker";

        // IConfigurableScheduledTask: visible + enabled, and its run is logged.
        public bool IsEnabled => true;
        public bool IsHidden => false;
        public bool IsLogged => true;

        // No default schedule — the user adds a trigger (or runs it manually). Returning an empty
        // set avoids silently encoding on a cadence the user didn't ask for.
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            return Task.Run(() =>
            {
                var options = Plugin.Instance?.Options;
                if (options == null)
                {
                    Logger.Error("[ThemeMaker] plugin options unavailable; aborting task");
                    return;
                }

                var engine = new ThemeEngine(_libraryManager, _itemRepository, _ffmpegManager, Logger);
                var results = engine.Run(options, Preview, progress, cancellationToken);
                LogSummary(results);
            }, cancellationToken);
        }

        private void LogSummary(List<ThemeResult> results)
        {
            // Only log a per-line row at Info for the outcomes that actually matter for the mode:
            // for Generate that's what was written (Created) or failed (Error); for Preview that's
            // what would be written (WouldCreate) or failed. Everything else (Skipped/NoSource, i.e.
            // the bulk on a large library) rolls into the summary counts only. Full per-item detail
            // is available at Debug from the engine. This keeps a big-library run to a few lines.
            var interesting = Preview
                ? new[] { ResultStatus.WouldCreate, ResultStatus.Error }
                : new[] { ResultStatus.Created, ResultStatus.Error };

            foreach (var r in results)
            {
                if (Array.IndexOf(interesting, r.Status) < 0)
                {
                    // Not a headline outcome — keep it out of the Info log, but leave a Debug trail.
                    var kd = r.Kind.HasValue ? " [" + r.Kind.Value.ToString().ToLowerInvariant() + "]" : "";
                    Logger.Debug("[ThemeMaker]   {0} {1}{2}: {3}", r.Status, r.SeriesName, kd, r.Detail);
                    continue;
                }

                var kind = r.Kind.HasValue ? " [" + r.Kind.Value.ToString().ToLowerInvariant() + "]" : "";
                Logger.Info("[ThemeMaker]   {0} {1}{2}: {3}", r.Status, r.SeriesName, kind, r.Detail);
            }

            var byStatus = results.GroupBy(r => r.Status)
                .ToDictionary(g => g.Key, g => g.Count());
            var parts = byStatus
                .OrderBy(kv => kv.Key.ToString())
                .Select(kv => kv.Key.ToString().ToLowerInvariant() + "=" + kv.Value);
            Logger.Info("[ThemeMaker] {0} summary: {1}",
                Preview ? "preview" : "generate",
                parts.Any() ? string.Join(", ", parts) : "nothing to do");
        }
    }
}

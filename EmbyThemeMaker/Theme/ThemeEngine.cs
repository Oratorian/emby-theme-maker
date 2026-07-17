using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using EmbyThemeMaker.Config;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace EmbyThemeMaker.Theme
{
    /// <summary>
    /// Core logic: find series, read their intro markers directly from the item repository,
    /// pick a representative episode, and (optionally) cut the theme with ffmpeg. In-process
    /// equivalent of emby_theme_maker.py — no HTTP, and item paths are already local so there
    /// is no path mapping.
    /// </summary>
    internal sealed class ThemeEngine
    {
        private const long TicksPerSecond = 10_000_000;

        // Emby-recognised theme containers.
        private static readonly string[] ThemeExts = { ".mp4", ".mkv", ".webm", ".m4v", ".mov" };
        private static readonly string[] ThemeAudioExts =
        {
            ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".oga", ".opus", ".wav", ".wma",
        };

        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly FfmpegRunner _ffmpeg;
        private readonly ILogger _logger;

        public ThemeEngine(ILibraryManager libraryManager, IItemRepository itemRepository,
                           IFfmpegManager ffmpegManager, ILogger logger)
        {
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _ffmpeg = new FfmpegRunner(ffmpegManager, logger);
            _logger = logger;
        }

        // ------------------------------------------------------------------ #
        // Public entry: run over all series
        // ------------------------------------------------------------------ #
        public List<ThemeResult> Run(ThemeMakerOptions cfg, bool preview, IProgress<double> progress,
                                     CancellationToken ct)
        {
            var results = new List<ThemeResult>();

            var maxRate = string.IsNullOrWhiteSpace(cfg.MaxRate) ? "uncapped" : cfg.MaxRate;
            var audioLang = string.IsNullOrWhiteSpace(cfg.AudioLang) ? "any" : cfg.AudioLang;

            _logger.Info("[ThemeMaker] ===== {0} run starting =====", preview ? "PREVIEW" : "GENERATE");
            _logger.Info("[ThemeMaker] settings: mode={0}, force={1}, prefer={2}, minIntro={3}s, maxIntro={4}s, " +
                         "pad={5}/{6}s, audioLang={7}, jobs={8}, refreshAfter={9}{10}",
                cfg.Mode, cfg.Force, cfg.Prefer, cfg.MinIntro, cfg.MaxIntro, cfg.PadStart, cfg.PadEnd,
                audioLang, cfg.Jobs, cfg.RefreshAfter,
                string.IsNullOrWhiteSpace(cfg.OnlyUnderPath) ? "" : ", onlyUnder=" + cfg.OnlyUnderPath);
            _logger.Info("[ThemeMaker] encode: maxHeight={0}, crf={1}, preset={2}, peakBitrate={3}, " +
                         "audioBitrate={4}, fadeIn={5}s, fadeOut={6}s",
                cfg.MaxHeight, cfg.Crf, cfg.Preset, maxRate, cfg.AudioBitrate, cfg.FadeIn, cfg.FadeOut);
            _logger.Info("[ThemeMaker] output: video={0}/{1}, audio={2}",
                cfg.OutDirName, cfg.OutName, cfg.AudioOutName);

            var units = GetWorkUnits(cfg);
            _logger.Info("[ThemeMaker] {0} item(s) to consider ({1})", units.Count,
                cfg.IncludeMovies ? "series + movies" : "series only");

            if (units.Count == 0)
            {
                _logger.Info("[ThemeMaker] nothing to do (nothing matched). Run finished.");
                return results;
            }

            var jobs = preview ? 1 : Math.Max(1, cfg.Jobs);
            var gate = new SemaphoreSlim(jobs);
            var tasks = new List<System.Threading.Tasks.Task>();
            var resultsLock = new object();
            int done = 0;
            bool anyCreated = false;

            foreach (var u in units)
            {
                ct.ThrowIfCancellationRequested();
                gate.Wait(ct);
                var captured = u;
                tasks.Add(System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        var r = ProcessUnit(captured, cfg, preview, ct);
                        lock (resultsLock)
                        {
                            results.AddRange(r);
                            if (r.Any(x => x.Status == ResultStatus.Created))
                            {
                                anyCreated = true;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // swallow; the outer run reports cancellation
                    }
                    catch (Exception ex)
                    {
                        lock (resultsLock)
                        {
                            results.Add(ThemeResult.Make(captured.Name, ResultStatus.Error, "unexpected: " + ex.Message));
                        }
                    }
                    finally
                    {
                        gate.Release();
                        var d = Interlocked.Increment(ref done);
                        progress?.Report(90.0 * d / units.Count);
                    }
                }, ct));
            }

            try
            {
                System.Threading.Tasks.Task.WaitAll(tasks.ToArray(), ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            // A single library scan registers everything just written (theme music only
            // registers on a full scan).
            if (!preview && cfg.RefreshAfter && anyCreated)
            {
                _logger.Info("[ThemeMaker] requesting Emby library scan so new theme files are detected");
                try
                {
                    _libraryManager.ValidateMediaLibrary(new Progress<double>(), ct);
                }
                catch (Exception ex)
                {
                    _logger.Error("[ThemeMaker] library scan failed: {0}", ex.Message);
                }
            }

            progress?.Report(100.0);
            return results;
        }

        // ------------------------------------------------------------------ #
        // Enumeration: build the list of work units (series, plus movies if enabled)
        // ------------------------------------------------------------------ #
        private List<WorkUnit> GetWorkUnits(ThemeMakerOptions cfg)
        {
            var types = cfg.IncludeMovies ? new[] { "Series", "Movie" } : new[] { "Series" };

            BaseItem[] items;
            try
            {
                items = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = types,
                    Recursive = true,
                });
            }
            catch (Exception ex)
            {
                _logger.Error("[ThemeMaker] failed to query items: {0}", ex.Message);
                return new List<WorkUnit>();
            }

            var list = new List<WorkUnit>();
            foreach (var it in items ?? Array.Empty<BaseItem>())
            {
                if (string.IsNullOrEmpty(it.Path))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(cfg.OnlyUnderPath) && !IsUnder(it.Path, cfg.OnlyUnderPath))
                {
                    continue;
                }

                // A series fans out to its episodes; a movie is its own single source. The output
                // dir is the item's own folder in both cases (series root / movie folder).
                if (it is Series series)
                {
                    list.Add(new WorkUnit(series.Name ?? "?", series.Path, isMovie: false, GetEpisodes(series)));
                }
                else if (it is Movie movie)
                {
                    list.Add(new WorkUnit(movie.Name ?? "?", MovieDir(movie), isMovie: true,
                                          new List<BaseItem> { movie }));
                }
            }

            list = list.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (cfg.Limit > 0 && list.Count > cfg.Limit)
            {
                list = list.Take(cfg.Limit).ToList();
            }

            return list;
        }

        // Output folder for a movie: its own containing directory (standard Emby one-folder-per-movie
        // layout). backdrops/theme.mp4 and theme.mp3 land there, mirroring the series convention.
        private static string MovieDir(Movie movie)
            => Path.GetDirectoryName(movie.Path) ?? movie.Path;

        private List<BaseItem> GetEpisodes(Series series)
        {
            var eps = new List<BaseItem>();
            try
            {
                foreach (var child in series.GetRecursiveChildren())
                {
                    if (child is Episode ep)
                    {
                        eps.Add(ep);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("[ThemeMaker] episode fetch failed for '{0}': {1}", series.Name, ex.Message);
            }

            return eps;
        }

        // ------------------------------------------------------------------ #
        // Per-unit processing (series or movie)
        // ------------------------------------------------------------------ #
        private List<ThemeResult> ProcessUnit(WorkUnit unit, ThemeMakerOptions cfg, bool preview,
                                              CancellationToken ct)
        {
            var name = unit.Name;
            var targets = BuildTargets(unit.OutDir, cfg);

            var (cand, reason) = ChooseSource(unit.Sources, cfg, ct);

            if (cand == null)
            {
                _logger.Info("[ThemeMaker] '{0}': no usable source ({1}) — {2} source(s) scanned", name, reason, unit.Sources.Count);
            }
            else if (unit.IsMovie)
            {
                _logger.Info("[ThemeMaker] '{0}' (movie): source intro {1:0.0}-{2:0.0}s ({3:0.0}s, {4}) from {5}",
                    name, cand.Start, cand.End, cand.Duration, reason, cand.LocalPath);
            }
            else
            {
                _logger.Info("[ThemeMaker] '{0}': source S{1}E{2} intro {3:0.0}-{4:0.0}s ({5:0.0}s, {6}) from {7}",
                    name, cand.Season, cand.Number, cand.Start, cand.End, cand.Duration, reason, cand.LocalPath);
            }

            return targets.Select(t => ProcessTarget(name, t, cand, reason, cfg, preview, ct)).ToList();
        }

        private ThemeResult ProcessTarget(string name, Target t, Candidate cand, string reason,
                                          ThemeMakerOptions cfg, bool preview, CancellationToken ct)
        {
            var prior = ExistingFor(t);

            if (preview)
            {
                if (cand == null)
                {
                    return ThemeResult.Make(name, ResultStatus.NoSource, "no source (" + reason + ")", t.Kind);
                }

                var tag = prior != null ? "HAS THEME" : "ready";
                var detail = string.Format(
                    "{0} -> {1} : S{2}E{3} intro {4:0.0}-{5:0.0}s ({6:0.0}s, {7})",
                    tag, t.OutPath, cand.Season, cand.Number, cand.Start, cand.End, cand.Duration, reason);
                return ThemeResult.Make(name, ResultStatus.WouldCreate, detail, t.Kind);
            }

            if (prior != null && !cfg.Force)
            {
                _logger.Debug("[ThemeMaker] '{0}' [{1}]: skip, exists: {2}", name, t.Kind, prior);
                return ThemeResult.Make(name, ResultStatus.Skipped,
                    "exists: " + Path.GetFileName(prior) + " (enable Overwrite to replace)", t.Kind);
            }

            if (cand == null)
            {
                return ThemeResult.Make(name, ResultStatus.NoSource, "no source (" + reason + ")", t.Kind);
            }

            var start = Math.Max(0.0, cand.Start - cfg.PadStart);
            var dur = (cand.End + cfg.PadEnd) - start;

            try
            {
                Directory.CreateDirectory(t.OutDir);
            }
            catch (Exception ex)
            {
                _logger.Error("[ThemeMaker] '{0}' [{1}]: mkdir failed for {2}: {3}", name, t.Kind, t.OutDir, ex.Message);
                return ThemeResult.Make(name, ResultStatus.Error, "mkdir failed: " + ex.Message, t.Kind);
            }

            _logger.Info("[ThemeMaker] '{0}' [{1}]: encoding {2:0.0}s -> {3}", name, t.Kind, dur, t.OutPath);
            var err = _ffmpeg.Encode(t, cand.LocalPath, start, dur, cfg, ct);
            if (err != null)
            {
                _logger.Error("[ThemeMaker] '{0}' [{1}]: encode failed: {2}", name, t.Kind, err);
                return ThemeResult.Make(name, ResultStatus.Error, err, t.Kind);
            }

            // Force over a DIFFERENTLY-named prior (e.g. theme.mkv -> theme.mp4): remove the old
            // one now that the new file is in place. Guarded to the SAME dir as the new file so
            // this never deletes a song out of a curated theme-music/ set, and only when the path
            // actually differs from what we just wrote. (Mirrors the Python cross-name cleanup.)
            if (prior != null
                && string.Equals(Path.GetDirectoryName(Path.GetFullPath(prior)),
                                 Path.GetFullPath(t.OutDir).TrimEnd(Path.DirectorySeparatorChar),
                                 StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetFullPath(prior), Path.GetFullPath(t.OutPath),
                                  StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(prior);
                    _logger.Info("[ThemeMaker] '{0}' [{1}]: removed superseded prior theme {2}", name, t.Kind, prior);
                }
                catch (Exception ex)
                {
                    _logger.Warn("[ThemeMaker] '{0}' [{1}]: could not remove prior {2}: {3}", name, t.Kind, prior, ex.Message);
                }
            }

            double sizeMb = 0;
            try { sizeMb = new FileInfo(t.OutPath).Length / (1024.0 * 1024.0); } catch { }

            _logger.Info("[ThemeMaker] '{0}' [{1}]: created {2:0.0} MB -> {3}", name, t.Kind, sizeMb, t.OutPath);
            return ThemeResult.Make(name, ResultStatus.Created,
                string.Format("{0:0.0}s, {1:0.0} MB -> {2}", dur, sizeMb, t.OutPath), t.Kind);
        }

        // ------------------------------------------------------------------ #
        // Marker reading + candidate selection (ported from Python)
        // ------------------------------------------------------------------ #
        private (double Start, double End)? FindIntro(BaseItem item, CancellationToken ct)
        {
            List<ChapterInfo> chapters;
            try
            {
                chapters = _itemRepository.GetChapters(item, ct);
            }
            catch (Exception ex)
            {
                _logger.Debug("[ThemeMaker] GetChapters failed for '{0}': {1}", item.Path, ex.Message);
                return null;
            }

            double? start = null, end = null;
            foreach (var ch in chapters ?? new List<ChapterInfo>())
            {
                if (ch.MarkerType == MarkerType.IntroStart && start == null)
                {
                    start = ch.StartPositionTicks / (double)TicksPerSecond;
                }
                else if (ch.MarkerType == MarkerType.IntroEnd && end == null)
                {
                    end = ch.StartPositionTicks / (double)TicksPerSecond;
                }
            }

            if (start == null || end == null || end <= start)
            {
                return null;
            }

            return (start.Value, end.Value);
        }

        private (Candidate, string) ChooseSource(List<BaseItem> sources, ThemeMakerOptions cfg,
                                                 CancellationToken ct)
        {
            var candidates = new List<Candidate>();
            bool sawMarker = false;

            foreach (var item in sources)
            {
                ct.ThrowIfCancellationRequested();
                var intro = FindIntro(item, ct);
                if (intro == null)
                {
                    continue;
                }

                sawMarker = true;
                var (start, end) = intro.Value;
                var dur = end - start;
                if (dur < cfg.MinIntro || dur > cfg.MaxIntro)
                {
                    continue;
                }

                var local = item.Path;
                if (string.IsNullOrEmpty(local) || !File.Exists(local))
                {
                    continue;
                }

                candidates.Add(new Candidate { Item = item, LocalPath = local, Start = start, End = end });
            }

            if (candidates.Count == 0)
            {
                return (null, sawMarker ? "markers present but no usable/in-range source file" : "no intro markers");
            }

            if (cfg.Prefer == SourcePref.First)
            {
                // Push specials/extras (season 0 or missing) to the back so real S01E01 wins.
                // For a movie there is only one candidate, so ordering is a no-op.
                int FirstSeason(Candidate c)
                {
                    var s = c.Item.ParentIndexNumber;
                    return (s.HasValue && s.Value > 0) ? s.Value : 9999;
                }

                var first = candidates
                    .OrderBy(FirstSeason)
                    .ThenBy(c => c.Number)
                    .First();
                return (first, "first-episode");
            }

            // "median": candidate whose intro duration is closest to the median.
            var med = Median(candidates.Select(c => c.Duration).ToList());
            var pick = candidates
                .OrderBy(c => Math.Abs(c.Duration - med))
                .ThenBy(c => c.Season)
                .ThenBy(c => c.Number)
                .First();
            return (pick, "median-duration");
        }

        private static double Median(List<double> values)
        {
            if (values.Count == 0)
            {
                return 0;
            }

            values.Sort();
            int mid = values.Count / 2;
            return values.Count % 2 == 1
                ? values[mid]
                : (values[mid - 1] + values[mid]) / 2.0;
        }

        // ------------------------------------------------------------------ #
        // Targets + existing-theme detection (ported from Python)
        // ------------------------------------------------------------------ #
        private static List<Target> BuildTargets(string itemDir, ThemeMakerOptions cfg)
        {
            var targets = new List<Target>();
            if (cfg.Mode == ThemeMode.Video || cfg.Mode == ThemeMode.Both)
            {
                targets.Add(new Target
                {
                    Kind = TargetKind.Video,
                    OutDir = Path.Combine(itemDir, cfg.OutDirName),
                    OutName = cfg.OutName,
                });
            }

            if (cfg.Mode == ThemeMode.Audio || cfg.Mode == ThemeMode.Both)
            {
                targets.Add(new Target
                {
                    Kind = TargetKind.Audio,
                    OutDir = itemDir,
                    OutName = cfg.AudioOutName,
                });
            }

            return targets;
        }

        private string ExistingFor(Target t)
        {
            return t.Kind == TargetKind.Audio
                ? ExistingThemeAudio(t.OutDir, t.OutName)
                : ExistingThemeVideo(t.OutDir, t.OutName);
        }

        private static string ExistingThemeVideo(string backdropsDir, string outName)
        {
            if (!Directory.Exists(backdropsDir))
            {
                return null;
            }

            var stem = Path.GetFileNameWithoutExtension(outName);
            var outExt = Path.GetExtension(outName);
            foreach (var ext in ThemeExts.Concat(new[] { outExt }))
            {
                var p = Path.Combine(backdropsDir, stem + ext);
                if (File.Exists(p))
                {
                    return p;
                }
            }

            return null;
        }

        private static string ExistingThemeAudio(string seriesDir, string outName)
        {
            if (!Directory.Exists(seriesDir))
            {
                return null;
            }

            var stem = Path.GetFileNameWithoutExtension(outName); // "theme"
            var outExt = Path.GetExtension(outName);
            foreach (var ext in ThemeAudioExts.Concat(new[] { outExt }))
            {
                var p = Path.Combine(seriesDir, stem + ext);
                if (File.Exists(p))
                {
                    return p;
                }
            }

            // a non-empty theme-music/ folder counts as "already has theme music"
            var tm = Path.Combine(seriesDir, "theme-music");
            if (Directory.Exists(tm))
            {
                foreach (var entry in Directory.EnumerateFiles(tm).OrderBy(x => x))
                {
                    if (ThemeAudioExts.Contains(Path.GetExtension(entry), StringComparer.OrdinalIgnoreCase))
                    {
                        return entry;
                    }
                }
            }

            return null;
        }

        private static bool IsUnder(string path, string root)
        {
            var p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(p, r, StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
    }
}

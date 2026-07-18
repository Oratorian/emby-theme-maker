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
            progress?.Report(0);

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
                if (!string.IsNullOrWhiteSpace(cfg.OnlyUnderPath))
                {
                    _logger.Info("[ThemeMaker] nothing to do — no items under '{0}' (check the \"Only under this folder\" setting). Run finished.",
                        cfg.OnlyUnderPath);
                }
                else
                {
                    _logger.Info("[ThemeMaker] nothing to do (nothing matched). Run finished.");
                }

                return results;
            }

            var jobs = preview ? 1 : Math.Max(1, cfg.Jobs);
            var gate = new SemaphoreSlim(jobs);
            var tasks = new List<System.Threading.Tasks.Task>();
            var resultsLock = new object();
            bool anyCreated = false;

            // Progress: encoding owns 0..90, the trailing library scan owns 90..100. Each unit's
            // targets are credited half on start and half on finish, so the bar moves even for a
            // single unit (0 -> 45 -> 90) instead of only jumping when a unit completes. A high-water
            // mark keeps it strictly non-decreasing despite parallel/out-of-order reports.
            int totalTargets = units.Sum(_ => TargetCountFor(cfg));
            double progressUnits = 0.0;
            double lastReported = 0.0;
            var progressLock = new object();

            void ReportEncode(double deltaTargets)
            {
                lock (progressLock)
                {
                    progressUnits += deltaTargets;
                    var pct = 90.0 * progressUnits / Math.Max(1, totalTargets);
                    if (pct > lastReported)
                    {
                        lastReported = pct;
                        progress?.Report(pct);
                    }
                }
            }

            // Launch loop is wrapped so that, however we leave it (normal completion OR a
            // cancellation thrown by ThrowIfCancellationRequested/gate.Wait mid-enumeration), every
            // worker already launched is joined before Run returns. Otherwise a cancel mid-launch
            // would throw straight out, leaving up-to-`jobs` encode threads orphaned and still
            // writing/deleting in output dirs when the next run starts. The worker bodies swallow
            // OCE/Exception, so the post-loop WaitAll never throws from normal cancellation.
            try
            {
                foreach (var u in units)
                {
                    ct.ThrowIfCancellationRequested();
                    gate.Wait(ct);
                    var captured = u;
                    var unitTargets = TargetCountFor(cfg);
                    tasks.Add(System.Threading.Tasks.Task.Run(() =>
                    {
                        ReportEncode(0.5 * unitTargets); // this unit's targets started
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
                            ReportEncode(0.5 * unitTargets); // this unit's targets finished
                        }
                    }, ct));
                }
            }
            finally
            {
                // Join everything already launched. The Release()s in worker finallys must complete
                // before the semaphore is disposed below, so this join precedes Dispose.
                try { System.Threading.Tasks.Task.WaitAll(tasks.ToArray()); }
                catch (AggregateException) { /* worker bodies already handled their own errors */ }
                gate.Dispose();
            }

            // Surface a cancellation requested during launch/encode now that all workers are joined.
            ct.ThrowIfCancellationRequested();

            // A single library scan registers everything just written (theme music only
            // registers on a full scan). The scan owns the 90..100 band: its own progress is
            // forwarded (mapped 0..100 -> 90..100) so the bar keeps moving during the scan tail
            // instead of freezing at 90. The scan is awaited — it returns a Task; discarding it
            // would let Report(100)/LogSummary fire while the scan is still running.
            if (!preview && cfg.RefreshAfter && anyCreated)
            {
                _logger.Info("[ThemeMaker] requesting Emby library scan so new theme files are detected");
                try
                {
                    var scanProgress = new Progress<double>(childPct =>
                    {
                        var pct = 90.0 + 10.0 * (Math.Max(0.0, Math.Min(100.0, childPct)) / 100.0);
                        lock (progressLock)
                        {
                            if (pct > lastReported)
                            {
                                lastReported = pct;
                                progress?.Report(pct);
                            }
                        }
                    });
                    // Block on the scan intentionally: Run is a synchronous method invoked from the
                    // task's Task.Run, so tying up this one pool thread until the scan finishes is
                    // fine (and there is no captured SynchronizationContext here, so no deadlock).
                    _libraryManager.ValidateMediaLibrary(scanProgress, ct).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    throw;
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

            // Movie output goes to the movie's own folder. If two units resolve to the SAME output
            // folder (two loose movie files in one directory, or a movie sitting directly in a
            // library root shared with other items), they would write the same backdrops/theme.mp4
            // and clobber each other — and a library-root theme is applied library-wide, not per
            // movie. Only movies that own their folder are safe, so skip any whose OutDir is shared.
            var dirCounts = list
                .GroupBy(u => NormDir(u.OutDir), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var kept = new List<WorkUnit>();
            foreach (var u in list)
            {
                if (u.IsMovie && dirCounts.TryGetValue(NormDir(u.OutDir), out var n) && n > 1)
                {
                    _logger.Info("[ThemeMaker] '{0}': skipped — movie shares its folder ({1}) with other " +
                                 "items, so a theme there would collide/apply library-wide. Give the movie its own folder.",
                                 u.Name, u.OutDir);
                    continue;
                }

                kept.Add(u);
            }

            kept = kept.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (cfg.Limit > 0 && kept.Count > cfg.Limit)
            {
                kept = kept.Take(cfg.Limit).ToList();
            }

            return kept;
        }

        // Output folder for a movie: its own containing directory (standard Emby one-folder-per-movie
        // layout). backdrops/theme.mp4 and theme.mp3 land there, mirroring the series convention.
        private static string MovieDir(Movie movie)
            => Path.GetDirectoryName(movie.Path) ?? movie.Path;

        // Normalize a directory for collision comparison (full path, no trailing separator).
        private static string NormDir(string dir)
        {
            try { return Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar); }
            catch { return dir ?? string.Empty; }
        }

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

            // Per-item source selection is Debug: on a large library most items have no markers,
            // so logging every one at Info floods the log. The run summary reports the counts.
            if (cand == null)
            {
                _logger.Debug("[ThemeMaker] '{0}': no usable source ({1}) — {2} source(s) scanned", name, reason, unit.Sources.Count);
            }
            else if (unit.IsMovie)
            {
                _logger.Debug("[ThemeMaker] '{0}' (movie): source intro {1:0.0}-{2:0.0}s ({3:0.0}s, {4}) from {5}",
                    name, cand.Start, cand.End, cand.Duration, reason, cand.LocalPath);
            }
            else
            {
                _logger.Debug("[ThemeMaker] '{0}': source S{1}E{2} intro {3:0.0}-{4:0.0}s ({5:0.0}s, {6}) from {7}",
                    name, cand.Season, cand.Number, cand.Start, cand.End, cand.Duration, reason, cand.LocalPath);
            }

            return targets.Select(t => ProcessTarget(name, unit.IsMovie, t, cand, reason, cfg, preview, ct)).ToList();
        }

        private ThemeResult ProcessTarget(string name, bool isMovie, Target t, Candidate cand, string reason,
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
                // Movies have no season/episode — only series get the SxxExx segment.
                var src = isMovie
                    ? string.Empty
                    : string.Format("S{0}E{1} ", cand.Season, cand.Number);
                var detail = string.Format(
                    "{0} -> {1} : {2}intro {3:0.0}-{4:0.0}s ({5:0.0}s, {6})",
                    tag, t.OutPath, src, cand.Start, cand.End, cand.Duration, reason);
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

            _logger.Debug("[ThemeMaker] '{0}' [{1}]: encoding {2:0.0}s -> {3}", name, t.Kind, dur, t.OutPath);
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

            // Collect and sort marker positions rather than trusting repository order: pick the
            // earliest IntroStart, then the earliest IntroEnd strictly after it. This survives
            // chapters returned out of order or a stray IntroEnd sitting before the real IntroStart.
            var starts = new List<long>();
            var ends = new List<long>();
            foreach (var ch in chapters ?? new List<ChapterInfo>())
            {
                if (ch.MarkerType == MarkerType.IntroStart)
                {
                    starts.Add(ch.StartPositionTicks);
                }
                else if (ch.MarkerType == MarkerType.IntroEnd)
                {
                    ends.Add(ch.StartPositionTicks);
                }
            }

            if (starts.Count == 0 || ends.Count == 0)
            {
                return null;
            }

            long startTicks = starts.Min();
            long? endTicks = ends.Where(e => e > startTicks).Cast<long?>().Min();
            if (endTicks == null)
            {
                return null;
            }

            return (startTicks / (double)TicksPerSecond, endTicks.Value / (double)TicksPerSecond);
        }

        // Resolve an item's path to an actual playable file. Usually the path IS the file. For a
        // folder-stacked / disc-image movie (DVD/BDMV or a stacked folder) Emby may set Path to a
        // directory; in that case pick the largest video file under it (the main feature) so a
        // stacked movie with valid markers isn't silently skipped. Returns null if nothing usable.
        private static string ResolveMediaFile(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            if (File.Exists(path))
            {
                return path;
            }

            if (!Directory.Exists(path))
            {
                return null;
            }

            try
            {
                return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Where(f => ThemeExts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)
                                || string.Equals(Path.GetExtension(f), ".ts", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => { try { return new FileInfo(f).Length; } catch { return 0L; } })
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private (Candidate, string) ChooseSource(List<BaseItem> sources, ThemeMakerOptions cfg,
                                                 CancellationToken ct)
        {
            var candidates = new List<Candidate>();
            bool sawMarker = false;
            double? outOfRangeDur = null; // a marked span that was rejected only for its length

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
                    outOfRangeDur = dur; // remember one, for an actionable "no source" reason
                    continue;
                }

                var local = ResolveMediaFile(item.Path);
                if (local == null)
                {
                    continue;
                }

                candidates.Add(new Candidate { Item = item, LocalPath = local, Start = start, End = end });
            }

            if (candidates.Count == 0)
            {
                if (!sawMarker)
                {
                    return (null, "no intro markers");
                }

                // Markers exist but nothing qualified. If a span was rejected purely for its length,
                // say so with the number and the window — the common movie case (e.g. a 210s scene
                // with Maximum intro length = 150), so the operator knows exactly what to change.
                if (outOfRangeDur.HasValue)
                {
                    return (null, string.Format("intro {0:0.0}s outside [{1:0.#}, {2:0.#}]s — adjust Min/Max intro length",
                        outOfRangeDur.Value, cfg.MinIntro, cfg.MaxIntro));
                }

                return (null, "markers present but the source file is missing/unreadable");
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
        // How many output targets a unit produces (Both = video + audio = 2, otherwise 1). Kept in
        // step with BuildTargets so progress weighting matches the actual encode count.
        private static int TargetCountFor(ThemeMakerOptions cfg)
            => cfg.Mode == ThemeMode.Both ? 2 : 1;

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

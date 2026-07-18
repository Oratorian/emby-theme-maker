using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using EmbyThemeMaker.Config;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Logging;

namespace EmbyThemeMaker.Theme
{
    /// <summary>
    /// Builds and runs ffmpeg commands to cut the intro span into a theme video or audio file.
    /// Uses Emby's bundled ffmpeg (IMediaEncoder.EncoderPath), so no external ffmpeg is required.
    /// Command shape ported verbatim from emby_theme_maker.py (build_video_cmd / build_audio_cmd).
    /// </summary>
    internal sealed class FfmpegRunner
    {
        private static readonly IReadOnlyDictionary<string, string> FormatByExt =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [".mp4"] = "mp4", [".m4v"] = "mp4", [".mov"] = "mov",
                [".mkv"] = "matroska", [".webm"] = "webm",
            };

        // audio ext -> (ffmpeg muxer forced via -f, audio codec). Fallback = mp3.
        private static readonly IReadOnlyDictionary<string, (string Muxer, string Codec)> AudioCodecByExt =
            new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
            {
                [".mp3"] = ("mp3", "libmp3lame"),
                [".m4a"] = ("ipod", "aac"),
                [".aac"] = ("adts", "aac"),
                [".flac"] = ("flac", "flac"),
                [".ogg"] = ("ogg", "libvorbis"),
                [".opus"] = ("opus", "libopus"),
                [".wav"] = ("wav", "pcm_s16le"),
            };

        /// <summary>Video output extensions this runner can actually mux (for settings validation).</summary>
        public static IReadOnlyCollection<string> SupportedVideoExts => (IReadOnlyCollection<string>)FormatByExt.Keys;

        /// <summary>Audio output extensions this runner can actually mux (for settings validation).</summary>
        public static IReadOnlyCollection<string> SupportedAudioExts => (IReadOnlyCollection<string>)AudioCodecByExt.Keys;

        private readonly IFfmpegManager _ffmpegManager;
        private readonly ILogger _logger;

        public FfmpegRunner(IFfmpegManager ffmpegManager, ILogger logger)
        {
            _ffmpegManager = ffmpegManager;
            _logger = logger;
        }

        private string FfmpegPath => _ffmpegManager?.FfmpegConfiguration?.EncoderPath;
        private string FfprobePath => _ffmpegManager?.FfmpegConfiguration?.ProbePath; // Emby's ffprobe

        /// <summary>Run the encode for one target. Returns null on success, or an error message.</summary>
        public string Encode(Target target, string src, double start, double dur, ThemeMakerOptions cfg,
                             CancellationToken ct)
        {
            var ffmpeg = FfmpegPath;
            if (string.IsNullOrEmpty(ffmpeg) || !File.Exists(ffmpeg))
            {
                return "Emby ffmpeg not found (FfmpegConfiguration.EncoderPath = '" + (ffmpeg ?? "null") + "')";
            }

            // Atomic write: encode to a temp name in the same dir, then replace.
            var stem = Path.GetFileNameWithoutExtension(target.OutName);
            var ext = Path.GetExtension(target.OutName);
            var tmp = Path.Combine(target.OutDir,
                "." + stem + "." + Process.GetCurrentProcess().Id + "." +
                Thread.CurrentThread.ManagedThreadId + ".tmp" + ext);

            var args = target.Kind == TargetKind.Audio
                ? BuildAudioArgs(src, start, dur, tmp, target.OutName, cfg)
                : BuildVideoArgs(src, start, dur, tmp, target.OutName, cfg);

            _logger.Debug("[ThemeMaker] ffmpeg ({0}) {1}", ffmpeg, string.Join(" ", args.Select(QuoteArg)));

            int rc;
            string stderrTail;
            bool timedOut;
            try
            {
                rc = RunFfmpeg(ffmpeg, args, out stderrTail, out timedOut, ct);
            }
            catch (OperationCanceledException)
            {
                SafeDelete(tmp);
                throw;
            }
            catch (Exception ex)
            {
                SafeDelete(tmp);
                return "ffmpeg spawn failed: " + ex.Message;
            }

            if (timedOut)
            {
                SafeDelete(tmp);
                return "ffmpeg killed: exceeded " + MaxRuntimeMinutes + " min runtime cap (source may be unreadable)";
            }

            if (rc != 0)
            {
                SafeDelete(tmp);
                return "ffmpeg: " + stderrTail;
            }

            if (!File.Exists(tmp) || new FileInfo(tmp).Length == 0)
            {
                SafeDelete(tmp);
                return "ffmpeg produced no output";
            }

            try
            {
                if (File.Exists(target.OutPath))
                {
                    // Atomic same-volume replace: the destination is never momentarily absent
                    // (matches Python's os.replace). Falls back to Move if Replace isn't supported.
                    try
                    {
                        File.Replace(tmp, target.OutPath, null);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Delete(target.OutPath);
                        File.Move(tmp, target.OutPath);
                    }
                }
                else
                {
                    File.Move(tmp, target.OutPath);
                }
            }
            catch (Exception ex)
            {
                SafeDelete(tmp);
                return "finalize failed: " + ex.Message;
            }

            return null;
        }

        // ------------------------------------------------------------------ #
        // Command building (ported from Python)
        // ------------------------------------------------------------------ #
        private static (double In, double Out, double OutStart) FadeArgs(double dur, ThemeMakerOptions cfg)
        {
            var fin = Math.Min(cfg.FadeIn, dur / 4);
            var fout = Math.Min(cfg.FadeOut, dur / 4);
            return (fin, fout, Math.Max(0.0, dur - fout));
        }

        private static string DoubleRate(string rate)
        {
            rate = (rate ?? string.Empty).Trim();
            if (rate.Length == 0)
            {
                return rate;
            }

            var last = rate[rate.Length - 1];
            if ("kKmMgG".IndexOf(last) >= 0 &&
                double.TryParse(rate.Substring(0, rate.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
            {
                return (num * 2).ToString("g", CultureInfo.InvariantCulture) + last;
            }

            if (double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain))
            {
                return (plain * 2).ToString("g", CultureInfo.InvariantCulture);
            }

            return rate;
        }

        private string AudioMap(string src, ThemeMakerOptions cfg)
        {
            var amap = "0:a:0?";
            if (!string.IsNullOrWhiteSpace(cfg.AudioLang))
            {
                var idx = ProbeAudioIndex(src, cfg.AudioLang);
                if (idx.HasValue)
                {
                    amap = "0:a:" + idx.Value + "?";
                }
                else
                {
                    // No stream tagged with the requested language — fall back to the first, but say
                    // so, since a wrong tag (e.g. "english"/"jp" instead of "eng"/"jpn") is silent otherwise.
                    _logger.Info("[ThemeMaker] no audio stream tagged '{0}' in {1}; using the first stream",
                        cfg.AudioLang, src);
                }
            }

            return amap;
        }

        private List<string> BuildVideoArgs(string src, double start, double dur, string outTmp,
                                            string outName, ThemeMakerOptions cfg)
        {
            var (fin, fout, foutStart) = FadeArgs(dur, cfg);
            var ci = CultureInfo.InvariantCulture;

            // never upscale; force even dimensions + yuv420p for broad player compatibility
            var vf =
                "scale=-2:'2*trunc(min(" + cfg.MaxHeight + ",ih)/2)'," +
                "fade=t=in:st=0:d=" + fin.ToString("0.000", ci) + "," +
                "fade=t=out:st=" + foutStart.ToString("0.000", ci) + ":d=" + fout.ToString("0.000", ci) + "," +
                "format=yuv420p";
            var af =
                "afade=t=in:st=0:d=" + fin.ToString("0.000", ci) + "," +
                "afade=t=out:st=" + foutStart.ToString("0.000", ci) + ":d=" + fout.ToString("0.000", ci);

            var fmt = FormatByExt.TryGetValue(Path.GetExtension(outName) ?? string.Empty, out var f) ? f : "mp4";

            var cmd = new List<string>
            {
                "-hide_banner", "-nostdin", "-y",
                "-ss", start.ToString("0.000", ci), "-i", src, "-t", dur.ToString("0.000", ci),
                "-map", "0:v:0", "-map", AudioMap(src, cfg),
                "-dn", "-map_chapters", "-1",
                "-vf", vf,
                "-af", af,
                "-c:v", "libx264", "-preset", cfg.Preset.ToString().ToLowerInvariant(), "-crf", cfg.Crf.ToString(ci),
                "-profile:v", "high", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", cfg.AudioBitrate, "-ac", "2", "-ar", "48000",
                "-max_muxing_queue_size", "1024",
            };

            if (!string.IsNullOrWhiteSpace(cfg.MaxRate))
            {
                cmd.Add("-maxrate");
                cmd.Add(cfg.MaxRate);
                cmd.Add("-bufsize");
                cmd.Add(DoubleRate(cfg.MaxRate));
            }

            if (fmt == "mp4" || fmt == "mov")
            {
                cmd.Add("-movflags");
                cmd.Add("+faststart");
            }

            cmd.Add("-f");
            cmd.Add(fmt);
            cmd.Add(outTmp);
            return cmd;
        }

        private List<string> BuildAudioArgs(string src, double start, double dur, string outTmp,
                                            string outName, ThemeMakerOptions cfg)
        {
            var (fin, fout, foutStart) = FadeArgs(dur, cfg);
            var ci = CultureInfo.InvariantCulture;
            var af =
                "afade=t=in:st=0:d=" + fin.ToString("0.000", ci) + "," +
                "afade=t=out:st=" + foutStart.ToString("0.000", ci) + ":d=" + fout.ToString("0.000", ci);

            var (muxer, acodec) = AudioCodecByExt.TryGetValue(Path.GetExtension(outName) ?? string.Empty, out var pair)
                ? pair
                : ("mp3", "libmp3lame");

            return new List<string>
            {
                "-hide_banner", "-nostdin", "-y",
                "-ss", start.ToString("0.000", ci), "-i", src, "-t", dur.ToString("0.000", ci),
                "-vn", "-dn", "-map", AudioMap(src, cfg),
                "-map_chapters", "-1",
                "-af", af,
                "-c:a", acodec, "-b:a", cfg.AudioBitrate, "-ac", "2", "-ar", "48000",
                "-f", muxer, outTmp,
            };
        }

        // ------------------------------------------------------------------ #
        // Process execution
        // ------------------------------------------------------------------ #
        private const int MaxRuntimeMinutes = 30;

        private int RunFfmpeg(string ffmpeg, List<string> args, out string stderrTail, out bool timedOut,
                              CancellationToken ct)
        {
            timedOut = false;
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.Arguments = string.Join(" ", args.Select(QuoteArg));

            using (var proc = new Process { StartInfo = psi })
            {
                var errBuf = new StringBuilder();
                proc.Start();

                // Drain stderr (ffmpeg logs there) so the pipe never blocks.
                var errTask = System.Threading.Tasks.Task.Run(() =>
                {
                    string line;
                    while ((line = proc.StandardError.ReadLine()) != null)
                    {
                        errBuf.AppendLine(line);
                    }
                });
                var outTask = System.Threading.Tasks.Task.Run(() => proc.StandardOutput.ReadToEnd());

                // Safety net: theme clips are seconds long, so an encode running for many minutes
                // means ffmpeg has wedged. Kill it so a stuck encode can't hang the scheduled task
                // forever even if the task is never cancelled. Uses monotonic TickCount.
                const int maxRuntimeMs = MaxRuntimeMinutes * 60 * 1000;
                int startTick = Environment.TickCount;

                while (!proc.WaitForExit(250))
                {
                    if (ct.IsCancellationRequested)
                    {
                        try { proc.Kill(); } catch { /* best effort */ }
                        try { proc.WaitForExit(); } catch { /* already gone */ }
                        // Let the readers settle against the now-closed pipes before the process is
                        // disposed, so their in-flight reads don't fault with ObjectDisposedException.
                        try { errTask.Wait(1000); } catch { /* best effort */ }
                        try { outTask.Wait(1000); } catch { /* best effort */ }
                        throw new OperationCanceledException(ct);
                    }

                    if (unchecked(Environment.TickCount - startTick) > maxRuntimeMs)
                    {
                        try { proc.Kill(); } catch { /* best effort */ }
                        timedOut = true;
                        errBuf.AppendLine("(killed: exceeded " + MaxRuntimeMinutes + " min runtime cap)");
                        break;
                    }
                }

                // Ensure the process is fully settled (esp. after a Kill) before reading ExitCode.
                try { proc.WaitForExit(); } catch { /* already exited */ }

                // The pipes are closed once the process has exited, so the readers complete promptly;
                // wait unbounded before touching errBuf so the read is not racing the drain thread.
                try { errTask.Wait(); } catch { /* reader faulted; use whatever was buffered */ }
                try { outTask.Wait(); } catch { /* stdout is unused */ }

                // Last 3 non-empty stderr lines (ffmpeg's error is usually at the tail).
                var lines = errBuf.ToString()
                    .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.TrimEnd('\r'))
                    .Where(s => s.Length > 0)
                    .ToList();
                var tailStart = Math.Max(0, lines.Count - 3);
                stderrTail = string.Join(" | ", lines.GetRange(tailStart, lines.Count - tailStart));
                int code;
                try { code = proc.ExitCode; } catch { code = -1; }
                return code;
            }
        }

        /// <summary>Return the audio-relative index of the first stream tagged with the given language.</summary>
        private int? ProbeAudioIndex(string path, string lang)
        {
            var ffprobe = FfprobePath;
            if (string.IsNullOrEmpty(ffprobe) || !File.Exists(ffprobe))
            {
                return null;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffprobe,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                var args = new[]
                {
                    "-v", "error", "-select_streams", "a",
                    "-show_entries", "stream=index:stream_tags=language",
                    "-of", "json", path,
                };
                psi.Arguments = string.Join(" ", args.Select(QuoteArg));

                using (var proc = new Process { StartInfo = psi })
                {
                    proc.Start();

                    // Drain both pipes on background tasks so ffprobe can never block writing to a
                    // full stderr buffer while we read stdout (a classic single-threaded-drain
                    // deadlock). Kill and bail if it overruns the timeout so a wedged ffprobe never
                    // holds its encode slot forever.
                    var outTask = System.Threading.Tasks.Task.Run(() => proc.StandardOutput.ReadToEnd());
                    var errTask = System.Threading.Tasks.Task.Run(() => proc.StandardError.ReadToEnd());

                    if (!proc.WaitForExit(60000))
                    {
                        try { proc.Kill(); } catch { /* best effort */ }
                        try { proc.WaitForExit(); } catch { /* already gone */ }
                        _logger.Debug("[ThemeMaker] ffprobe audio-lang detect timed out for {0}", path);
                        return null;
                    }

                    outTask.Wait(2000);
                    return FindLangIndex(outTask.Status == System.Threading.Tasks.TaskStatus.RanToCompletion
                        ? outTask.Result : string.Empty, lang);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("[ThemeMaker] ffprobe audio-lang detect failed for {0}: {1}", path, ex.Message);
                return null;
            }
        }

        // Minimal parse of ffprobe's -of json output: streams array, in order; return the
        // 0-based position of the first whose tags.language matches (case-insensitive).
        private static int? FindLangIndex(string json, string lang)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            // We avoid a JSON dependency: scan for "language": "<x>" occurrences in order.
            // Each audio stream object contributes at most one language tag; their order in
            // the "streams" array is the audio-relative order because we filtered -select_streams a.
            int rel = 0;
            int idx = 0;
            while (true)
            {
                int braceStreams = json.IndexOf("\"index\"", idx, StringComparison.Ordinal);
                if (braceStreams < 0)
                {
                    return null;
                }

                // find this stream's language (if any) before the next "index"
                int nextStream = json.IndexOf("\"index\"", braceStreams + 1, StringComparison.Ordinal);
                int end = nextStream < 0 ? json.Length : nextStream;
                int langPos = json.IndexOf("\"language\"", braceStreams, StringComparison.Ordinal);
                if (langPos >= 0 && langPos < end)
                {
                    int colon = json.IndexOf(':', langPos);
                    int q1 = json.IndexOf('"', colon + 1);
                    int q2 = q1 >= 0 ? json.IndexOf('"', q1 + 1) : -1;
                    if (q1 >= 0 && q2 > q1)
                    {
                        var value = json.Substring(q1 + 1, q2 - q1 - 1);
                        if (string.Equals(value, lang, StringComparison.OrdinalIgnoreCase))
                        {
                            return rel;
                        }
                    }
                }

                rel++;
                idx = braceStreams + 1;
            }
        }

        private static void SafeDelete(string p)
        {
            try
            {
                if (File.Exists(p))
                {
                    File.Delete(p);
                }
            }
            catch
            {
                /* best effort */
            }
        }

        /// <summary>Quote an argv token for ProcessStartInfo.Arguments (netstandard2.0 has no ArgumentList).</summary>
        private static string QuoteArg(string arg)
        {
            if (!string.IsNullOrEmpty(arg) && arg.IndexOfAny(new[] { ' ', '\t', '"', '\\' }) < 0)
            {
                return arg;
            }

            var sb = new StringBuilder();
            sb.Append('"');
            for (int i = 0; i < arg.Length; i++)
            {
                int backslashes = 0;
                while (i < arg.Length && arg[i] == '\\')
                {
                    backslashes++;
                    i++;
                }

                if (i == arg.Length)
                {
                    sb.Append('\\', backslashes * 2);
                    break;
                }

                if (arg[i] == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(arg[i]);
                }
            }

            sb.Append('"');
            return sb.ToString();
        }
    }
}

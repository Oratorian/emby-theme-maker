using System.Collections.Generic;
using MediaBrowser.Controller.Entities;

namespace EmbyThemeMaker.Theme
{
    /// <summary>
    /// One thing to generate a theme for: a series (whose sources are its episodes) or a movie
    /// (whose single source is itself). OutDir is the folder that receives backdrops/theme.mp4 and
    /// theme.mp3 — the series root or the movie's own folder.
    /// </summary>
    internal sealed class WorkUnit
    {
        public WorkUnit(string name, string outDir, bool isMovie, List<BaseItem> sources)
        {
            Name = name;
            OutDir = outDir;
            IsMovie = isMovie;
            Sources = sources;
        }

        public string Name { get; }
        public string OutDir { get; }
        public bool IsMovie { get; }
        public List<BaseItem> Sources { get; }
    }

    /// <summary>
    /// A source item that carries usable intro markers and a readable local file. For a series this
    /// is an Episode; for a movie it is the Movie itself. Season/Number are only meaningful for
    /// episodes (a movie reports 0/0) and are used purely for tie-break ordering and logging.
    /// </summary>
    internal sealed class Candidate
    {
        public BaseItem Item { get; set; }
        public string LocalPath { get; set; }
        public double Start { get; set; }
        public double End { get; set; }

        public double Duration => End - Start;

        public int Season => Item?.ParentIndexNumber ?? 0;
        public int Number => Item?.IndexNumber ?? 0;
    }

    /// <summary>What kind of output to write and where.</summary>
    internal enum TargetKind
    {
        Video,
        Audio,
    }

    internal sealed class Target
    {
        public TargetKind Kind { get; set; }
        public string OutDir { get; set; }   // backdrops/ for video, series root for audio
        public string OutName { get; set; }  // theme.mp4 / theme.mp3

        public string OutPath => System.IO.Path.Combine(OutDir, OutName);
    }

    internal enum ResultStatus
    {
        Created,
        Skipped,
        Error,
        WouldCreate,   // preview
        NoSource,
    }

    internal sealed class ThemeResult
    {
        public string SeriesName { get; set; }
        public ResultStatus Status { get; set; }
        public string Detail { get; set; } = string.Empty;
        public TargetKind? Kind { get; set; }

        public static ThemeResult Make(string series, ResultStatus status, string detail, TargetKind? kind = null)
            => new ThemeResult { SeriesName = series, Status = status, Detail = detail, Kind = kind };
    }
}

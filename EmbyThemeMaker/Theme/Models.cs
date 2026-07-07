using MediaBrowser.Controller.Entities.TV;

namespace EmbyThemeMaker.Theme
{
    /// <summary>An episode that carries usable intro markers and a readable local file.</summary>
    internal sealed class Candidate
    {
        public Episode Episode { get; set; }
        public string LocalPath { get; set; }
        public double Start { get; set; }
        public double End { get; set; }

        public double Duration => End - Start;

        public int Season => Episode?.ParentIndexNumber ?? 0;
        public int Number => Episode?.IndexNumber ?? 0;
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

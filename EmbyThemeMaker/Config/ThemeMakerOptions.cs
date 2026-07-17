using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Validation;
using MediaBrowser.Model.Attributes;

namespace EmbyThemeMaker.Config
{
    /// <summary>
    /// Settings and single source of configuration for the Theme Maker plugin. Emby renders this
    /// natively (code-driven UI, no HTML/JS) and the scheduled tasks read its values directly.
    ///
    /// Ported from emby_theme_maker.py, minus the settings that are meaningless in-process:
    /// no api_key / url (we call Emby directly), and no path_from/path_to (Emby gives us real
    /// local paths for every item, so there is nothing to rewrite).
    /// </summary>
    public class ThemeMakerOptions : EditableOptionsBase
    {
        public override string EditorTitle => "Theme Maker";

        public override string EditorDescription =>
            "Turn Emby intro markers into per-series theme media. For each series, a representative " +
            "episode's [IntroStart -> IntroEnd] span is cut with the server's built-in ffmpeg into a " +
            "video backdrop (backdrops/theme.mp4) and/or theme music (theme.mp3). " +
            "Run it from Scheduled Tasks: \"Theme Maker: Generate\" writes files; " +
            "\"Theme Maker: Preview (read-only)\" logs what it would do without encoding.";

        // ----- What to generate -----

        [DisplayName("Output mode")]
        [Description("video = backdrops/theme.mp4; audio = theme.mp3 in the series root; both = both in one pass.")]
        public ThemeMode Mode { get; set; } = ThemeMode.Video;

        [DisplayName("Overwrite existing themes")]
        [Description("Regenerate even when a series already has a theme video/audio. Default: skip existing.")]
        public bool Force { get; set; } = false;

        // ----- Scope -----

        public CaptionItem ScopeCaption { get; set; } = new CaptionItem("Scope");

        [DisplayName("Include movies")]
        [Description("Also generate themes for movies. Emby only auto-creates intro markers for episodes, so " +
                     "a movie is skipped unless it has IntroStart/IntroEnd chapter markers (e.g. added manually " +
                     "via the Chapter Editor). Series are always processed. Default: off (series only).")]
        public bool IncludeMovies { get; set; } = false;

        [DisplayName("Only under this folder")]
        [Description("If set, only process items whose folder is under this local path. Leave empty to process everything.")]
        [EditFolderPicker]
        public string OnlyUnderPath { get; set; } = string.Empty;

        [DisplayName("Limit (0 = no limit)")]
        [Description("Process at most this many series per run (useful for a first test run). 0 means no limit.")]
        [MinValue(0)]
        public int Limit { get; set; } = 0;

        // ----- Intro selection -----

        public CaptionItem SelectionCaption { get; set; } = new CaptionItem("Intro selection");

        [DisplayName("Source episode")]
        [Description("Which episode to cut the theme from: Median (intro closest to the median duration, robust) or First.")]
        public SourcePref Prefer { get; set; } = SourcePref.Median;

        [DisplayName("Minimum intro length (seconds)")]
        [Description("Ignore intros shorter than this.")]
        [MinValue(0)]
        public double MinIntro { get; set; } = 8.0;

        [DisplayName("Maximum intro length (seconds)")]
        [Description("Ignore intros longer than this.")]
        [MinValue(0)]
        public double MaxIntro { get; set; } = 150.0;

        [DisplayName("Pad start (seconds)")]
        [Description("Start this many seconds before IntroStart.")]
        [MinValue(0)]
        public double PadStart { get; set; } = 0.0;

        [DisplayName("Pad end (seconds)")]
        [Description("Extend this many seconds past IntroEnd.")]
        [MinValue(0)]
        public double PadEnd { get; set; } = 0.0;

        [DisplayName("Preferred audio language")]
        [Description("Optional 3-letter tag (e.g. jpn, eng). Picks that audio stream if present; blank = first stream.")]
        public string AudioLang { get; set; } = string.Empty;

        // ----- Encode -----

        public CaptionItem EncodeCaption { get; set; } = new CaptionItem("Encoding");

        [DisplayName("Max video height")]
        [Description("Downscale to this height if the source is taller (never upscales). Default 1080.")]
        [MinValue(64)]
        public int MaxHeight { get; set; } = 1080;

        [DisplayName("Video quality (CRF)")]
        [Description("libx264 CRF. Lower = higher quality/larger. Default 20.")]
        [MinValue(0)]
        [MaxValue(51)]
        public int Crf { get; set; } = 20;

        [DisplayName("Encoder preset")]
        [Description("libx264 preset. Slower = smaller files for the same quality.")]
        public X264Preset Preset { get; set; } = X264Preset.Slow;

        [DisplayName("Peak bitrate cap")]
        [Description("Optional VBV cap on peak video bitrate, e.g. 4M or 3500k. Blank = uncapped.")]
        public string MaxRate { get; set; } = string.Empty;

        [DisplayName("Audio bitrate")]
        [Description("AAC/theme audio bitrate. Default 192k.")]
        public string AudioBitrate { get; set; } = "192k";

        [DisplayName("Fade in (seconds)")]
        [MinValue(0)]
        public double FadeIn { get; set; } = 0.5;

        [DisplayName("Fade out (seconds)")]
        [MinValue(0)]
        public double FadeOut { get; set; } = 1.0;

        // ----- Output names -----

        public CaptionItem OutputCaption { get; set; } = new CaptionItem("Output files");

        [DisplayName("Backdrop subfolder")]
        [Description("Subfolder in the series dir for the video backdrop. Default: backdrops.")]
        public string OutDirName { get; set; } = "backdrops";

        [DisplayName("Video filename")]
        [Description("Backdrop video filename. Default: theme.mp4.")]
        public string OutName { get; set; } = "theme.mp4";

        [DisplayName("Audio filename")]
        [Description("Theme-music filename, written to the SERIES ROOT. Default: theme.mp3 (.m4a/.flac/.opus/.ogg/.aac/.wav also supported).")]
        public string AudioOutName { get; set; } = "theme.mp3";

        // ----- Execution -----

        public CaptionItem ExecCaption { get; set; } = new CaptionItem("Execution");

        [DisplayName("Parallel encodes")]
        [Description("How many ffmpeg encodes to run at once. Default 2.")]
        [MinValue(1)]
        [MaxValue(16)]
        public int Jobs { get; set; } = 2;

        [DisplayName("Scan library after generating")]
        [Description("Request one Emby library scan at the end so new theme files are registered (a per-item refresh does not pick up theme media).")]
        public bool RefreshAfter { get; set; } = true;

        protected override void Validate(ValidationContext context)
        {
            if (MaxIntro < MinIntro)
            {
                context.AddValidationError(nameof(MaxIntro),
                    "Maximum intro length must be greater than or equal to the minimum.");
            }

            if ((Mode == ThemeMode.Video || Mode == ThemeMode.Both) && string.IsNullOrWhiteSpace(OutName))
            {
                context.AddValidationError(nameof(OutName), "Video filename is required.");
            }

            if ((Mode == ThemeMode.Audio || Mode == ThemeMode.Both) && string.IsNullOrWhiteSpace(AudioOutName))
            {
                context.AddValidationError(nameof(AudioOutName), "Audio filename is required for audio/both modes.");
            }
        }
    }

    public enum ThemeMode
    {
        Video,
        Audio,
        Both,
    }

    public enum SourcePref
    {
        Median,
        First,
    }

    // libx264 presets. Names map 1:1 to the ffmpeg preset strings (lowercased).
    public enum X264Preset
    {
        UltraFast,
        SuperFast,
        VeryFast,
        Faster,
        Fast,
        Medium,
        Slow,
        Slower,
        VerySlow,
    }
}

using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace EmbyThemeMaker.Theme
{
    /// <summary>
    /// Resolves a STRM wrapper only at the point where Generate is about to open media. A wrapper
    /// may contain one absolute HTTP or HTTPS target. Local media paths pass through unchanged.
    /// </summary>
    public static class StrmResolver
    {
        // The reader never accepts more than this small fixed amount of wrapper data.
        public const int MaximumBytes = 64 * 1024;
        public const string RedactedTarget = "[STRM target redacted]";
        private static readonly Regex HttpUri = new Regex(@"https?://[^\s""']+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static StrmResolution Resolve(string path, CancellationToken cancellationToken)
        {
            if (!IsStrmPath(path))
            {
                return StrmResolution.Success(path, false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes;
            try
            {
                bytes = ReadBounded(path, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (WrapperTooLargeException)
            {
                return StrmResolution.Failure(StrmResolveError.TooLarge);
            }
            catch (DecoderFallbackException)
            {
                return StrmResolution.Failure(StrmResolveError.InvalidEncoding);
            }
            catch
            {
                return StrmResolution.Failure(StrmResolveError.Unreadable);
            }

            string text;
            try
            {
                text = new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return StrmResolution.Failure(StrmResolveError.InvalidEncoding);
            }

            if (text.Length > 0 && text[0] == '\ufeff')
            {
                text = text.Substring(1);
            }

            string target = null;
            using (var reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var candidate = line.Trim();
                    // A documented wrapper comment starts with # after optional whitespace.
                    if (candidate.Length == 0 || candidate.StartsWith("#", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (target != null)
                    {
                        return StrmResolution.Failure(StrmResolveError.MultipleTargets);
                    }

                    target = candidate;
                }
            }

            if (target == null)
            {
                return StrmResolution.Failure(StrmResolveError.NoTarget);
            }

            Uri uri;
            if (!Uri.TryCreate(target, UriKind.Absolute, out uri))
            {
                return StrmResolution.Failure(StrmResolveError.InvalidTarget);
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return StrmResolution.Failure(StrmResolveError.UnsupportedScheme);
            }

            if (string.IsNullOrEmpty(uri.Host))
            {
                return StrmResolution.Failure(StrmResolveError.InvalidTarget);
            }

            return StrmResolution.Success(target, true);
        }

        /// <summary>Removes a target and any HTTP(S) URI from messages that can reach logs or results.</summary>
        public static string RedactForLog(string message, string target)
        {
            var safe = message ?? string.Empty;
            if (!string.IsNullOrEmpty(target))
            {
                safe = safe.Replace(target, RedactedTarget);
            }

            return HttpUri.Replace(safe, RedactedTarget);
        }

        private static bool IsStrmPath(string path)
            => !string.IsNullOrEmpty(path) && path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase);

        private static byte[] ReadBounded(string path, CancellationToken cancellationToken)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return ReadBounded(input, cancellationToken);
            }
        }

        internal static byte[] ReadBounded(Stream input, CancellationToken cancellationToken)
        {
            using (var output = new MemoryStream())
            {
                if (input.CanSeek && input.Length > MaximumBytes)
                {
                    throw new WrapperTooLargeException();
                }

                var buffer = new byte[4096];
                int total = 0;
                while (total < MaximumBytes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var remaining = MaximumBytes - total;
                    var read = input.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    output.Write(buffer, 0, read);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (input.CanSeek && input.Length > MaximumBytes)
                {
                    throw new WrapperTooLargeException();
                }

                return output.ToArray();
            }
        }

        private sealed class WrapperTooLargeException : Exception
        {
        }
    }

    public static class StrmResolveError
    {
        public const string TooLarge = "strm-wrapper-too-large";
        public const string InvalidEncoding = "strm-wrapper-invalid-utf8";
        public const string Unreadable = "strm-wrapper-unreadable";
        public const string NoTarget = "strm-wrapper-no-target";
        public const string MultipleTargets = "strm-wrapper-multiple-targets";
        public const string InvalidTarget = "strm-wrapper-invalid-target";
        public const string UnsupportedScheme = "strm-wrapper-unsupported-scheme";
    }

    public sealed class StrmResolution
    {
        private StrmResolution(bool isSuccess, string source, bool isStrmTarget, string errorCategory)
        {
            IsSuccess = isSuccess;
            Source = source;
            IsStrmTarget = isStrmTarget;
            ErrorCategory = errorCategory;
        }

        public bool IsSuccess { get; }
        public string Source { get; }
        public bool IsStrmTarget { get; }
        public string ErrorCategory { get; }

        internal static StrmResolution Success(string source, bool isStrmTarget)
            => new StrmResolution(true, source, isStrmTarget, null);

        internal static StrmResolution Failure(string errorCategory)
            => new StrmResolution(false, null, false, errorCategory);
    }

    /// <summary>Separates Generate-only media opening from all metadata-only task paths.</summary>
    public static class ThemeGenerationPolicy
    {
        public static bool ShouldResolveStrm(bool generate) => generate;
    }

    /// <summary>Caps concurrently active and successfully completed series that create new sidecars.</summary>
    public sealed class SuccessfulGenerationLimiter
    {
        private readonly int _maximum;
        private readonly object _sync = new object();
        private int _active;
        private int _successful;

        public SuccessfulGenerationLimiter(int maximum)
        {
            _maximum = Math.Max(0, maximum);
        }

        public int SuccessfulGenerations
        {
            get
            {
                lock (_sync)
                {
                    return _successful;
                }
            }
        }

        public bool CanGenerate
        {
            get
            {
                lock (_sync)
                {
                    return _maximum == 0 || _successful + _active < _maximum;
                }
            }
        }

        public bool TryBeginUnit()
        {
            lock (_sync)
            {
                if (_maximum > 0 && _successful + _active >= _maximum)
                {
                    return false;
                }

                _active++;
                return true;
            }
        }

        public void CompleteUnit(bool successful)
        {
            lock (_sync)
            {
                if (_active <= 0)
                {
                    return;
                }

                _active--;
                if (successful)
                {
                    _successful++;
                }
            }
        }
    }
}

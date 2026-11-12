using System;
using System.Collections.Generic;
using System.Linq;

namespace StremioXbox.Core
{
    /// <summary>Audio formats, as far as choosing a track goes.</summary>
    public enum AudioFormat
    {
        Other,
        Aac,
        DolbyDigital,
        Dts,
        DolbyDigitalPlus,
        TrueHd,
    }

    /// <summary>One audio track of the file, as the chooser sees it.</summary>
    public sealed class AudioOption
    {
        public AudioOption(string language, string label, AudioFormat format, int channels)
        {
            Language = language ?? string.Empty;
            Label = label ?? string.Empty;
            Format = format;
            Channels = channels;
        }

        /// <summary>Gets the normalized language ("en", "ja"; "und" or empty when untagged).</summary>
        public string Language { get; }

        public string Label { get; }

        public AudioFormat Format { get; }

        public int Channels { get; }

        public bool IsAtmos => Label.IndexOf("atmos", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Gets a value indicating whether this is commentary or audio description, never picked first.</summary>
        public bool IsExtra =>
            Label.IndexOf("comment", StringComparison.OrdinalIgnoreCase) >= 0 ||
            Label.IndexOf("descri", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>The chosen track and why.</summary>
    public sealed class AudioDecision
    {
        public AudioDecision(int index, string language, bool originalLanguage)
        {
            Index = index;
            Language = language;
            OriginalLanguage = originalLanguage;
        }

        public int Index { get; }

        public string Language { get; }

        /// <summary>Gets a value indicating whether a foreign title plays in its own language (with English subtitles).</summary>
        public bool OriginalLanguage { get; }
    }

    /// <summary>
    /// Picks the audio track: the language first, then the best-sounding track the Xbox plays reliably.
    /// Language: a foreign title (by its main country of production) plays in its original language when
    /// the file has it, for English subtitles; otherwise English when the file has it; otherwise the
    /// fallback (Stremio's audio language), then the file's default track. Quality within that language:
    /// Atmos, then more channels, then the format (Dolby Digital Plus, DTS, Dolby Digital, AAC); TrueHD
    /// only when nothing else is there (it is often silent on Xbox); commentary and audio description last.
    /// </summary>
    public static class AudioChoice
    {
        private static readonly HashSet<string> EnglishCountries = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "United States", "USA", "United Kingdom", "UK", "Canada", "Australia", "New Zealand", "Ireland",
        };

        // Cinemeta's country names to the languages a title from there is usually made in.
        private static readonly Dictionary<string, string[]> CountryLanguages = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Japan"] = new[] { "ja" },
            ["South Korea"] = new[] { "ko" },
            ["Korea"] = new[] { "ko" },
            ["China"] = new[] { "zh" },
            ["Hong Kong"] = new[] { "yue", "zh" },
            ["Taiwan"] = new[] { "zh" },
            ["France"] = new[] { "fr" },
            ["Germany"] = new[] { "de" },
            ["West Germany"] = new[] { "de" },
            ["Austria"] = new[] { "de" },
            ["Switzerland"] = new[] { "de", "fr", "it" },
            ["Belgium"] = new[] { "fr", "nl" },
            ["Spain"] = new[] { "es" },
            ["Mexico"] = new[] { "es" },
            ["Argentina"] = new[] { "es" },
            ["Colombia"] = new[] { "es" },
            ["Chile"] = new[] { "es" },
            ["Peru"] = new[] { "es" },
            ["Venezuela"] = new[] { "es" },
            ["Cuba"] = new[] { "es" },
            ["Uruguay"] = new[] { "es" },
            ["Italy"] = new[] { "it" },
            ["Brazil"] = new[] { "pt" },
            ["Portugal"] = new[] { "pt" },
            ["Russia"] = new[] { "ru" },
            ["Soviet Union"] = new[] { "ru" },
            ["Ukraine"] = new[] { "uk" },
            ["India"] = new[] { "hi", "ta", "te", "ml", "kn", "bn", "mr", "pa", "gu" },
            ["Sweden"] = new[] { "sv" },
            ["Denmark"] = new[] { "da" },
            ["Norway"] = new[] { "no", "nb", "nn" },
            ["Finland"] = new[] { "fi" },
            ["Iceland"] = new[] { "is" },
            ["Netherlands"] = new[] { "nl" },
            ["Poland"] = new[] { "pl" },
            ["Czech Republic"] = new[] { "cs" },
            ["Czechia"] = new[] { "cs" },
            ["Hungary"] = new[] { "hu" },
            ["Romania"] = new[] { "ro" },
            ["Greece"] = new[] { "el" },
            ["Turkey"] = new[] { "tr" },
            ["Israel"] = new[] { "he" },
            ["Iran"] = new[] { "fa" },
            ["Egypt"] = new[] { "ar" },
            ["Saudi Arabia"] = new[] { "ar" },
            ["Lebanon"] = new[] { "ar" },
            ["Morocco"] = new[] { "ar" },
            ["Thailand"] = new[] { "th" },
            ["Vietnam"] = new[] { "vi" },
            ["Indonesia"] = new[] { "id" },
            ["Philippines"] = new[] { "tl" },
        };

        /// <summary>Chooses the track. <paramref name="fallbackLanguage"/> is normalized (or empty).</summary>
        public static AudioDecision Choose(IReadOnlyList<AudioOption> tracks, string country, string fallbackLanguage)
        {
            if (tracks == null || tracks.Count == 0)
            {
                return new AudioDecision(-1, string.Empty, false);
            }

            // Commentary and audio description never decide the language.
            var main = Enumerable.Range(0, tracks.Count).Where(i => !tracks[i].IsExtra).ToList();
            if (main.Count == 0)
            {
                main = Enumerable.Range(0, tracks.Count).ToList();
            }

            var original = OriginalLanguage(tracks, main, country);
            if (original != null)
            {
                return new AudioDecision(Best(tracks, original), original, true);
            }

            foreach (var language in new[] { "en", fallbackLanguage, tracks[main[0]].Language })
            {
                if (!string.IsNullOrEmpty(language) && main.Any(i => tracks[i].Language == language))
                {
                    return new AudioDecision(Best(tracks, language), language, false);
                }
            }

            return new AudioDecision(Best(tracks, tracks[main[0]].Language), tracks[main[0]].Language, false);
        }

        /// <summary>Gets a value indicating whether the country makes titles in English (or is unknown).</summary>
        public static bool IsEnglishSpeaking(string country)
        {
            return string.IsNullOrWhiteSpace(country) || EnglishCountries.Contains(country.Trim());
        }

        // The language a foreign title was made in, when the file has it; null for English or unknown titles.
        private static string OriginalLanguage(IReadOnlyList<AudioOption> tracks, List<int> main, string country)
        {
            if (IsEnglishSpeaking(country))
            {
                return null;
            }

            if (CountryLanguages.TryGetValue(country.Trim(), out var languages))
            {
                // The first matching track in file order (the default comes first) settles between
                // several (India: Hindi, Tamil, Telugu...).
                var match = FirstIndex(main, i => languages.Contains(tracks[i].Language));
                return match >= 0 ? tracks[match].Language : null;
            }

            // A country not in the table: trust the file's default track when it is in another language.
            var first = tracks[main[0]].Language;
            return Known(first) && first != "en" ? first : null;
        }

        private static int Best(IReadOnlyList<AudioOption> tracks, string language)
        {
            var best = -1;
            var bestScore = int.MinValue;
            for (var i = 0; i < tracks.Count; i++)
            {
                if (tracks[i].Language != language)
                {
                    continue;
                }

                var score = Quality(tracks[i]) - i; // Ties: the earlier track (the file's default first).
                if (score > bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }

            return best >= 0 ? best : 0;
        }

        /// <summary>Scores a track's sound (higher is better), in steps of 100 so file order only breaks ties.</summary>
        public static int Quality(AudioOption track)
        {
            var channels = Math.Min(Math.Max(track.Channels, 1), 8);
            var format =
                track.Format == AudioFormat.DolbyDigitalPlus ? 6 :
                track.Format == AudioFormat.Dts ? 5 :
                track.Format == AudioFormat.DolbyDigital ? 4 :
                track.Format == AudioFormat.Aac ? 2 : 1;
            var score = (channels * 1000) + (format * 100) + (track.IsAtmos ? 3000 : 0);
            if (track.Format == AudioFormat.TrueHd)
            {
                score -= 100000; // Often silent on Xbox: only when the language has nothing else.
            }

            if (track.IsExtra)
            {
                score -= 1000000;
            }

            return score;
        }

        private static bool Known(string language)
        {
            return !string.IsNullOrEmpty(language) && language != "und" && language != "zxx" && language != "mul";
        }

        private static int FirstIndex(IEnumerable<int> source, Func<int, bool> predicate)
        {
            foreach (var item in source)
            {
                if (predicate(item))
                {
                    return item;
                }
            }

            return -1;
        }
    }
}

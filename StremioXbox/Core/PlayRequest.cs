using System.Collections.Generic;
using Windows.Data.Json;

namespace StremioXbox.Core
{
    /// <summary>A stream handed over from Stremio Web for native playback.</summary>
    public sealed class PlayRequest
    {
        /// <summary>Gets the HTTP(S) URL of the file.</summary>
        public string Url { get; private set; }

        /// <summary>Gets the addon's stream name (often holds quality tags).</summary>
        public string Name { get; private set; }

        /// <summary>Gets the addon's stream description (often holds the file name).</summary>
        public string Description { get; private set; }

        /// <summary>Gets the file name from behaviorHints, if the addon set one.</summary>
        public string FileName { get; private set; }

        /// <summary>Gets a value indicating whether the addon asked for custom request headers.</summary>
        public bool HasRequestHeaders { get; private set; }

        /// <summary>Gets the shell session id; events about this playback are sent back with it.</summary>
        public double Session { get; private set; }

        /// <summary>Gets the title from Stremio (show or movie name), if known.</summary>
        public string Title { get; private set; }

        /// <summary>Gets the episode line (e.g. "S1 E2 · Name"), if any.</summary>
        public string Subtitle { get; private set; }

        /// <summary>Gets the title's backdrop image URL, if any.</summary>
        public string Background { get; private set; }

        /// <summary>Gets where Stremio's library says this video was left, in milliseconds (0 = start).</summary>
        public double StartPositionMs { get; private set; }

        /// <summary>Gets where Stremio says the intro starts, in milliseconds (0 with IntroToMs 0 = unknown).</summary>
        public double IntroFromMs { get; private set; }

        /// <summary>Gets where Stremio says the intro ends, in milliseconds (0 = no intro known).</summary>
        public double IntroToMs { get; private set; }

        /// <summary>Gets the next episode's label, or empty when there is none.</summary>
        public string Next { get; private set; }

        /// <summary>Gets a value indicating whether Stremio's binge watching setting is on.</summary>
        public bool Binge { get; private set; }

        /// <summary>Gets Stremio's preferred subtitle language (ISO 639-2, e.g. "eng"), or empty.</summary>
        public string SubtitlesLanguage { get; private set; }

        /// <summary>Gets Stremio's preferred audio language (ISO 639-2, e.g. "eng"), or empty.</summary>
        public string AudioLanguage { get; private set; }

        /// <summary>Gets the title's main country of production (from Cinemeta), or empty when unknown.</summary>
        public string Country { get; private set; }

        /// <summary>Gets external subtitles collected from the stream and the user's subtitle addons.</summary>
        public IReadOnlyList<SubtitleSource> Subtitles { get; private set; } = new List<SubtitleSource>();

        /// <summary>Builds a request from the shell script's "play" message.</summary>
        public static PlayRequest FromJson(JsonObject message)
        {
            var subtitles = new List<SubtitleSource>();
            if (message.ContainsKey("subtitles") && message["subtitles"].ValueType == JsonValueType.Array)
            {
                foreach (var value in message.GetNamedArray("subtitles"))
                {
                    if (value.ValueType != JsonValueType.Object)
                    {
                        continue;
                    }

                    var sub = value.GetObject();
                    var url = sub.GetNamedString("url", string.Empty);
                    if (url.Length > 0)
                    {
                        subtitles.Add(new SubtitleSource(url, sub.GetNamedString("lang", string.Empty), sub.GetNamedString("label", string.Empty)));
                    }
                }
            }

            return new PlayRequest
            {
                Subtitles = subtitles,
                Url = message.GetNamedString("url", string.Empty),
                Name = message.GetNamedString("name", string.Empty),
                Description = message.GetNamedString("description", string.Empty),
                FileName = message.GetNamedString("filename", string.Empty),
                HasRequestHeaders = message.GetNamedBoolean("hasRequestHeaders", false),
                Session = message.GetNamedNumber("session", 0),
                Title = message.GetNamedString("title", string.Empty),
                Subtitle = message.GetNamedString("subtitle", string.Empty),
                Background = message.GetNamedString("background", string.Empty),
                StartPositionMs = message.GetNamedNumber("startPosition", 0),
                IntroFromMs = message.GetNamedNumber("introFrom", 0),
                IntroToMs = message.GetNamedNumber("introTo", 0),
                Next = message.GetNamedString("next", string.Empty),
                Binge = message.GetNamedBoolean("binge", true),
                SubtitlesLanguage = message.GetNamedString("subtitlesLanguage", string.Empty),
                AudioLanguage = message.GetNamedString("audioLanguage", string.Empty),
                Country = message.GetNamedString("country", string.Empty),
            };
        }

        /// <summary>Gets the main line for the player: Stremio's title, else the stream's own name.</summary>
        public string DisplayTitle => string.IsNullOrEmpty(Title) ? ToString() : Title;

        /// <summary>Gets the second line: the episode, else the addon's stream name (quality tags).</summary>
        public string DisplaySubtitle
        {
            get
            {
                if (string.IsNullOrEmpty(Title))
                {
                    return string.Empty;
                }

                return string.IsNullOrEmpty(Subtitle) ? (Name ?? string.Empty).Replace("\n", " ").Trim() : Subtitle;
            }
        }

        /// <inheritdoc />
        public override string ToString()
        {
            var label = string.IsNullOrEmpty(FileName) ? Description : FileName;
            return (Name + " | " + label).Replace("\n", " ");
        }
    }

    /// <summary>An external subtitle file (SRT/VTT) offered by the stream or an addon.</summary>
    public sealed class SubtitleSource
    {
        public SubtitleSource(string url, string language, string label)
        {
            Url = url;
            Language = language;
            Label = label;
        }

        public string Url { get; }

        public string Language { get; }

        public string Label { get; }
    }
}

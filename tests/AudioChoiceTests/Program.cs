using System;
using System.Collections.Generic;
using StremioXbox.Core;

// Checks the audio track chooser with the track lists real releases ship.
internal static class Program
{
    private static int failures;

    private static int Main()
    {
        // Quality within English.
        Check("DD+ Atmos beats the AC3 default", 1, false, "United States",
            T("en", "", AudioFormat.DolbyDigital, 6), T("en", "Dolby Atmos", AudioFormat.DolbyDigitalPlus, 6));
        Check("7.1 beats 5.1 in the same format", 1, false, "United States",
            T("en", "", AudioFormat.DolbyDigitalPlus, 6), T("en", "", AudioFormat.DolbyDigitalPlus, 8));
        Check("5.1 AC3 beats a stereo AAC default", 1, false, "United States",
            T("en", "", AudioFormat.Aac, 2), T("en", "", AudioFormat.DolbyDigital, 6));
        Check("TrueHD Atmos default loses to DD+", 1, false, "United States",
            T("en", "TrueHD Atmos 7.1", AudioFormat.TrueHd, 8), T("en", "", AudioFormat.DolbyDigitalPlus, 6));
        Check("TrueHD when it is the only English track", 0, false, "United States",
            T("en", "", AudioFormat.TrueHd, 8), T("fr", "", AudioFormat.DolbyDigital, 6));
        Check("Commentary never wins", 0, false, "United States",
            T("en", "", AudioFormat.Aac, 2), T("en", "Director's Commentary", AudioFormat.DolbyDigitalPlus, 6));
        Check("Audio description never wins", 0, false, "United States",
            T("en", "", AudioFormat.DolbyDigital, 6), T("en", "Audio Description", AudioFormat.DolbyDigitalPlus, 8));
        Check("Equal tracks keep the file's default", 0, false, "United States",
            T("en", "", AudioFormat.DolbyDigital, 6), T("en", "", AudioFormat.DolbyDigital, 6));

        // English first.
        Check("English over a German default", 1, false, "United States",
            T("de", "", AudioFormat.DolbyDigitalPlus, 6), T("en", "", AudioFormat.DolbyDigital, 6));
        Check("English when the country is unknown", 1, false, "",
            T("es", "", AudioFormat.DolbyDigital, 6), T("en", "", AudioFormat.Aac, 2));
        Check("Untagged single track still plays", 0, false, "United States",
            T("und", "", AudioFormat.Aac, 2));
        // Check() passes German ("de") as Stremio's audio language.
        Check("Stremio's language when there is no English", 1, false, "United States",
            T("fr", "", AudioFormat.DolbyDigital, 6), T("de", "", AudioFormat.DolbyDigital, 6));

        // Foreign titles: original language, best track in it.
        Check("Anime: Japanese over an English dub default", 1, true, "Japan",
            T("en", "", AudioFormat.DolbyDigitalPlus, 6), T("ja", "", AudioFormat.Aac, 2));
        Check("Korean film: best Korean track", 2, true, "South Korea",
            T("en", "", AudioFormat.DolbyDigital, 6), T("ko", "", AudioFormat.Aac, 2), T("ko", "", AudioFormat.DolbyDigitalPlus, 6));
        Check("French film with only an English dub plays English", 0, false, "France",
            T("en", "", AudioFormat.DolbyDigital, 6));
        Check("Co-production listed Japan first stays Japanese", 1, true, "Japan",
            T("en", "", AudioFormat.DolbyDigital, 6), T("ja", "", AudioFormat.DolbyDigital, 6));
        Check("Hong Kong: Cantonese", 1, true, "Hong Kong",
            T("en", "", AudioFormat.DolbyDigital, 6), T("yue", "", AudioFormat.DolbyDigital, 6), T("zh", "Mandarin", AudioFormat.DolbyDigital, 6));
        Check("India: the first Indian language in the file", 1, true, "India",
            T("en", "", AudioFormat.Aac, 2), T("te", "", AudioFormat.DolbyDigital, 6), T("hi", "", AudioFormat.DolbyDigital, 6));
        Check("Unlisted country: a non-English default is the original", 0, true, "Nigeria",
            T("yo", "", AudioFormat.Aac, 2), T("en", "", AudioFormat.Aac, 2));
        Check("Unlisted country: English default stays English", 0, false, "Nigeria",
            T("en", "", AudioFormat.Aac, 2), T("yo", "", AudioFormat.Aac, 2));
        Check("Foreign commentary track does not decide the language", 2, true, "Japan",
            T("ja", "Commentary", AudioFormat.Aac, 2), T("en", "", AudioFormat.DolbyDigital, 6), T("ja", "", AudioFormat.DolbyDigital, 6));
        Check("Japanese TrueHD loses to Japanese DD+", 2, true, "Japan",
            T("ja", "", AudioFormat.TrueHd, 8), T("en", "", AudioFormat.DolbyDigitalPlus, 6), T("ja", "", AudioFormat.DolbyDigitalPlus, 6));

        Check("Japanese DTS-HD 5.1 loses to Japanese FLAC stereo", 2, true, "Japan",
            T("ru", "", AudioFormat.DolbyDigital, 2), T("ja", "", AudioFormat.DtsHd, 6), T("ja", "", AudioFormat.Other, 2));
        Check("DTS-HD loses to TrueHD in the same language", 1, false, "United States",
            T("en", "", AudioFormat.DtsHd, 8), T("en", "", AudioFormat.TrueHd, 8));
        Check("DTS-HD when it is the only track", 0, true, "Japan",
            T("ja", "", AudioFormat.DtsHd, 2));

        Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static AudioOption T(string language, string label, AudioFormat format, int channels)
    {
        return new AudioOption(language, label, format, channels);
    }

    private static void Check(string name, int index, bool original, string country, params AudioOption[] tracks)
    {
        var decision = AudioChoice.Choose(new List<AudioOption>(tracks), country, "de");
        var ok = decision.Index == index && decision.OriginalLanguage == original;
        if (!ok)
        {
            failures++;
        }

        Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (ok ? string.Empty : string.Format(
            "  (got track {0}, original {1}; wanted track {2}, original {3})", decision.Index, decision.OriginalLanguage, index, original)));
    }
}

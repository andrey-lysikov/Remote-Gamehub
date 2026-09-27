//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;

namespace RemoteGameHub.App;

// What the person sees, in the host's language: Russian when Windows is Russian, else English.
// The English text is the key; the log stays English.
internal static partial class Text
{
    // The language Windows was set to, not the account's: the worker runs as SYSTEM.
    internal static bool Russian { get; set; } =
        CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "ru";

    internal static string Language => Russian ? "ru" : "en";

    internal static string T(string english) =>
        Russian && TextRu.Strings.TryGetValue(english, out var russian) ? russian : english;

    internal static string T(string english, params object?[] values) =>
        string.Format(CultureInfo.InvariantCulture, T(english), values);

    // [[English]] in the page's markup and script, replaced once when they are read.
    [GeneratedRegex(@"\[\[(.+?)\]\]")]
    internal static partial Regex Marker { get; }

    internal static string Translate(string asset) => Marker.Replace(asset, match => T(match.Groups[1].Value));
}

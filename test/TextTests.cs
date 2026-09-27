//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using RemoteGameHub.App;
using Xunit;

namespace RemoteGameHub.Tests;

internal static class EnglishForTests
{
    // Tests assert English texts whatever language this machine's Windows is in.
    [ModuleInitializer]
    internal static void Initialize() => Text.Russian = false;
}

// The Russian texts: one for every English text asked for, and none left over.
public class TextTests
{
    // Keys built at run time, which no literal in the sources shows.
    private static readonly string[] Dynamic =
    {
        "by hand", "folder",
        "There is no such game.", "This game was added by hand; there is nothing to reset it to.",
        "Reset. The game is being scanned again…",
        "This server needs Windows 11 or newer.", "This server needs administrator rights and does not have them.",
        "The configuration file has a mistake and was not read:", "The configuration file could not be read:",
        "The server could not open its ports.",
    };

    private static string Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "sample.conf")))
            directory = directory.Parent;

        return directory!.FullName;
    }

    private static string[] PageKeys(string file) =>
        Text.Marker.Matches(File.ReadAllText(Path.Combine(Repository(), "src", "Web", file)))
            .Select(match => match.Groups[1].Value).ToArray();

    private static IEnumerable<string> CodeKeys()
    {
        var call = new Regex("""Text\.T\(\s*((?:"(?:[^"\\]|\\.)*"\s*\+?\s*)+)""");
        var literal = new Regex("""
            "((?:[^"\\]|\\.)*)"
            """.Trim());

        foreach (var file in Directory.EnumerateFiles(Path.Combine(Repository(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match match in call.Matches(File.ReadAllText(file)))
            {
                yield return string.Concat(literal.Matches(match.Groups[1].Value)
                    .Select(part => Regex.Unescape(part.Groups[1].Value)));
            }
        }
    }

    private static HashSet<string> AllKeys() =>
        PageKeys("page.html").Concat(PageKeys("page.js")).Concat(CodeKeys()).Concat(Dynamic).ToHashSet();

    [Fact]
    public void Every_text_has_a_russian_translation()
    {
        var missing = AllKeys().Where(key => !TextRu.Strings.ContainsKey(key)).ToArray();
        Assert.Empty(missing);
    }

    [Fact]
    public void No_translation_is_left_over()
    {
        var used = AllKeys();
        Assert.DoesNotContain(TextRu.Strings.Keys, key => !used.Contains(key));
    }

    [Fact]
    public void A_translation_keeps_the_placeholders_and_the_markup()
    {
        var pieces = new Regex(@"\{\{?\w+\}\}?|</?\w+[^>]*>");

        foreach (var (english, russian) in TextRu.Strings)
        {
            var wanted = pieces.Matches(english).Select(m => m.Value).Order().ToArray();
            var found = pieces.Matches(russian).Select(m => m.Value).Order().ToArray();
            Assert.True(wanted.SequenceEqual(found), $"\"{english}\" → \"{russian}\"");
        }
    }

    [Fact]
    public void A_translation_used_in_the_script_does_not_break_its_quotes()
    {
        foreach (var key in PageKeys("page.js"))
            Assert.DoesNotContain("'", TextRu.Strings[key]);
    }

    [Fact]
    public void English_is_the_key_and_the_fallback()
    {
        Assert.Equal("Save", Text.T("Save"));
        Assert.Equal("7 games", Text.T("{0} games", 7));
        Assert.Equal("<b>Save</b>", Text.Translate("<b>[[Save]]</b>"));
        Assert.Equal("no such text", Text.T("no such text"));
    }
}

using System.Text.RegularExpressions;

namespace PuTTY.Sessions.Tests;

/// <summary>
/// The licence notices: PuTTY's licence must travel with PuTTY Modern word for word, and the source files that adapt PuTTY's
/// code must say whose it is. These read the repository's own files, so a PuTTY update in the submodule or a new ported file
/// that is not marked fails here instead of shipping without its notice.
/// </summary>
[TestClass]
public sealed class NoticesTests
{
    // The engine files that adapt or replace PuTTY's own code (see their headers).
    private static readonly string[] PortedFiles =
    {
        "keys.c", "termwin.c", "session.c", "frontend.c", "eventloop.c", "configbox.c", "handlewait.c",
    };

    [TestMethod]
    public void The_notices_file_has_PuTTYs_licence_word_for_word()
    {
        string licence = Normalize(File.ReadAllText(Path.Combine(RepoRoot(), "external", "putty", "LICENCE")));
        string notices = Normalize(File.ReadAllText(Path.Combine(RepoRoot(), "THIRD-PARTY-NOTICES.txt")));

        Assert.IsTrue(licence.Contains("Portions copyright Robert de Bath"), "the pinned PuTTY LICENCE was not read as expected");
        Assert.IsTrue(notices.Contains(licence),
            "THIRD-PARTY-NOTICES.txt does not contain external/putty/LICENCE word for word: PuTTY was updated, copy the new LICENCE into it");
    }

    [TestMethod]
    public void The_notices_file_names_every_component_that_ships()
    {
        string notices = File.ReadAllText(Path.Combine(RepoRoot(), "THIRD-PARTY-NOTICES.txt"));

        foreach (string expected in new[]
                 {
                     "Simon Tatham", "Digi21", ".NET Foundation and Contributors", "Copyright (c) Microsoft Corporation",
                     "CommunityToolkit.Mvvm", "Microsoft Windows App SDK", "not made or endorsed by Simon Tatham",
                 })
        {
            Assert.IsTrue(notices.Contains(expected), $"THIRD-PARTY-NOTICES.txt does not mention \"{expected}\"");
        }

        Assert.IsTrue(File.Exists(Path.Combine(RepoRoot(), "notices", "dotnet-runtime-THIRD-PARTY-NOTICES.txt")),
            "the .NET runtime's own third-party notices are missing");
    }

    [TestMethod]
    public void The_files_that_adapt_PuTTYs_code_say_so()
    {
        foreach (string name in PortedFiles)
        {
            string path = Path.Combine(RepoRoot(), "native", "engine", name);
            string head = File.ReadAllText(path);
            head = head[..Math.Min(head.Length, 1500)];

            Assert.IsTrue(head.Contains("Simon Tatham and the PuTTY contributors"), $"{name} does not name Simon Tatham in its header");
            Assert.IsTrue(head.Contains("Andrij Abyzov"), $"{name} does not name its own author in its header");
            Assert.IsTrue(head.Contains("SPDX-License-Identifier: MIT"), $"{name} has no SPDX line in its header");
        }
    }

    [TestMethod]
    public void The_readme_credits_PuTTY_and_says_it_is_independent()
    {
        string readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));

        Assert.IsTrue(readme.Contains("Simon Tatham"));
        Assert.IsTrue(readme.Contains("not affiliated with or endorsed by"));
        Assert.IsTrue(readme.Contains("THIRD-PARTY-NOTICES.txt"));
    }

    /// <summary>Whitespace runs become one space, so line breaks and indentation do not matter.</summary>
    private static string Normalize(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    private static string RepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PuTTY.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("PuTTY.slnx was not found above " + AppContext.BaseDirectory);
    }
}

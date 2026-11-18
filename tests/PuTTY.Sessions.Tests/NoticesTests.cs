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

    [TestMethod]
    public void The_about_credit_has_the_years_of_PuTTYs_licence()
    {
        string licence = File.ReadAllText(Path.Combine(RepoRoot(), "external", "putty", "LICENCE"));
        Match years = Regex.Match(licence, @"copyright (\d{4})-(\d{4}) Simon Tatham");

        Assert.IsTrue(years.Success, "the first line of PuTTY's LICENCE is not in the form it was");
        Assert.IsTrue(AboutText.Credit.Contains($"{years.Groups[1].Value}–{years.Groups[2].Value} Simon Tatham and the PuTTY contributors"),
            "the About text's years differ from PuTTY's LICENCE: " + years.Value);
    }

    [TestMethod]
    public void The_about_text_names_the_product_both_authors_and_the_independence()
    {
        Assert.AreEqual("PuTTY Modern", AboutText.Product);
        Assert.IsTrue(AboutText.Copyright.Contains("Andrij Abyzov"));
        Assert.IsTrue(AboutText.Copyright.StartsWith("Copyright © "), "a notice needs the word or the sign, not (c)");
        Assert.IsTrue(AboutText.Credit.Contains("Simon Tatham and the PuTTY contributors"));
        Assert.IsTrue(AboutText.Credit.Contains("used under the MIT licence"));
        Assert.IsTrue(AboutText.Credit.Contains("PuTTY Modern is an independent project, not made or endorsed by them"));
        Assert.IsTrue(AboutText.Summary.Contains("includes PuTTY's core"), "the code is included, not 'based on'");
        Assert.IsFalse(AboutText.Summary.Contains("backend"), "PuTTY's backend is only its protocol layer");
        Assert.IsTrue(AboutText.OfficialPuttyUrl.StartsWith("https://www.chiark.greenend.org.uk/~sgtatham/putty/"));
    }

    [TestMethod]
    public void The_licences_text_has_our_licence_then_the_notices_then_the_dotnet_notices()
    {
        string text = AboutText.LicencesText("OWN LICENCE\r\n", "THE NOTICES\r\n", "DOTNET NOTICES\r\n");

        int own = text.IndexOf("OWN LICENCE", StringComparison.Ordinal);
        int notices = text.IndexOf("THE NOTICES", StringComparison.Ordinal);
        int dotnet = text.IndexOf("DOTNET NOTICES", StringComparison.Ordinal);
        Assert.IsTrue(own > 0 && own < notices && notices < dotnet, text);
        Assert.IsFalse(text.Contains('\r'), "line ends are \\n here; the dialog turns them into what the text box wants");
    }

    [TestMethod]
    public void Reflow_joins_the_lines_of_a_paragraph_and_keeps_paragraphs_headings_and_items()
    {
        string text = "Title\r\n=====\r\n\r\nOne two\r\nthree four.\r\n\r\nSecond paragraph\r\nalso wrapped.\r\n\r\n  - item one\r\n    continues here\r\n  - item two\r\n";

        Assert.AreEqual(
            "Title\n=====\n\nOne two three four.\n\nSecond paragraph also wrapped.\n\n  - item one continues here\n\n  - item two",
            AboutText.Reflow(text));
    }

    [TestMethod]
    public void Reflow_keeps_every_word_of_PuTTYs_licence()
    {
        string licence = File.ReadAllText(Path.Combine(RepoRoot(), "external", "putty", "LICENCE"));
        string reflowed = AboutText.Reflow(licence);

        Assert.AreEqual(Normalize(licence), Normalize(reflowed));
        Assert.IsTrue(reflowed.Split('\n').Length < licence.Split('\n').Length / 2, "the hard-wrapped lines were not joined");
    }

    [TestMethod]
    public void The_installers_licence_text_is_valid_ASCII_RTF_with_PuTTYs_licence_in_it()
    {
        string rtfPath = Path.Combine(Path.GetTempPath(), "putty-modern-notices-test-" + Guid.NewGuid().ToString("N") + ".rtf");
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in new[]
                     {
                         "-NonInteractive", "-ExecutionPolicy", "Bypass", "-NoProfile", "-File",
                         Path.Combine(RepoRoot(), "installer", "Common", "MakeNoticesRtf.ps1"), "-OutputFilePath", rtfPath,
                     })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = System.Diagnostics.Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode, output);

            byte[] bytes = File.ReadAllBytes(rtfPath);
            Assert.IsTrue(bytes.All(b => b < 128), "the RTF must be plain ASCII (everything else is a \\u escape)");
            string rtf = System.Text.Encoding.ASCII.GetString(bytes);
            Assert.IsTrue(rtf.StartsWith(@"{\rtf1"));
            Assert.IsTrue(rtf.EndsWith("}"));

            // Braces balance once the escaped ones are taken out.
            string bare = rtf.Replace(@"\\", "").Replace(@"\{", "").Replace(@"\}", "");
            Assert.AreEqual(bare.Count(c => c == '{'), bare.Count(c => c == '}'));

            // PuTTY's licence is in it (the contributor list is on one paragraph however the source wrapped it), and so is ours.
            Assert.IsTrue(rtf.Contains("Portions copyright Robert de Bath, Joris van Rantwijk"), "PuTTY's contributor list is missing");
            Assert.IsTrue(rtf.Contains("PuTTY is copyright 1997-2026 Simon Tatham."));
            Assert.IsTrue(rtf.Contains("Copyright (c) 2026 Andrij Abyzov"));
            Assert.IsTrue(rtf.Contains("Digi21"));
            Assert.IsTrue(rtf.IndexOf("Andrij Abyzov", StringComparison.Ordinal) < rtf.IndexOf("Simon Tatham", StringComparison.Ordinal),
                "the program's own licence comes first");
        }
        finally
        {
            File.Delete(rtfPath);
        }
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

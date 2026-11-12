using System.Globalization;
using System.Xml.Linq;

namespace PuTTY.Sessions;

/// <summary>How the terminals (the documents of the docking area) are arranged: all in tabs of one group, or spread over
/// several groups side by side, stacked or in a grid.</summary>
public enum Arrangement
{
    /// <summary>One group, every session a tab in it.</summary>
    Tabs,

    /// <summary>Two columns.</summary>
    SideBySide,

    /// <summary>Two rows.</summary>
    Stacked,

    /// <summary>Three columns (3 x 1).</summary>
    ThreeColumns,

    /// <summary>Three rows (1 x 3).</summary>
    ThreeRows,

    /// <summary>Two rows of two (2 x 2).</summary>
    Grid,

    /// <summary>One large group on the left (60 percent), two stacked on the right.</summary>
    MainAndTwo,
}

/// <summary>Names and sizes of the <see cref="Arrangement"/>s, for menus.</summary>
public static class Arrangements
{
    public static IReadOnlyList<Arrangement> All { get; } =
    [
        Arrangement.Tabs, Arrangement.SideBySide, Arrangement.Stacked, Arrangement.ThreeColumns, Arrangement.ThreeRows,
        Arrangement.Grid, Arrangement.MainAndTwo,
    ];

    public static string Title(Arrangement arrangement) => arrangement switch
    {
        Arrangement.Tabs => "Tabs only",
        Arrangement.SideBySide => "Side by side",
        Arrangement.Stacked => "Stacked",
        Arrangement.ThreeColumns => "Three columns",
        Arrangement.ThreeRows => "Three rows",
        Arrangement.Grid => "Grid (2 × 2)",
        Arrangement.MainAndTwo => "One large, two small",
        _ => arrangement.ToString(),
    };

    /// <summary>How many groups the arrangement has when there are enough sessions.</summary>
    public static int Cells(Arrangement arrangement) => ArrangementShape.Of(arrangement).Leaves;
}

/// <summary>
/// Edits the document ids in a saved docking layout (the library's versioned XML, see <c>DockSiteLayoutSerializer</c>)
/// without touching the rest of it: the tool windows, their sizes, the auto-hidden and floating panels stay as they
/// are. This is for named layouts, which are saved with the ids their documents had at the time and loaded when the open
/// sessions have other ids. (Arranging the open documents into columns, rows or a grid is the library's own
/// <c>DocumentHost.Arrange</c>; <see cref="ArrangementShape"/> says which shape.) Pure text in and out, with no WinUI,
/// so it is tested on its own.
/// </summary>
/// <remarks>
/// The parts of the format that matter here: <c>DocumentHost</c> holds one root, either a <c>DocumentContainer</c> (a group
/// of tabs, with <c>Document</c> children that carry an <c>Id</c>) or a <c>SplitContainer</c> (<c>Orientation</c>
/// <c>Horizontal</c> puts its children side by side, <c>Vertical</c> stacks them) of such nodes; every node has a
/// <c>RelativeSize</c> among its siblings.
/// </remarks>
public static class LayoutArranger
{
    /// <summary>The ids of the documents the layout holds, in the order of the tree (left to right, top to bottom, tab by tab).</summary>
    public static IReadOnlyList<string> DocumentIds(string layoutXml)
    {
        XDocument document = Parse(layoutXml);
        return [.. document.Descendants("Document").Select(d => (string?)d.Attribute("Id")).OfType<string>()];
    }

    /// <summary>
    /// The layout with each document id replaced by the one <paramref name="map"/> gives it; a document the map does not name
    /// is taken out, with the groups and splits it leaves empty. For a layout saved earlier, whose documents are now open
    /// under other ids.
    /// </summary>
    public static string RemapDocumentIds(string layoutXml, IReadOnlyDictionary<string, string> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        XDocument document = Parse(layoutXml);
        foreach (XElement element in document.Descendants("Document").ToList())
        {
            string? id = (string?)element.Attribute("Id");
            if (id is not null && map.TryGetValue(id, out string? replacement))
            {
                element.SetAttributeValue("Id", replacement);
            }
            else
            {
                element.Remove();
            }
        }

        foreach (XElement group in document.Descendants("DocumentContainer"))
        {
            if ((string?)group.Attribute("SelectedId") is { } selected)
            {
                group.SetAttributeValue("SelectedId", map.TryGetValue(selected, out string? replacement) ? replacement : null);
            }
        }

        Tidy(document);
        return Write(document);
    }

    /// <summary>
    /// The layout with every document whose id is not in <paramref name="keep"/> taken out, with the groups and splits that
    /// leaves empty.
    /// </summary>
    public static string Retain(string layoutXml, IReadOnlyCollection<string> keep)
    {
        ArgumentNullException.ThrowIfNull(keep);
        XDocument document = Parse(layoutXml);
        foreach (XElement element in document.Descendants("Document").ToList())
        {
            if ((string?)element.Attribute("Id") is not { } id || !keep.Contains(id))
            {
                element.Remove();
            }
        }

        Tidy(document);
        return Write(document);
    }

    /// <summary>
    /// The layout with the documents it does not mention added as tabs of its last group (or as a group of their own when
    /// it has none), so that a layout loaded for some of the open sessions does not close the others.
    /// </summary>
    public static string EnsureDocuments(string layoutXml, IReadOnlyList<string> documentIds)
    {
        ArgumentNullException.ThrowIfNull(documentIds);
        XDocument document = Parse(layoutXml);
        HashSet<string> present = [.. document.Descendants("Document").Select(d => (string?)d.Attribute("Id")).OfType<string>()];
        string[] missing = [.. documentIds.Where(id => !present.Contains(id))];
        if (missing.Length == 0)
        {
            return Write(document);
        }

        XElement host = FindHost(document);
        XElement? last = host.Descendants("DocumentContainer").LastOrDefault();
        if (last is null)
        {
            last = new XElement("DocumentContainer", new XAttribute("RelativeSize", "1"));
            host.Add(last);
        }

        foreach (string id in missing)
        {
            last.Add(new XElement("Document", new XAttribute("Id", id), new XAttribute("State", "Docked")));
        }

        return Write(document);
    }

    // ----- The XML ----------------------------------------------------------------------------------------------

    private static XDocument Parse(string layoutXml)
    {
        ArgumentException.ThrowIfNullOrEmpty(layoutXml);
        XDocument document = XDocument.Parse(layoutXml);
        if (document.Root is null || document.Root.Name.LocalName != "DockSiteLayout")
        {
            throw new InvalidOperationException("This is not a docking layout.");
        }

        return document;
    }

    private static XElement FindHost(XDocument document) =>
        document.Descendants("DocumentHost").FirstOrDefault() ?? throw new InvalidOperationException("The layout has no document area.");

    private static string Write(XDocument document) => document.ToString(SaveOptions.None);

    /// <summary>Removes the groups that have no document left, the splits that have no child, and replaces a split of one child by it.</summary>
    private static void Tidy(XDocument document)
    {
        foreach (XElement host in document.Descendants("DocumentHost").ToList())
        {
            Tidy(host);
        }

        foreach (XElement floating in document.Descendants("FloatingWindow").ToList())
        {
            Tidy(floating);
            if (!floating.Descendants().Any(d => d.Name.LocalName is "ToolWindow" or "Document"))
            {
                floating.Remove();
            }
        }

        // A group whose selected tab went keeps showing its first.
        foreach (XElement group in document.Descendants("DocumentContainer"))
        {
            string[] ids = [.. group.Elements("Document").Select(d => (string?)d.Attribute("Id")).OfType<string>()];
            if (ids.Length > 0 && ((string?)group.Attribute("SelectedId") is not { } selected || !ids.Contains(selected)))
            {
                group.SetAttributeValue("SelectedId", ids[0]);
            }
        }
    }

    private static void Tidy(XElement area)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (XElement group in area.Descendants("DocumentContainer").ToList())
            {
                if (!group.Elements("Document").Any())
                {
                    group.Remove();
                    changed = true;
                }
            }

            foreach (XElement split in area.Descendants("SplitContainer").ToList())
            {
                // Only the splits of documents: one that holds tool windows is not ours to collapse.
                if (split.Descendants("ToolWindowContainer").Any() || split.Descendants("ToolWindow").Any())
                {
                    continue;
                }

                int count = split.Elements().Count();
                if (count == 0)
                {
                    split.Remove();
                    changed = true;
                }
                else if (count == 1)
                {
                    XElement only = split.Elements().First();
                    if (split.Attribute("RelativeSize") is { } size)
                    {
                        only.SetAttributeValue("RelativeSize", size.Value);
                    }

                    only.Remove();
                    split.ReplaceWith(only);
                    changed = true;
                }
            }
        }

        // The weights of the siblings that remain are shared out again.
        foreach (XElement split in area.Descendants("SplitContainer"))
        {
            double[] sizes = [.. split.Elements().Select(e => double.TryParse((string?)e.Attribute("RelativeSize"), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0 ? v : 1.0)];
            double total = sizes.Sum();
            if (total > 0 && Math.Abs(total - 1.0) > 1e-9 && split.Elements().Count() == sizes.Length)
            {
                int i = 0;
                foreach (XElement child in split.Elements())
                {
                    child.SetAttributeValue("RelativeSize", (sizes[i++] / total).ToString("R", CultureInfo.InvariantCulture));
                }
            }
        }
    }
}

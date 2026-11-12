namespace PuTTY.Sessions;

/// <summary>
/// The shape of an <see cref="Arrangement"/>: a tree of splits (side by side or stacked) whose leaves are groups of tabs, in
/// the order sessions are dealt into them. The app turns a shape into the docking library's <c>DocumentLayout</c> with the
/// open tabs in the leaves; keeping the shapes here, as plain data, keeps them tested without a window.
/// </summary>
/// <param name="Horizontal">For a split: its panes are side by side (true) or stacked (false). Meaningless for a leaf.</param>
/// <param name="Weight">The share of space among its siblings.</param>
/// <param name="Children">The panes of a split; empty for a leaf, which is a group of tabs.</param>
public sealed record ArrangementShape(bool Horizontal, double Weight, IReadOnlyList<ArrangementShape> Children)
{
    /// <summary>A group of tabs, rather than a split.</summary>
    public bool IsLeaf => Children.Count == 0;

    /// <summary>How many groups the shape has: the leaves.</summary>
    public int Leaves => IsLeaf ? 1 : Children.Sum(child => child.Leaves);

    /// <summary>The shape of an arrangement. Siblings' weights add up to 1.</summary>
    public static ArrangementShape Of(Arrangement arrangement) => arrangement switch
    {
        Arrangement.SideBySide => Split(true, 1, Leaf(0.5), Leaf(0.5)),
        Arrangement.Stacked => Split(false, 1, Leaf(0.5), Leaf(0.5)),
        Arrangement.ThreeColumns => Split(true, 1, Leaf(1.0 / 3), Leaf(1.0 / 3), Leaf(1.0 / 3)),
        Arrangement.ThreeRows => Split(false, 1, Leaf(1.0 / 3), Leaf(1.0 / 3), Leaf(1.0 / 3)),
        Arrangement.Grid => Split(false, 1,
            Split(true, 0.5, Leaf(0.5), Leaf(0.5)),
            Split(true, 0.5, Leaf(0.5), Leaf(0.5))),
        Arrangement.MainAndTwo => Split(true, 1, Leaf(0.6), Split(false, 0.4, Leaf(0.5), Leaf(0.5))),
        _ => Leaf(1),
    };

    private static ArrangementShape Leaf(double weight) => new(false, weight, []);

    private static ArrangementShape Split(bool horizontal, double weight, params ArrangementShape[] children) => new(horizontal, weight, children);
}

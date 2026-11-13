using PuTTY.Sessions;

namespace PuTTY.Sessions.Tests;

/// <summary>
/// The Connection log without its controls: how many lines it keeps, what the filter shows, and that the changes it hands to a
/// view, applied to a copy of what the view had, always give what the view should show.
/// </summary>
[TestClass]
public sealed class ConnectionLogModelTests
{
    private static readonly DateTime Start = new(2026, 11, 13, 15, 0, 0);

    private static LogEntry Line(string session, int n, bool error = false) => new(Start.AddSeconds(n), session, $"{session} line {n}", error);

    private static string[] Texts(IEnumerable<LogEntry> entries) => [.. entries.Select(e => e.Text)];

    private static string[] Visible(ConnectionLogModel model) => [.. Enumerable.Range(0, model.VisibleCount).Select(i => model.VisibleAt(i).Text)];

    /// <summary>Does to a copy of the view's lines what a view does with a change.</summary>
    private static void Apply(List<LogEntry> mirror, LogChange change)
    {
        if (change.IsReset)
        {
            mirror.Clear();
        }
        else
        {
            mirror.RemoveRange(0, change.Removed);
        }

        mirror.AddRange(change.Added);
    }

    [TestMethod]
    public void Lines_are_shown_oldest_first()
    {
        var model = new ConnectionLogModel(capacity: 10);
        model.Add(Line("a", 1));
        model.Add(Line("a", 2));

        CollectionAssert.AreEqual(new[] { "a line 1", "a line 2" }, Visible(model));
    }

    [TestMethod]
    public void The_oldest_lines_go_first_when_the_capacity_is_reached()
    {
        var model = new ConnectionLogModel(capacity: 3);
        for (int i = 1; i <= 5; i++)
        {
            model.Add(Line("a", i));
        }

        CollectionAssert.AreEqual(new[] { "a line 3", "a line 4", "a line 5" }, Visible(model));
    }

    [TestMethod]
    public void A_session_is_new_only_the_first_time_and_keeps_its_place_in_the_list()
    {
        var model = new ConnectionLogModel();

        Assert.IsTrue(model.Add(Line("b", 1)));
        Assert.IsTrue(model.Add(Line("a", 2)));
        Assert.IsFalse(model.Add(Line("b", 3)));

        CollectionAssert.AreEqual(new[] { "b", "a" }, model.Sessions.ToArray());
    }

    [TestMethod]
    public void A_few_new_lines_are_reported_as_lines_to_append()
    {
        var model = new ConnectionLogModel(capacity: 100);
        model.Add(Line("a", 1));
        model.TakeChange();

        model.Add(Line("a", 2));
        model.Add(Line("a", 3));
        LogChange change = model.TakeChange();

        Assert.IsFalse(change.IsReset);
        Assert.AreEqual(0, change.Removed);
        CollectionAssert.AreEqual(new[] { "a line 2", "a line 3" }, Texts(change.Added));
    }

    [TestMethod]
    public void A_full_log_reports_each_new_line_as_one_removed_and_one_added()
    {
        var model = new ConnectionLogModel(capacity: 3);
        for (int i = 1; i <= 3; i++)
        {
            model.Add(Line("a", i));
        }

        model.TakeChange();
        model.Add(Line("a", 4));
        LogChange change = model.TakeChange();

        Assert.IsFalse(change.IsReset);
        Assert.AreEqual(1, change.Removed);
        CollectionAssert.AreEqual(new[] { "a line 4" }, Texts(change.Added));
    }

    [TestMethod]
    public void A_burst_bigger_than_the_threshold_is_one_reset_with_the_last_lines()
    {
        var model = new ConnectionLogModel(capacity: 200, resetThreshold: 10);
        for (int i = 1; i <= 500; i++)
        {
            model.Add(Line("a", i));
        }

        LogChange change = model.TakeChange();

        Assert.IsTrue(change.IsReset);
        Assert.AreEqual(200, change.Added.Count);
        Assert.AreEqual("a line 301", change.Added[0].Text);
        Assert.AreEqual("a line 500", change.Added[^1].Text);
    }

    [TestMethod]
    public void Removals_that_would_reach_lines_the_view_never_had_are_a_reset()
    {
        var model = new ConnectionLogModel(capacity: 3, resetThreshold: 100);
        for (int i = 1; i <= 7; i++)
        {
            model.Add(Line("a", i));         // 4 lines dropped, none of them ever given to the view
        }

        LogChange change = model.TakeChange();

        Assert.IsTrue(change.IsReset);
        CollectionAssert.AreEqual(new[] { "a line 5", "a line 6", "a line 7" }, Texts(change.Added));
    }

    [TestMethod]
    public void Nothing_is_pending_until_something_changes()
    {
        var model = new ConnectionLogModel();
        Assert.IsFalse(model.HasPending);

        model.Add(Line("a", 1));
        Assert.IsTrue(model.HasPending);

        model.TakeChange();
        Assert.IsFalse(model.HasPending);
    }

    [TestMethod]
    public void The_filter_shows_one_sessions_lines_and_ignores_the_others_that_arrive()
    {
        var model = new ConnectionLogModel(capacity: 50);
        model.Add(Line("a", 1));
        model.Add(Line("b", 2));
        model.Add(Line("a", 3));
        model.SetFilter("a");
        model.TakeChange();

        model.Add(Line("b", 4));

        Assert.IsFalse(model.HasPending);
        CollectionAssert.AreEqual(new[] { "a line 1", "a line 3" }, Visible(model));

        model.Add(Line("a", 5));
        CollectionAssert.AreEqual(new[] { "a line 5" }, Texts(model.TakeChange().Added));
    }

    [TestMethod]
    public void Changing_the_filter_is_a_reset_with_the_lines_to_show()
    {
        var model = new ConnectionLogModel(capacity: 50);
        model.Add(Line("a", 1));
        model.Add(Line("b", 2));
        model.TakeChange();

        model.SetFilter("b");
        LogChange change = model.TakeChange();

        Assert.IsTrue(change.IsReset);
        CollectionAssert.AreEqual(new[] { "b line 2" }, Texts(change.Added));

        model.SetFilter(null);
        CollectionAssert.AreEqual(new[] { "a line 1", "b line 2" }, Texts(model.TakeChange().Added));
    }

    [TestMethod]
    public void A_dropped_line_leaves_the_filtered_view_only_if_it_was_shown_there()
    {
        var model = new ConnectionLogModel(capacity: 4);
        model.Add(Line("a", 1));
        model.Add(Line("b", 2));
        model.Add(Line("a", 3));
        model.Add(Line("b", 4));
        model.SetFilter("a");
        model.TakeChange();

        model.Add(Line("b", 5));        // drops a line 1: the filtered view loses it
        CollectionAssert.AreEqual(new[] { "a line 3" }, Visible(model));
        LogChange first = model.TakeChange();
        Assert.IsFalse(first.IsReset);
        Assert.AreEqual(1, first.Removed);
        Assert.AreEqual(0, first.Added.Count);

        model.Add(Line("b", 6));        // drops b line 2: nothing changes in the filtered view
        Assert.IsFalse(model.HasPending);
    }

    [TestMethod]
    public void Clear_forgets_the_lines_but_not_the_sessions()
    {
        var model = new ConnectionLogModel();
        model.Add(Line("a", 1));
        model.TakeChange();

        model.Clear();
        LogChange change = model.TakeChange();

        Assert.IsTrue(change.IsReset);
        Assert.AreEqual(0, change.Added.Count);
        Assert.AreEqual(0, model.VisibleCount);
        CollectionAssert.AreEqual(new[] { "a" }, model.Sessions.ToArray());
    }

    [TestMethod]
    public void A_view_that_applies_every_change_always_shows_what_the_model_has()
    {
        // The property the view relies on, tried over a long random run of lines, filters, clears and views that look
        // at the log at uneven moments, with small buffers so that lines are dropped all the time.
        var random = new Random(20261113);
        string[] names = ["a", "b", "c"];
        for (int capacity = 1; capacity <= 8; capacity++)
        {
            var model = new ConnectionLogModel(capacity, resetThreshold: 1 + capacity);
            var mirror = new List<LogEntry>();
            for (int step = 0; step < 4000; step++)
            {
                switch (random.Next(20))
                {
                    case 0:
                        model.SetFilter(random.Next(3) == 0 ? null : names[random.Next(names.Length)]);
                        break;
                    case 1:
                        model.Clear();
                        break;
                    case < 8:
                        Apply(mirror, model.TakeChange());
                        CollectionAssert.AreEqual(Visible(model), Texts(mirror), $"capacity {capacity}, step {step}");
                        break;
                    default:
                        model.Add(Line(names[random.Next(names.Length)], step));
                        break;
                }
            }

            Apply(mirror, model.TakeChange());
            CollectionAssert.AreEqual(Visible(model), Texts(mirror), $"capacity {capacity}, at the end");
        }
    }
}

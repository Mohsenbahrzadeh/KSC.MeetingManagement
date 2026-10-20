using KSC.MeetingManagement.Domain.Meetings;

namespace KSC.MeetingManagement.UnitTests;

public class AgendaTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid MeetingId = Guid.NewGuid();

    private static AgendaItem Item(int order, string title = "موضوع") =>
        new(Guid.NewGuid(), Tenant, MeetingId, order, title, null, null, null);

    [Fact]
    public void Reorder_assigns_a_gapless_sequence_in_the_given_order()
    {
        var items = new[] { Item(1, "a"), Item(2, "b"), Item(3, "c") };

        AgendaOrdering.Reorder(items, [items[2].Id, items[0].Id, items[1].Id]);

        Assert.Equal(["c", "a", "b"], items.OrderBy(i => i.Order).Select(i => i.Title));
        Assert.Equal([1, 2, 3], items.Select(i => i.Order).Order());
    }

    [Fact]
    public void Reorder_rejects_missing_extra_or_duplicate_ids_and_changes_nothing()
    {
        var items = new[] { Item(1), Item(2), Item(3) };
        var before = items.Select(i => i.Order).ToArray();

        Assert.Throws<MeetingDomainException>(() => AgendaOrdering.Reorder(items, [items[0].Id, items[1].Id]));
        Assert.Throws<MeetingDomainException>(() => AgendaOrdering.Reorder(items, [items[0].Id, items[1].Id, items[1].Id]));
        Assert.Throws<MeetingDomainException>(() => AgendaOrdering.Reorder(items, [items[0].Id, items[1].Id, Guid.NewGuid()]));
        Assert.Throws<MeetingDomainException>(() => AgendaOrdering.Reorder(items, [items[0].Id, items[1].Id, items[2].Id, Guid.NewGuid()]));

        Assert.Equal(before, items.Select(i => i.Order));
    }

    [Fact]
    public void Normalize_closes_gaps_and_keeps_relative_order()
    {
        var a = Item(2, "a");
        var b = Item(5, "b");
        var c = Item(9, "c");

        AgendaOrdering.Normalize([c, a, b]);

        Assert.Equal((1, 2, 3), (a.Order, b.Order, c.Order));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Title_is_required(string title) =>
        Assert.Throws<MeetingDomainException>(() => new AgendaItem(Guid.NewGuid(), Tenant, MeetingId, 1, title, null, null, null));

    [Fact]
    public void Limits_are_enforced_and_text_is_trimmed()
    {
        Assert.Throws<MeetingDomainException>(() => Item(1, new string('x', AgendaItem.MaxTitleLength + 1)));
        Assert.Throws<MeetingDomainException>(() => new AgendaItem(Guid.NewGuid(), Tenant, MeetingId, 1, "t", null, null, 0));
        Assert.Throws<MeetingDomainException>(() => new AgendaItem(Guid.NewGuid(), Tenant, MeetingId, 1, "t", null, null, AgendaItem.MaxDurationMinutes + 1));
        Assert.Throws<MeetingDomainException>(() => new AgendaItem(Guid.NewGuid(), Tenant, MeetingId, 0, "t", null, null, null));
        Assert.Throws<MeetingDomainException>(() => new AgendaItem(Guid.NewGuid(), Tenant, MeetingId, 1, "t", null, Guid.Empty, null));

        var ok = new AgendaItem(Guid.NewGuid(), Tenant, MeetingId, 1, "  عنوان  ", "  ", null, 30);
        Assert.Equal("عنوان", ok.Title);
        Assert.Null(ok.Description);
    }

    [Fact]
    public void Agenda_revision_increases_on_every_touch_and_a_closed_meeting_rejects_it()
    {
        var meeting = new Meeting(Guid.NewGuid(), Tenant, "m", MeetingMode.Online, Guid.NewGuid());

        meeting.TouchAgenda();
        meeting.TouchAgenda();
        Assert.Equal(2, meeting.AgendaRevision);

        meeting.Cancel("x");
        Assert.Throws<MeetingDomainException>(meeting.TouchAgenda);
        Assert.Equal(2, meeting.AgendaRevision);
    }
}

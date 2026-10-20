using KSC.MeetingManagement.Application.Notifications;
using KSC.MeetingManagement.Domain.Notifications;

namespace KSC.MeetingManagement.UnitTests;

public class OutboxProcessorTests
{
    private static readonly DateTimeOffset Start = new(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeOutbox : IOutboxRepository
    {
        public List<OutboxMessage> Messages { get; } = [];

        public int Saves { get; private set; }

        public Task<bool> AddAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            if (Messages.Any(m => m.TenantId == message.TenantId && m.DedupeKey == message.DedupeKey))
            {
                return Task.FromResult(false);
            }

            Messages.Add(message);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<OutboxMessage>> ClaimDueAsync(DateTimeOffset nowUtc, TimeSpan lease, int batchSize, CancellationToken cancellationToken)
        {
            var claimed = Messages.Where(m => m.IsDue(nowUtc)).Take(batchSize).Where(m => m.TryClaim(nowUtc, lease)).ToList();
            return Task.FromResult<IReadOnlyList<OutboxMessage>>(claimed);
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            Saves++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeChannel(Func<OutgoingNotification, DeliveryResult> respond) : INotificationChannel
    {
        public List<OutgoingNotification> Sent { get; } = [];

        public Task<DeliveryResult> SendAsync(OutgoingNotification notification, CancellationToken cancellationToken)
        {
            Sent.Add(notification);
            return Task.FromResult(respond(notification));
        }
    }

    private sealed class ThrowingChannel : INotificationChannel
    {
        public Task<DeliveryResult> SendAsync(OutgoingNotification notification, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("SMTP login failed for g@example.com password=hunter2");
    }

    private sealed class Directory(Func<Guid, Contact?> find) : IContactDirectory
    {
        public Task<Contact?> FindAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken) => Task.FromResult(find(userId));
    }

    private sealed class ThrowingDirectory : IContactDirectory
    {
        public Task<Contact?> FindAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken) => throw new TimeoutException("db down");
    }

    private sealed class Reversible : ISecretProtector
    {
        public string Protect(string plainText) => "P:" + plainText;

        public string Unprotect(string protectedText) =>
            protectedText.StartsWith("P:", StringComparison.Ordinal) ? protectedText[2..] : throw new System.Security.Cryptography.CryptographicException("bad");
    }

    private static readonly NotificationOptions Options = new() { MaxAttempts = 3, BaseDelay = TimeSpan.FromMinutes(1), MaxDelay = TimeSpan.FromHours(1) };

    private static OutboxMessage Guest(string key = "k", string body = "P:متن") =>
        new(Guid.NewGuid(), Guid.NewGuid(), NotificationKind.MeetingInvitation, RecipientType.Guest, null, "g@example.com", "مهمان", "موضوع", body, key, Start, Start);

    private static OutboxMessage Member(Guid userId, string key = "m") =>
        new(Guid.NewGuid(), Guid.NewGuid(), NotificationKind.MeetingChanged, RecipientType.Internal, userId, null, null, "موضوع", "P:متن عضو", key, Start, Start);

    private static (OutboxProcessor Processor, FakeOutbox Outbox, Clock Clock) Create(
        INotificationChannel channel, IContactDirectory? directory = null, ISecretProtector? protector = null)
    {
        var outbox = new FakeOutbox();
        var clock = new Clock(Start);
        return (new OutboxProcessor(outbox, directory ?? new Directory(_ => null), channel, protector ?? new Reversible(), Options, clock), outbox, clock);
    }

    [Fact]
    public async Task A_delivered_message_is_marked_sent_and_receives_the_decrypted_text()
    {
        var channel = new FakeChannel(_ => DeliveryResult.Delivered);
        var (processor, outbox, _) = Create(channel);
        outbox.Messages.Add(Guest());

        var result = await processor.ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(new OutboxRunResult(1, 1, 0, 0, 0), result);
        Assert.Equal(OutboxStatus.Sent, outbox.Messages[0].Status);
        var sent = Assert.Single(channel.Sent);
        Assert.Equal(("g@example.com", "متن", "مهمان"), (sent.ToAddress, sent.Body, sent.ToName));
    }

    [Fact]
    public async Task A_temporary_failure_is_retried_with_growing_delays_and_finally_recovers()
    {
        var failures = 2;
        var channel = new FakeChannel(_ => failures-- > 0 ? DeliveryResult.Temporary("smtp 421") : DeliveryResult.Delivered);
        var (processor, outbox, clock) = Create(channel);
        var message = Guest();
        outbox.Messages.Add(message);

        await processor.ProcessBatchAsync(CancellationToken.None);
        Assert.Equal((OutboxStatus.Pending, 1, Start + TimeSpan.FromMinutes(1)), (message.Status, message.Attempts, message.NextAttemptAtUtc));

        var tooEarly = await processor.ProcessBatchAsync(CancellationToken.None);
        Assert.Equal(0, tooEarly.Claimed);

        clock.Now = Start.AddMinutes(1);
        await processor.ProcessBatchAsync(CancellationToken.None);
        Assert.Equal((OutboxStatus.Pending, 2, Start.AddMinutes(1) + TimeSpan.FromMinutes(2)), (message.Status, message.Attempts, message.NextAttemptAtUtc));

        clock.Now = Start.AddMinutes(3);
        var last = await processor.ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(1, last.Sent);
        Assert.Equal((OutboxStatus.Sent, 3), (message.Status, message.Attempts));
        Assert.Equal(3, channel.Sent.Count);
    }

    [Fact]
    public async Task A_message_that_keeps_failing_is_given_up_after_the_limit()
    {
        var (processor, outbox, clock) = Create(new FakeChannel(_ => DeliveryResult.Temporary("down")));
        var message = Guest();
        outbox.Messages.Add(message);

        for (var i = 0; i < 6; i++)
        {
            await processor.ProcessBatchAsync(CancellationToken.None);
            clock.Now = clock.Now.AddHours(2);
        }

        Assert.Equal((OutboxStatus.Dead, 3), (message.Status, message.Attempts));
    }

    [Fact]
    public async Task A_permanent_failure_is_not_retried()
    {
        var channel = new FakeChannel(_ => DeliveryResult.Permanent("recipient rejected"));
        var (processor, outbox, clock) = Create(channel);
        outbox.Messages.Add(Guest());

        var result = await processor.ProcessBatchAsync(CancellationToken.None);
        clock.Now = clock.Now.AddDays(1);
        await processor.ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(1, result.Dead);
        Assert.Single(channel.Sent);
    }

    [Fact]
    public async Task A_throwing_channel_counts_as_temporary_and_its_details_are_not_stored()
    {
        var (processor, outbox, _) = Create(new ThrowingChannel());
        var message = Guest();
        outbox.Messages.Add(message);

        var result = await processor.ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(1, result.Retrying);
        Assert.Equal("channel error: InvalidOperationException", message.LastError);
        Assert.DoesNotContain("hunter2", message.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain("g@example.com", message.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_bad_message_does_not_stop_the_others()
    {
        var channel = new FakeChannel(n => n.Subject == "خراب" ? DeliveryResult.Temporary("x") : DeliveryResult.Delivered);
        var (processor, outbox, _) = Create(channel);
        outbox.Messages.Add(new OutboxMessage(Guid.NewGuid(), Guid.NewGuid(), NotificationKind.MeetingReminder, RecipientType.Guest, null, "a@example.com", null, "خراب", "P:x", "bad", Start, Start));
        outbox.Messages.Add(Guest("ok1"));
        outbox.Messages.Add(Guest("ok2"));

        var result = await processor.ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(new OutboxRunResult(3, 2, 1, 0, 0), result);
        Assert.Equal(3, outbox.Saves);
    }

    [Fact]
    public async Task Internal_members_are_reached_through_the_contact_directory_and_unknown_ones_are_skipped()
    {
        var known = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        var channel = new FakeChannel(_ => DeliveryResult.Delivered);
        var (processor, outbox, _) = Create(channel, new Directory(id => id == known ? new Contact("member@example.com", "علی") : null));
        outbox.Messages.Add(Member(known, "k1"));
        var skipped = Member(unknown, "k2");
        outbox.Messages.Add(skipped);

        var result = await processor.ProcessBatchAsync(CancellationToken.None);

        Assert.Equal((1, 1), (result.Sent, result.Skipped));
        Assert.Equal(("member@example.com", "علی"), (channel.Sent.Single().ToAddress, channel.Sent.Single().ToName));
        Assert.Equal(OutboxStatus.Skipped, skipped.Status);
    }

    [Fact]
    public async Task A_broken_directory_is_a_temporary_failure_not_a_skip()
    {
        var (processor, outbox, _) = Create(new FakeChannel(_ => DeliveryResult.Delivered), new ThrowingDirectory());
        var message = Member(Guid.NewGuid());
        outbox.Messages.Add(message);

        var result = await processor.ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(1, result.Retrying);
        Assert.Equal(OutboxStatus.Pending, message.Status);
        Assert.Equal("contact directory failed: TimeoutException", message.LastError);
    }

    [Fact]
    public async Task Unreadable_text_is_given_up_immediately_without_sending()
    {
        var channel = new FakeChannel(_ => DeliveryResult.Delivered);
        var (processor, outbox, _) = Create(channel);
        var message = Guest(body: "tampered");
        outbox.Messages.Add(message);

        var result = await processor.ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(1, result.Dead);
        Assert.Empty(channel.Sent);
        Assert.Equal("message text cannot be read", message.LastError);
    }

    [Fact]
    public async Task Messages_claimed_by_another_worker_are_not_sent_twice()
    {
        var channel = new FakeChannel(_ => DeliveryResult.Delivered);
        var (processor, outbox, _) = Create(channel);
        var message = Guest();
        outbox.Messages.Add(message);
        message.TryClaim(Start, TimeSpan.FromMinutes(5));

        var result = await processor.ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(0, result.Claimed);
        Assert.Empty(channel.Sent);
    }

    [Fact]
    public async Task The_batch_size_limits_one_run_and_the_rest_waits_for_the_next()
    {
        var channel = new FakeChannel(_ => DeliveryResult.Delivered);
        var outbox = new FakeOutbox();
        var processor = new OutboxProcessor(outbox, new Directory(_ => null), channel, new Reversible(), new NotificationOptions { BatchSize = 2 }, new Clock(Start));
        for (var i = 0; i < 5; i++)
        {
            outbox.Messages.Add(Guest("k" + i));
        }

        var first = await processor.ProcessBatchAsync(CancellationToken.None);
        var second = await processor.ProcessBatchAsync(CancellationToken.None);
        var third = await processor.ProcessBatchAsync(CancellationToken.None);

        Assert.Equal((2, 2, 1), (first.Claimed, second.Claimed, third.Claimed));
        Assert.All(outbox.Messages, m => Assert.Equal(OutboxStatus.Sent, m.Status));
    }
}

using System.Security.Cryptography;
using KSC.MeetingManagement.Application.Notifications;
using KSC.MeetingManagement.Domain.Notifications;
using KSC.MeetingManagement.Infrastructure.Notifications;

namespace KSC.MeetingManagement.UnitTests;

public class OutboxMessageTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);

    private static OutboxMessage New(RecipientType type = RecipientType.Internal, DateTimeOffset? notBefore = null) =>
        type == RecipientType.Internal
            ? new OutboxMessage(Guid.NewGuid(), Guid.NewGuid(), NotificationKind.MeetingInvitation, type, Guid.NewGuid(), null, null, "موضوع", "protected", "key-1", Now, notBefore ?? Now)
            : new OutboxMessage(Guid.NewGuid(), Guid.NewGuid(), NotificationKind.MeetingInvitation, type, null, " g@example.com ", "مهمان", "موضوع", "protected", "key-1", Now, notBefore ?? Now);

    [Fact]
    public void New_messages_are_pending_and_due_at_their_start_time()
    {
        var m = New(notBefore: Now.AddMinutes(5));

        Assert.Equal(OutboxStatus.Pending, m.Status);
        Assert.False(m.IsDue(Now));
        Assert.True(m.IsDue(Now.AddMinutes(5)));
        Assert.Equal(0, m.Attempts);
    }

    [Fact]
    public void Recipients_need_the_right_identity()
    {
        Assert.Throws<ArgumentException>(() => new OutboxMessage(Guid.NewGuid(), Guid.NewGuid(), NotificationKind.MeetingReminder, RecipientType.Internal, null, null, null, "s", "b", "k", Now, Now));
        Assert.Throws<ArgumentException>(() => new OutboxMessage(Guid.NewGuid(), Guid.NewGuid(), NotificationKind.MeetingReminder, RecipientType.Guest, null, " ", null, "s", "b", "k", Now, Now));
        Assert.Throws<ArgumentException>(() => new OutboxMessage(Guid.Empty, Guid.NewGuid(), NotificationKind.MeetingReminder, RecipientType.Internal, Guid.NewGuid(), null, null, "s", "b", "k", Now, Now));
        Assert.Throws<ArgumentException>(() => new OutboxMessage(Guid.NewGuid(), Guid.NewGuid(), NotificationKind.MeetingReminder, RecipientType.Internal, Guid.NewGuid(), null, null, "s", "b", " ", Now, Now));
        Assert.Equal("g@example.com", New(RecipientType.Guest).RecipientAddress);
    }

    [Fact]
    public void A_claim_holds_the_message_until_the_lease_ends()
    {
        var m = New();

        Assert.True(m.TryClaim(Now, TimeSpan.FromMinutes(5)));
        Assert.False(m.TryClaim(Now.AddMinutes(1), TimeSpan.FromMinutes(5)));
        Assert.False(m.IsDue(Now.AddMinutes(4)));
        Assert.True(m.IsDue(Now.AddMinutes(5)));
        Assert.True(m.TryClaim(Now.AddMinutes(5), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void A_message_that_is_not_due_cannot_be_claimed()
    {
        var m = New(notBefore: Now.AddHours(1));

        Assert.False(m.TryClaim(Now, TimeSpan.FromMinutes(5)));
        Assert.Equal(0, m.ClaimVersion);
    }

    [Fact]
    public void Success_marks_sent_clears_the_lock_and_counts_the_attempt()
    {
        var m = New();
        m.TryClaim(Now, TimeSpan.FromMinutes(5));

        m.MarkSent(Now.AddSeconds(2));

        Assert.Equal((OutboxStatus.Sent, 1, null), (m.Status, m.Attempts, m.LockedUntilUtc));
        Assert.Equal(Now.AddSeconds(2), m.SentAtUtc);
        Assert.False(m.IsDue(Now.AddDays(1)));
    }

    [Fact]
    public void Failures_are_retried_later_and_given_up_after_the_limit()
    {
        var m = New();

        m.TryClaim(Now, TimeSpan.FromMinutes(5));
        m.MarkFailed(Now, "boom", maxAttempts: 3, TimeSpan.FromMinutes(1));
        Assert.Equal((OutboxStatus.Pending, 1, Now.AddMinutes(1)), (m.Status, m.Attempts, m.NextAttemptAtUtc));
        Assert.False(m.IsDue(Now));
        Assert.True(m.IsDue(Now.AddMinutes(1)));

        m.MarkFailed(Now.AddMinutes(1), "boom", 3, TimeSpan.FromMinutes(2));
        m.MarkFailed(Now.AddMinutes(3), "boom", 3, TimeSpan.FromMinutes(4));

        Assert.Equal((OutboxStatus.Dead, 3), (m.Status, m.Attempts));
        Assert.False(m.IsDue(Now.AddYears(1)));
    }

    [Fact]
    public void Errors_are_truncated_and_a_dead_message_can_be_requeued_once_the_cause_is_fixed()
    {
        var m = New();
        m.MarkFailed(Now, new string('e', 1000), maxAttempts: 1, TimeSpan.Zero);

        Assert.Equal(OutboxMessage.MaxErrorLength, m.LastError!.Length);
        Assert.Throws<InvalidOperationException>(() => New().Requeue(Now));

        m.Requeue(Now.AddHours(1));
        Assert.Equal((OutboxStatus.Pending, 0), (m.Status, m.Attempts));
        Assert.True(m.IsDue(Now.AddHours(1)));
    }

    [Fact]
    public void Skipping_records_the_reason_and_stops_delivery()
    {
        var m = New();

        m.MarkSkipped("no address");

        Assert.Equal((OutboxStatus.Skipped, "no address"), (m.Status, m.LastError));
        Assert.False(m.IsDue(Now));
    }
}

public class NotificationOptionsTests
{
    [Fact]
    public void Delay_doubles_with_each_failure_up_to_the_cap()
    {
        var o = new NotificationOptions { BaseDelay = TimeSpan.FromSeconds(30), MaxDelay = TimeSpan.FromMinutes(10) };

        Assert.Equal(TimeSpan.FromSeconds(30), o.Delay(1));
        Assert.Equal(TimeSpan.FromMinutes(1), o.Delay(2));
        Assert.Equal(TimeSpan.FromMinutes(2), o.Delay(3));
        Assert.Equal(TimeSpan.FromMinutes(10), o.Delay(10));
        Assert.Equal(TimeSpan.FromMinutes(10), o.Delay(500));
        Assert.Equal(TimeSpan.FromSeconds(30), o.Delay(0));
    }
}

public class SecretProtectorTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void Text_round_trips_including_persian()
    {
        var protector = new AesGcmSecretProtector(Key);
        const string text = "کد دعوت: abc-123 — جلسهٔ «هیئت مدیره»";

        var protectedText = protector.Protect(text);

        Assert.DoesNotContain("abc-123", protectedText, StringComparison.Ordinal);
        Assert.Equal(text, protector.Unprotect(protectedText));
    }

    [Fact]
    public void Each_protection_uses_a_new_nonce()
    {
        var protector = new AesGcmSecretProtector(Key);

        Assert.NotEqual(protector.Protect("same"), protector.Protect("same"));
    }

    [Fact]
    public void Tampered_or_foreign_text_is_rejected()
    {
        var protector = new AesGcmSecretProtector(Key);
        var other = new AesGcmSecretProtector(RandomNumberGenerator.GetBytes(32));
        var data = Convert.FromBase64String(protector.Protect("secret"));
        data[^1] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(Convert.ToBase64String(data)));
        Assert.ThrowsAny<CryptographicException>(() => other.Unprotect(protector.Protect("secret")));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(Convert.ToBase64String(new byte[5])));
        Assert.Throws<FormatException>(() => protector.Unprotect("not base64!"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("AAAA")]
    public void Missing_or_invalid_keys_are_not_accepted(string? key) =>
        Assert.Null(AesGcmSecretProtector.TryCreate(key));

    [Fact]
    public void A_valid_key_is_accepted_and_wrong_sizes_are_refused()
    {
        Assert.NotNull(AesGcmSecretProtector.TryCreate(Convert.ToBase64String(Key)));
        Assert.Throws<ArgumentException>(() => new AesGcmSecretProtector(new byte[16]));
    }
}

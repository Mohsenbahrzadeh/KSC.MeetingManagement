using System.Net;
using System.Text;
using Bunit;
using KSC.MeetingManagement.Web.Components.Pages;
using KSC.MeetingManagement.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace KSC.MeetingManagement.UnitTests;

public sealed class RoomTests : BunitContext, IAsyncLifetime
{
    private static readonly Guid MeetingId = Guid.NewGuid();

    private readonly FakeAuthServer _auth = new();
    private HttpStatusCode _joinStatus = HttpStatusCode.OK;
    private int _joinCalls;

    public RoomTests()
    {
        Services.AddMudServices();
        var handler = new RoomHandler(this);
        Services.AddSingleton<IHttpClientFactory>(new FakeFactory(handler));
        Services.AddSingleton<RoomApiClient>();
        Services.AddSingleton<SessionState>(_ => new SessionState(new AuthApiClient(new FakeFactory(_auth)), new MemoryStore(), new FakeClock(_auth.Now)));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    private sealed class RoomHandler(RoomTests owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            owner._joinCalls++;
            var response = new HttpResponseMessage(owner._joinStatus);
            if (owner._joinStatus == HttpStatusCode.OK)
            {
                response.Content = new StringContent(
                    """{"serverUrl":"wss://media.test","token":"t","room":"r","role":"Host","expiresAtUtc":"2030-01-01T00:00:00Z","iceServers":[{"urls":["turn:t.test:3478"],"username":"u","credential":"c"}]}""",
                    Encoding.UTF8,
                    "application/json");
            }

            return Task.FromResult(response);
        }
    }

    private BunitJSModuleInterop SetupModule(string previewResult = "ok", string connectResult = "ok")
    {
        var module = JSInterop.SetupModule("./ksc-room.js");
        module.Setup<bool>("isSupported").SetResult(true);
        module.Setup<string>("startPreview", _ => true).SetResult(previewResult);
        module.Setup<string>("connect", _ => true).SetResult(connectResult);
        module.SetupVoid("disconnect").SetVoidResult();
        module.SetupVoid("stopPreview").SetVoidResult();
        return module;
    }

    [Fact]
    public void The_lobby_shows_the_self_view_and_the_join_button_and_opens_the_devices()
    {
        var module = SetupModule();

        var cut = Render<Room>(p => p.Add(c => c.MeetingId, MeetingId));

        cut.WaitForAssertion(() => module.VerifyInvoke("startPreview"));
        Assert.NotEmpty(cut.FindAll("video.ksc-preview"));
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Contains("ورود به جلسه", StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[role=alert]"));
    }

    [Theory]
    [InlineData("NotAllowedError", "مسدود")]
    [InlineData("NotFoundError", "پیدا نشد")]
    [InlineData("NotReadableError", "برنامهٔ دیگری")]
    public void A_device_problem_is_explained_in_plain_persian_and_does_not_block_joining(string code, string expected)
    {
        SetupModule(previewResult: code);

        var cut = Render<Room>(p => p.Add(c => c.MeetingId, MeetingId));

        cut.WaitForAssertion(() => Assert.Contains(expected, cut.Find("[role=alert]").TextContent, StringComparison.Ordinal));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("ورود به جلسه", StringComparison.Ordinal) && b.HasAttribute("disabled"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "باز نیست")]
    [InlineData(HttpStatusCode.Forbidden, "اجازه")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "حضوری")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "در دسترس نیست")]
    public void A_refused_join_shows_the_reason_and_stays_in_the_lobby(HttpStatusCode status, string expected)
    {
        SetupModule();
        _joinStatus = status;
        var cut = Render<Room>(p => p.Add(c => c.MeetingId, MeetingId));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("video.ksc-preview")));

        cut.FindAll("button").First(b => b.TextContent.Contains("ورود به جلسه", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains(expected, cut.Find("[role=status]").TextContent, StringComparison.Ordinal));
        Assert.Equal(1, _joinCalls);
        Assert.NotEmpty(cut.FindAll("video.ksc-preview"));
    }

    [Fact]
    public void A_granted_join_connects_with_the_token_and_relay_servers_and_shows_the_controls()
    {
        var module = SetupModule();
        var cut = Render<Room>(p => p.Add(c => c.MeetingId, MeetingId));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("video.ksc-preview")));

        cut.FindAll("button").First(b => b.TextContent.Contains("ورود به جلسه", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=toolbar]")));
        var connect = module.Invocations.Single(i => i.Identifier == "connect");
        Assert.Equal("wss://media.test", connect.Arguments[0]);
        Assert.Equal("t", connect.Arguments[1]);
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Contains("خروج", StringComparison.Ordinal));
    }

    [Fact]
    public void If_the_media_connection_fails_the_user_returns_to_the_lobby_with_a_message()
    {
        SetupModule(connectResult: "boom");
        var cut = Render<Room>(p => p.Add(c => c.MeetingId, MeetingId));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("video.ksc-preview")));

        cut.FindAll("button").First(b => b.TextContent.Contains("ورود به جلسه", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains("برقرار نشد", cut.Find("[role=status]").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[role=toolbar]"));
    }

    [Fact]
    public void A_final_disconnect_can_rejoin_with_a_fresh_room_token()
    {
        var module = SetupModule();
        var cut = Render<Room>(p => p.Add(c => c.MeetingId, MeetingId));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("video.ksc-preview")));

        cut.FindAll("button").First(b => b.TextContent.Contains("ورود به جلسه", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=toolbar]")));

        cut.InvokeAsync(() => cut.Instance.OnRoomState("disconnected"));

        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("button"), b => b.TextContent.Contains("اتصال دوباره", StringComparison.Ordinal)));
        cut.FindAll("button").First(b => b.TextContent.Contains("اتصال دوباره", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=toolbar]")));
        Assert.Equal(2, _joinCalls);
        Assert.Equal(2, module.Invocations.Count(i => i.Identifier == "connect"));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("اتصال دوباره", StringComparison.Ordinal));
    }

    [Fact]
    public void Leaving_intentionally_does_not_offer_reconnect()
    {
        SetupModule();
        var cut = Render<Room>(p => p.Add(c => c.MeetingId, MeetingId));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("video.ksc-preview")));

        cut.FindAll("button").First(b => b.TextContent.Contains("ورود به جلسه", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=toolbar]")));
        cut.FindAll("button").First(b => b.TextContent.Contains("خروج", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains("از جلسه خارج شدید", cut.Find("[role=status]").TextContent, StringComparison.Ordinal));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("اتصال دوباره", StringComparison.Ordinal));
    }

    [Fact]
    public void The_participant_roster_shows_local_and_remote_names()
    {
        SetupModule();
        var cut = Render<Room>(p => p.Add(c => c.MeetingId, MeetingId));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("video.ksc-preview")));
        cut.FindAll("button").First(b => b.TextContent.Contains("ورود به جلسه", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=toolbar]")));

        cut.InvokeAsync(() => cut.Instance.OnParticipants(
        [
            new Room.RoomParticipant("local-user", "سارا", true),
            new Room.RoomParticipant("remote-user", "رضا", false)
        ]));

        cut.WaitForAssertion(() =>
        {
            var roster = cut.Find("[aria-label='شرکت‌کنندگان جلسه']");
            Assert.Contains("سارا", roster.TextContent, StringComparison.Ordinal);
            Assert.Contains("رضا", roster.TextContent, StringComparison.Ordinal);
            Assert.Contains("2 نفر در اتاق", roster.TextContent, StringComparison.Ordinal);
        });
    }
}

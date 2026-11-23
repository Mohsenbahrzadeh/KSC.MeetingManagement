using System.Net;
using System.Text;
using System.Text.Json;
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
    private string _joinRole = "Host";
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
            if (request.RequestUri?.AbsolutePath.EndsWith("/room/join", StringComparison.Ordinal) == true)
            {
                owner._joinCalls++;
            }

            if (request.RequestUri?.AbsolutePath.EndsWith("/media-permissions", StringComparison.Ordinal) == true
                || request.RequestUri?.AbsolutePath.EndsWith("/microphone-permission", StringComparison.Ordinal) == true
                || request.RequestUri?.AbsolutePath.EndsWith("/screen-share-permission", StringComparison.Ordinal) == true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            var response = new HttpResponseMessage(owner._joinStatus);
            if (request.RequestUri?.AbsolutePath.EndsWith("/mute-all", StringComparison.Ordinal) == true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            if (owner._joinStatus == HttpStatusCode.OK)
            {
                response.Content = new StringContent(
                    $$"""{"serverUrl":"wss://media.test","token":"t","room":"r","role":"{{owner._joinRole}}","expiresAtUtc":"2030-01-01T00:00:00Z","presenterIdentity":"u-host","iceServers":[{"urls":["turn:t.test:3478"],"username":"u","credential":"c"}]}""",
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
        module.Setup<bool>("announceMuteAll").SetResult(true);
        module.Setup<bool>("requestPermission", _ => true).SetResult(true);
        module.Setup<bool>("decidePermission", _ => true).SetResult(true);
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
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Contains("بی‌صدا کردن همه", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_the_meeting_presenter_sees_the_mute_all_control()
    {
        _joinRole = "Participant";
        SetupModule();
        var cut = Render<Room>(p => p.Add(c => c.MeetingId, MeetingId));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("video.ksc-preview")));
        cut.FindAll("button").First(b => b.TextContent.Contains("ورود به جلسه", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=toolbar]")));

        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("بی‌صدا کردن همه", StringComparison.Ordinal));
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Contains("ضبط برای من", StringComparison.Ordinal));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("ضبط سروری جلسه", StringComparison.Ordinal));
    }

    [Fact]
    public void A_participant_can_start_a_private_local_recording_without_server_recording_access()
    {
        var module = SetupModule();
        module.Setup<string>("startLocalRecording", _ => true).SetResult("started-audio");
        JSInterop.Setup<bool>("confirm", _ => true).SetResult(true);
        _joinRole = "Participant";
        var cut = Render<Room>(p => p.Add(c => c.MeetingId, MeetingId));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("video.ksc-preview")));
        cut.FindAll("button").First(b => b.TextContent.Contains("ورود به جلسه", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=toolbar]")));

        cut.FindAll("button").First(b => b.TextContent.Contains("ضبط برای من", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains("ذخیره روی دستگاه شما", cut.Markup, StringComparison.Ordinal));
        Assert.Contains(module.Invocations, invocation => invocation.Identifier == "startLocalRecording");
        Assert.DoesNotContain(module.Invocations, invocation => invocation.Identifier == "SetRecordingAsync");
    }

    [Fact]
    public void A_muted_participant_is_shown_with_a_slash_over_the_microphone_icon()
    {
        var module = SetupModule();
        var cut = Render<Room>(p => p.Add(c => c.MeetingId, MeetingId));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("video.ksc-preview")));
        cut.FindAll("button").First(b => b.TextContent.Contains("ورود به جلسه", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=toolbar]")));
        cut.InvokeAsync(() => cut.Instance.OnParticipants(
        [
            new Room.RoomParticipant("u-host", "برگزارکننده", true, true),
            new Room.RoomParticipant("u-member", "شرکت‌کننده", false, false),
        ]));

        cut.WaitForAssertion(() => Assert.Contains("ksc-mic-off-icon", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("aria-label=\"میکروفون بی‌صدا\"", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void A_participant_is_notified_and_can_request_permission_to_speak()
    {
        _joinRole = "Participant";
        SetupModule();
        var cut = Render<Room>(p => p.Add(c => c.MeetingId, MeetingId));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("video.ksc-preview")));
        cut.FindAll("button").First(b => b.TextContent.Contains("ورود به جلسه", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=toolbar]")));

        cut.InvokeAsync(() => cut.Instance.OnMuteAllNotice());

        cut.WaitForAssertion(() => Assert.Contains("برگزارکننده میکروفن‌ها را بی‌صدا کرده است", cut.Markup, StringComparison.Ordinal));
        cut.FindAll("button").First(b => b.TextContent.Contains("درخواست صحبت", StringComparison.Ordinal)).Click();

        Assert.Contains("درخواست صحبت", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(JSInterop.Invocations, invocation => invocation.Identifier == "requestPermission" && Equals(invocation.Arguments[0], "speak"));
        cut.InvokeAsync(() => cut.Instance.OnParticipants(
        [
            new Room.RoomParticipant("u-member", "شرکت‌کننده", true, false),
        ]));
        cut.WaitForAssertion(() => Assert.Contains("ksc-raised-hand", cut.Markup, StringComparison.Ordinal));

        cut.InvokeAsync(() => cut.Instance.OnSpeakDecision(true));

        cut.WaitForAssertion(() => Assert.Contains("درخواست شما تأیید شد", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("disabled", cut.FindAll("button").First(b => b.GetAttribute("aria-label") == "میکروفون").OuterHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_presenter_can_approve_a_participants_speak_request()
    {
        SetupModule();
        var cut = Render<Room>(p => p.Add(c => c.MeetingId, MeetingId));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("video.ksc-preview")));
        cut.FindAll("button").First(b => b.TextContent.Contains("ورود به جلسه", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=toolbar]")));

        cut.InvokeAsync(() => cut.Instance.OnPermissionRequest("u-member", "شرکت‌کننده", "speak"));

        Assert.Contains("شرکت‌کننده", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("✋", cut.Markup, StringComparison.Ordinal);
        var allow = cut.FindAll("button").First(button => button.TextContent.Contains("اجازهٔ صحبت", StringComparison.Ordinal));
        allow.Click();

        cut.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, invocation => invocation.Identifier == "decidePermission"
            && Equals(invocation.Arguments[0], "u-member") && Equals(invocation.Arguments[1], "speak") && Equals(invocation.Arguments[2], true)));
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
            new Room.RoomParticipant("local-user", "سارا", true, true),
            new Room.RoomParticipant("remote-user", "رضا", false, false)
        ]));

        cut.WaitForAssertion(() =>
        {
            var roster = cut.Find("[aria-label='شرکت‌کنندگان جلسه']");
            Assert.Contains("سارا", roster.TextContent, StringComparison.Ordinal);
            Assert.Contains("رضا", roster.TextContent, StringComparison.Ordinal);
            Assert.Contains("سارا", roster.TextContent, StringComparison.Ordinal);
            Assert.Contains("میکروفون روشن", roster.TextContent, StringComparison.Ordinal);
            Assert.Contains("بی‌صدا", roster.TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_participant_list_can_be_collapsed_and_reopened_from_the_room_toolbar()
    {
        SetupModule();
        var cut = Render<Room>(p => p.Add(c => c.MeetingId, MeetingId));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("video.ksc-preview")));
        cut.FindAll("button").First(b => b.TextContent.Contains("ورود به جلسه", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ksc-room-roster-toggle")));

        cut.Find(".ksc-room-roster-toggle").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[aria-label='شرکت‌کنندگان جلسه']")));

        cut.Find(".ksc-room-roster-toggle").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[aria-label='شرکت‌کنندگان جلسه']")));
    }

    [Fact]
    public void Browser_stopping_screen_share_updates_the_control_state()
    {
        SetupModule();
        var cut = Render<Room>(p => p.Add(c => c.MeetingId, MeetingId));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("video.ksc-preview")));
        cut.FindAll("button").First(b => b.TextContent.Contains("ورود به جلسه", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role=toolbar]")));

        cut.InvokeAsync(() => cut.Instance.OnScreenShare(true));
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("button"), b => b.TextContent.Contains("توقف اشتراک", StringComparison.Ordinal)));

        cut.InvokeAsync(() => cut.Instance.OnScreenShare(false));
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("button"), b => b.TextContent.Contains("اشتراک صفحه", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_mute_all_client_calls_the_manager_endpoint()
    {
        var factory = new CapturingFactory();
        var client = new RoomApiClient(factory);

        var error = await client.MuteAllAsync(MeetingId, CancellationToken.None);

        Assert.Null(error);
        Assert.EndsWith($"/api/v1/meetings/{MeetingId}/room/mute-all", factory.Handler.LastRequest!.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Post, factory.Handler.LastRequest.Method);
    }

    [Fact]
    public async Task Recording_client_displays_the_api_problem_detail_for_unavailable_recording()
    {
        var factory = new CapturingFactory();
        factory.Handler.Response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { detail = "Recording path is not configured." }), Encoding.UTF8, "application/problem+json"),
        };
        var client = new RoomApiClient(factory);

        var error = await client.SetRecordingAsync(MeetingId, true, CancellationToken.None);

        Assert.Equal("Recording path is not configured.", error);
    }

    private sealed class CapturingFactory : IHttpClientFactory
    {
        public CapturingHandler Handler { get; } = new();

        public HttpClient CreateClient(string name) => new(Handler) { BaseAddress = new Uri("https://api.example.test/") };
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.NoContent);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(Response);
        }
    }

    [Fact]
    public void Preview_requests_only_the_devices_enabled_by_the_user()
    {
        var module = SetupModule();
        var cut = Render<Room>(p => p.Add(c => c.MeetingId, MeetingId));
        cut.WaitForAssertion(() => Assert.Contains(module.Invocations, i => i.Identifier == "startPreview"));
        Assert.Equal((true, true), LastPreviewRequest(module));

        cut.FindAll("button").First(b => b.GetAttribute("aria-label") == "میکروفون").Click();
        cut.WaitForAssertion(() => Assert.Equal((true, false), LastPreviewRequest(module)));

        cut.FindAll("button").First(b => b.GetAttribute("aria-label") == "دوربین").Click();
        cut.WaitForAssertion(() => Assert.Contains(module.Invocations, i => i.Identifier == "stopPreview"));
        Assert.Equal((true, false), LastPreviewRequest(module));
    }

    [Fact]
    public void Denying_preview_permission_does_not_block_joining_without_devices()
    {
        var module = SetupModule(previewResult: "NotAllowedError");
        var cut = Render<Room>(p => p.Add(c => c.MeetingId, MeetingId));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("video.ksc-preview")));

        cut.FindAll("button").First(b => b.GetAttribute("aria-label") == "میکروفون").Click();
        cut.FindAll("button").First(b => b.GetAttribute("aria-label") == "دوربین").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[role=alert]")));
        var join = cut.FindAll("button").First(b => b.TextContent.Contains("ورود به جلسه", StringComparison.Ordinal));
        Assert.False(join.HasAttribute("disabled"));
        join.Click();
        cut.WaitForAssertion(() => Assert.Contains(module.Invocations, i => i.Identifier == "connect"));
        var connect = module.Invocations.Last(i => i.Identifier == "connect");
        Assert.Equal(false, connect.Arguments[7]);
        Assert.Equal("u-host", connect.Arguments[8]);
        Assert.Equal(true, connect.Arguments[9]);
    }

    private static (bool Video, bool Audio) LastPreviewRequest(BunitJSModuleInterop module)
    {
        var args = module.Invocations.Last(i => i.Identifier == "startPreview").Arguments;
        return ((bool)args[1]!, (bool)args[2]!);
    }
}

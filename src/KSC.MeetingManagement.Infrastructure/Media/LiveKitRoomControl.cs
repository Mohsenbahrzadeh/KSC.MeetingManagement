using System.Net;
using System.Text;
using System.Text.Json;
using KSC.MeetingManagement.Application.Media;

namespace KSC.MeetingManagement.Infrastructure.Media;

/// <summary>
/// Calls the LiveKit RoomService (Twirp over HTTP/JSON) with a short-lived admin token limited to one room. A participant or room that is
/// already gone (404) counts as success; any other failure is thrown so the caller can count and log it.
/// </summary>
public sealed class LiveKitRoomControl(HttpClient http, MediaOptions options, TimeProvider clock) : IMediaRoomControl
{
    private static readonly TimeSpan AdminTokenLifetime = TimeSpan.FromSeconds(60);

    public Task CloseRoomAsync(string room, CancellationToken cancellationToken) =>
        CallAsync("DeleteRoom", room, new Dictionary<string, string> { ["room"] = room }, cancellationToken);

    public Task RemoveParticipantAsync(string room, string identity, CancellationToken cancellationToken) =>
        CallAsync("RemoveParticipant", room, new Dictionary<string, string> { ["room"] = room, ["identity"] = identity }, cancellationToken);

    /// <summary>Public WebSocket address turned into the HTTP address of the same server (or the explicitly configured API address).</summary>
    public static Uri ApiBase(MediaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var text = string.IsNullOrWhiteSpace(options.ApiUrl) ? options.ServerUrl : options.ApiUrl;
        var uri = new Uri(text!, UriKind.Absolute);
        var scheme = uri.Scheme switch
        {
            "wss" => "https",
            "ws" => "http",
            var other => other,
        };
        return new UriBuilder(uri) { Scheme = scheme, Port = uri.IsDefaultPort ? -1 : uri.Port }.Uri;
    }

    private async Task CallAsync(string method, string room, Dictionary<string, string> body, CancellationToken cancellationToken)
    {
        if (!options.IsComplete)
        {
            return;
        }

        var now = clock.GetUtcNow();
        var token = LiveKitJwt.Sign(options.ApiSecret!, new Dictionary<string, object?>
        {
            ["iss"] = options.ApiKey,
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["exp"] = (now + AdminTokenLifetime).ToUnixTimeSeconds(),
            ["video"] = new Dictionary<string, object> { ["roomAdmin"] = true, ["room"] = room },
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiBase(options), $"twirp/livekit.RoomService/{method}"))
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }
}

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

    public async Task CloseRoomAsync(string room, CancellationToken cancellationToken) =>
        _ = await CallAsync("DeleteRoom", room, new Dictionary<string, object?> { ["room"] = room }, cancellationToken);

    public async Task RemoveParticipantAsync(string room, string identity, CancellationToken cancellationToken) =>
        _ = await CallAsync("RemoveParticipant", room, new Dictionary<string, object?> { ["room"] = room, ["identity"] = identity }, cancellationToken);

    public async Task MuteAllParticipantsAsync(string room, string exceptIdentity, CancellationToken cancellationToken)
    {
        using var participants = await CallAsync("ListParticipants", room, new Dictionary<string, object?> { ["room"] = room }, cancellationToken);
        if (participants is null)
        {
            return;
        }

        if (!participants.RootElement.TryGetProperty("participants", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var participant in values.EnumerateArray())
        {
            if (!participant.TryGetProperty("identity", out var identityValue)
                || identityValue.GetString() is not { } identity
                || string.Equals(identity, exceptIdentity, StringComparison.Ordinal))
            {
                continue;
            }

            if (!participant.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var track in tracks.EnumerateArray())
            {
                if (!track.TryGetProperty("type", out var type)
                    || type.ValueKind != JsonValueKind.String
                    || !string.Equals(type.GetString(), "AUDIO", StringComparison.OrdinalIgnoreCase)
                    || !track.TryGetProperty("sid", out var sidValue)
                    || sidValue.GetString() is not { } sid)
                {
                    continue;
                }

                _ = await CallAsync("MutePublishedTrack", room, new Dictionary<string, object?>
                {
                    ["room"] = room,
                    ["identity"] = identity,
                    ["track_sid"] = sid,
                    ["muted"] = true,
                }, cancellationToken);
            }
        }
    }

    public async Task SetScreenSharePermissionAsync(string room, string identity, bool allowed, CancellationToken cancellationToken)
    {
        using var current = await CallAsync("ListParticipants", room, new Dictionary<string, object?> { ["room"] = room }, cancellationToken);
        if (current?.RootElement.TryGetProperty("participants", out var participants) != true
            || participants.ValueKind != JsonValueKind.Array
            || !participants.EnumerateArray().Any(p => p.TryGetProperty("identity", out var value) && value.GetString() == identity))
        {
            throw new HttpRequestException("The participant is not currently present in the LiveKit room.");
        }

        var sources = allowed ? new[] { "CAMERA", "MICROPHONE", "SCREEN_SHARE" } : new[] { "CAMERA", "MICROPHONE" };
        _ = await CallAsync("UpdateParticipant", room, new Dictionary<string, object?>
        {
            ["room"] = room,
            ["identity"] = identity,
            ["permission"] = new Dictionary<string, object?>
            {
                ["canSubscribe"] = true,
                ["canPublish"] = true,
                ["canPublishData"] = true,
                ["canPublishSources"] = sources,
            },
        }, cancellationToken);
    }

    public async Task<bool> SetRecordingAsync(string room, bool enabled, string? recordingPath, CancellationToken cancellationToken)
    {
        if (!options.IsComplete || string.IsNullOrWhiteSpace(recordingPath)) return false;
        if (enabled)
        {
            using var active = await CallEgressAsync("ListEgress", room, new Dictionary<string, object?>
            {
                ["room_name"] = room,
                ["active"] = true,
            }, cancellationToken);
            if (active?.RootElement.TryGetProperty("items", out var items) == true
                && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0)
            {
                return true;
            }

            var path = $"{recordingPath.TrimEnd('/', '\\')}/{{room_name}}-{{time}}.mp4";
            using var result = await CallEgressAsync("StartRoomCompositeEgress", room, new Dictionary<string, object?>
            {
                ["room_name"] = room,
                ["layout"] = "grid",
                ["file_outputs"] = new[] { new Dictionary<string, object?> { ["filepath"] = path, ["file_type"] = "MP4" } },
            }, cancellationToken);
            var id = result?.RootElement.TryGetProperty("egress_id", out var startedEgressId) == true ? startedEgressId.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) throw new HttpRequestException("Egress did not return a recording id.");
            return true;
        }

        using var running = await CallEgressAsync("ListEgress", room, new Dictionary<string, object?>
        {
            ["room_name"] = room,
            ["active"] = true,
        }, cancellationToken);
        if (running?.RootElement.TryGetProperty("items", out var activeItems) != true
            || activeItems.ValueKind != JsonValueKind.Array || activeItems.GetArrayLength() == 0) return false;
        var egressId = activeItems[0].TryGetProperty("egress_id", out var idValue) ? idValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(egressId)) return false;
        _ = await CallEgressAsync("StopEgress", room, new Dictionary<string, object?> { ["egress_id"] = egressId }, cancellationToken);
        return true;
    }

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

    private async Task<JsonDocument?> CallAsync(string method, string room, Dictionary<string, object?> body, CancellationToken cancellationToken)
    {
        if (!options.IsComplete)
        {
            return null;
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

        if (response.StatusCode == HttpStatusCode.NotFound || method != "ListParticipants")
        {
            return null;
        }

        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }

    private async Task<JsonDocument?> CallEgressAsync(string method, string room, Dictionary<string, object?> body, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var token = LiveKitJwt.Sign(options.ApiSecret!, new Dictionary<string, object?>
        {
            ["iss"] = options.ApiKey,
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["exp"] = (now + AdminTokenLifetime).ToUnixTimeSeconds(),
            ["video"] = new Dictionary<string, object> { ["roomRecord"] = true, ["room"] = room },
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiBase(options), $"twirp/livekit.Egress/{method}"))
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }
}

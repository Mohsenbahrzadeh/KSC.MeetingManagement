using System.Text.RegularExpressions;

namespace KSC.MeetingManagement.UnitTests;

/// <summary>The web app must work without internet: the Persian font ships with it and no style or script points to another host.</summary>
public class OfflineAssetsTests
{
    private static string WebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.GetFiles("KSC.MeetingManagement.sln").Length == 0)
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "src", "KSC.MeetingManagement.Web");
    }

    [Fact]
    public void The_persian_font_is_bundled_with_its_licence_and_declared_locally()
    {
        var root = WebRoot();
        var css = File.ReadAllText(Path.Combine(root, "wwwroot", "app.css"));

        Assert.True(new FileInfo(Path.Combine(root, "wwwroot", "fonts", "Vazirmatn-Variable.woff2")).Length > 50_000);
        Assert.True(File.Exists(Path.Combine(root, "wwwroot", "fonts", "OFL.txt")));
        Assert.Contains("@font-face", css, StringComparison.Ordinal);
        Assert.Contains("fonts/Vazirmatn-Variable.woff2", css, StringComparison.Ordinal);
    }

    [Fact]
    public void No_stylesheet_script_or_font_is_loaded_from_another_host()
    {
        var root = WebRoot();
        var files = Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".css", StringComparison.Ordinal) || f.EndsWith(".html", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        var external = new Regex(@"(src|href)\s*=\s*""https?://|url\(\s*['""]?https?://|@import\s+(url\()?['""]?https?://", RegexOptions.IgnoreCase);

        var offenders = files.Where(f => external.IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).ToList();

        Assert.NotEmpty(files);
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_meeting_room_library_is_bundled_with_its_licence_and_loaded_only_from_this_site()
    {
        var wwwroot = Path.Combine(WebRoot(), "wwwroot");
        var script = File.ReadAllText(Path.Combine(wwwroot, "ksc-room.js"));

        Assert.True(new FileInfo(Path.Combine(wwwroot, "lib", "livekit-client", "livekit-client.esm.mjs")).Length > 500_000);
        Assert.True(File.Exists(Path.Combine(wwwroot, "lib", "livekit-client", "LICENSE.txt")));
        Assert.Contains("./lib/livekit-client/livekit-client.esm.mjs", script, StringComparison.Ordinal);
        Assert.Contains("adaptiveStream: true", script, StringComparison.Ordinal);
        Assert.Contains("dynacast: true", script, StringComparison.Ordinal);
        Assert.Contains("ScreenSharePresets.h1080fps15.encoding", script, StringComparison.Ordinal);
        Assert.Contains("ScreenSharePresets.h720fps15, ScreenSharePresets.h360fps15", script, StringComparison.Ordinal);
        Assert.Contains("contentHint: \"detail\"", script, StringComparison.Ordinal);
        Assert.Contains("video.videoWidth && video.videoHeight", script, StringComparison.Ordinal);
        Assert.Contains("VideoPresets.h1080.resolution", script, StringComparison.Ordinal);
        Assert.Contains("videoSimulcastLayers: [VideoPresets.h360, VideoPresets.h720]", script, StringComparison.Ordinal);
        Assert.Contains("RoomEvent.DataReceived", script, StringComparison.Ordinal);
        Assert.Contains("ksc-whiteboard-v1", script, StringComparison.Ordinal);
        Assert.Contains("sendWhiteboardSnapshot(participant.identity)", script, StringComparison.Ordinal);
        Assert.Contains("type: \"stroke-append\"", script, StringComparison.Ordinal);
        Assert.Contains("type: \"item\"", script, StringComparison.Ordinal);
        Assert.Contains("whiteboardTool === \"triangle\"", script, StringComparison.Ordinal);
        Assert.Contains("whiteboardTool === \"select\"", script, StringComparison.Ordinal);
        Assert.Contains("translateWhiteboardItem", script, StringComparison.Ordinal);
        Assert.Contains("distanceToSegment", script, StringComparison.Ordinal);
        Assert.Contains("export async function startLocalRecording", script, StringComparison.Ordinal);
        Assert.Contains("export async function stopLocalRecording", script, StringComparison.Ordinal);
        Assert.Contains("new MediaRecorder", script, StringComparison.Ordinal);
        Assert.Contains("30 * 60 * 1000", script, StringComparison.Ordinal);
        Assert.Contains("downloadRecordingPart", script, StringComparison.Ordinal);
        Assert.Contains("getDisplayMedia", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ksc-screen-share-on-board", script, StringComparison.Ordinal);
        Assert.DoesNotContain("whiteboardCanvas.parentElement.appendChild(tile)", script, StringComparison.Ordinal);
        Assert.Contains("permission-request", script, StringComparison.Ordinal);
        Assert.Contains("permission-decision", script, StringComparison.Ordinal);
        Assert.Contains("export async function requestPermission", script, StringComparison.Ordinal);
        Assert.Contains("canEditWhiteboard", script, StringComparison.Ordinal);
        Assert.Contains("ضبط سروری جلسه", File.ReadAllText(Path.Combine(WebRoot(), "Components", "Pages", "Room.razor")), StringComparison.Ordinal);
        Assert.Contains("ضبط برای من", File.ReadAllText(Path.Combine(WebRoot(), "Components", "Pages", "Room.razor")), StringComparison.Ordinal);
        Assert.Contains("export function setWhiteboardText", script, StringComparison.Ordinal);
        Assert.Contains("AudioPresets.speech", script, StringComparison.Ordinal);
        Assert.Contains("videoCaptureDefaults: { resolution: VideoPresets.h1080.resolution }", script, StringComparison.Ordinal);
        Assert.Contains("videoSimulcastLayers: [VideoPresets.h360, VideoPresets.h720]", script, StringComparison.Ordinal);
        Assert.Contains("publishData(bytes, { reliable: true, topic: whiteboardTopic, destinationIdentities })", script, StringComparison.Ordinal);
        Assert.DoesNotContain("adaptCameraQuality", script, StringComparison.Ordinal);
        var roomPage = File.ReadAllText(Path.Combine(WebRoot(), "Components", "Pages", "Room.razor"));
        Assert.Contains("کیفیت شبکه:", roomPage, StringComparison.Ordinal);
        Assert.Contains("OnConnectionQuality", roomPage, StringComparison.Ordinal);
        Assert.Contains("<canvas @ref=\"_whiteboard\"", roomPage, StringComparison.Ordinal);
        Assert.Contains("رسم دایره", roomPage, StringComparison.Ordinal);
        Assert.Contains("اجازهٔ اشتراک صفحه", roomPage, StringComparison.Ordinal);
        Assert.Contains("اجازهٔ وایت‌برد", roomPage, StringComparison.Ordinal);
        Assert.Contains("نوشتن متن", roomPage, StringComparison.Ordinal);
        Assert.DoesNotContain("_showWhiteboard", roomPage, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"import\s[^;]*from\s+['""](https?:)?//", script);
        Assert.DoesNotContain("import(\"http", script, StringComparison.Ordinal);
    }
}

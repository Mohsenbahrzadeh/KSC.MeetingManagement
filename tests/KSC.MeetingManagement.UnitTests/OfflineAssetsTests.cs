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
        Assert.DoesNotMatch(@"import\s[^;]*from\s+['""](https?:)?//", script);
        Assert.DoesNotContain("import(\"http", script, StringComparison.Ordinal);
    }
}

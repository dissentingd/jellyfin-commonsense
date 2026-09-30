using System.Reflection;
using System.Text.RegularExpressions;
using Acornima;

namespace Jellyfin.Plugin.RatingsFixer.Tests;

public partial class ConfigPageTests
{
    private static string Page()
    {
        var assembly = typeof(Plugin).Assembly;
        using var stream = assembly.GetManifestResourceStream("Jellyfin.Plugin.RatingsFixer.Configuration.configPage.html")
            ?? throw new InvalidOperationException("configPage.html isn't embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void SettingsPageScript_Parses()
    {
        // A syntax error here doesn't break the build - it silently breaks the whole settings page.
        var script = ScriptBlock().Match(Page());
        Assert.True(script.Success, "no <script> block found");

        var ex = Record.Exception(() => new Parser().ParseScript(script.Groups[1].Value));

        Assert.Null(ex);
    }

    [Fact]
    public void SettingsPage_UsesThePluginsId()
    {
        Assert.Contains($"var pluginId = '{new Guid("65127be7-01ef-469f-b13b-ecfa1289f5ed")}';", Page(), StringComparison.Ordinal);
    }

    [GeneratedRegex("<script type=\"text/javascript\">(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex ScriptBlock();
}

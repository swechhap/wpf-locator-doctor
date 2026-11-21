using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace WpfLocatorDoctor.Mcp.Tests;

/// <summary>Runs only when WLD_LIVE_TESTS=1: it starts a real app, so it needs Windows and an unlocked desktop.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WLD_LIVE_TESTS") != "1")
            Skip = "Starts the practice app; set WLD_LIVE_TESTS=1 on Windows with an unlocked desktop to run it.";
    }
}

/// <summary>
/// The Windows build's tools for a running app, used as an assistant would: over stdio with the official MCP client,
/// against the practice app in samples/PracticeApp.
/// </summary>
public sealed class LiveToolsTests : IAsyncLifetime
{
    private McpClient _client = null!;

    private static string ServerDll => Path.Combine(AppContext.BaseDirectory, "wpf-locator-doctor-mcp.dll");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WpfLocatorDoctor.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string PracticeAppExe()
    {
#if DEBUG
        string[] configurations = ["Debug", "Release"];
#else
        string[] configurations = ["Release", "Debug"];
#endif
        var candidates = configurations.Select(c => Path.Combine(RepoRoot(), "samples", "PracticeApp", "bin", c, "net10.0-windows", "PracticeApp.exe"));
        var exe = candidates.FirstOrDefault(File.Exists);
        Assert.True(exe is not null, "Build the solution first so samples/PracticeApp has a PracticeApp.exe.");
        return exe!;
    }

    public async Task InitializeAsync()
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "wpf-locator-doctor",
            Command = "dotnet",
            Arguments = [ServerDll],
        });
        _client = await McpClient.CreateAsync(transport);
    }

    public async Task DisposeAsync() => await _client.DisposeAsync();

    private async Task<JsonElement> Call(string tool, Dictionary<string, object?> args)
    {
        var result = await _client.CallToolAsync(tool, args);
        var text = result.Content.OfType<TextContentBlock>().First().Text;
        Assert.False(result.IsError == true, $"{tool} failed: {text}");
        using var json = JsonDocument.Parse(text);
        return json.RootElement.Clone();
    }

    private static string RefOf(JsonElement snapshot, string contains) =>
        snapshot.GetProperty("outline").EnumerateArray().Select(l => l.GetString()!)
            .First(l => l.Contains(contains, StringComparison.Ordinal)).Trim().Split(' ')[0];

    [Fact]
    public async Task Live_tools_explain_that_an_app_must_be_opened_first()
    {
        var result = await _client.CallToolAsync("snapshot_screen", new Dictionary<string, object?>());
        Assert.True(result.IsError);
        Assert.Contains("open_app", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text, StringComparison.Ordinal);
    }

    [LiveFact]
    public async Task An_assistant_can_explore_check_and_drive_the_practice_app()
    {
        // Start the app.
        var opened = await Call("open_app", new() { ["exePath"] = PracticeAppExe(), ["waitSeconds"] = 60 });
        Assert.Contains("#OrdersWindow", opened.GetProperty("summary").GetString(), StringComparison.Ordinal);

        try
        {
            // See the screen, with its known problems marked.
            var snap = await Call("snapshot_screen", []);
            var outline = string.Join("\n", snap.GetProperty("outline").EnumerateArray().Select(l => l.GetString()));
            Assert.Contains("#SearchButton", outline, StringComparison.Ordinal);
            Assert.Contains("#ActionButton ⚠ ID used 2 times", outline, StringComparison.Ordinal);
            Assert.Contains("⚠ no AutomationId", outline, StringComparison.Ordinal);

            // Recommend and check locators.
            var advice = await Call("recommend_locators", new() { ["ref"] = RefOf(snap, "#SearchButton") });
            var best = advice.GetProperty("locators")[0];
            Assert.Equal("high", best.GetProperty("strength").GetString());
            Assert.Equal("SearchButton", best.GetProperty("locator").GetProperty("automationId").GetString());

            var duplicate = await Call("check_locator", new() { ["automationId"] = "ActionButton" });
            Assert.Equal(2, duplicate.GetProperty("count").GetInt32());
            Assert.True((await Call("check_locator", new() { ["automationId"] = "SaveButton" })).GetProperty("unique").GetBoolean());

            // Type into the search box, and see the text in a new snapshot.
            await Call("act", new() { ["action"] = "type", ["ref"] = RefOf(snap, "#SearchBox"), ["text"] = "Order 42" });
            var after = await Call("snapshot_screen", []);
            Assert.Contains("#SearchBox = \"Order 42\"", string.Join("\n", after.GetProperty("outline").EnumerateArray().Select(l => l.GetString())), StringComparison.Ordinal);

            // Open the settings window through the menu, and wait for it.
            await Call("act", new() { ["action"] = "click", ["automationId"] = "FileMenu" });
            await Call("act", new() { ["action"] = "click", ["automationId"] = "OpenSettingsMenuItem" });
            var appeared = await Call("wait_for", new() { ["window"] = "SettingsWindow", ["timeoutSeconds"] = 20 });
            Assert.True(appeared.GetProperty("met").GetBoolean(), appeared.GetProperty("summary").GetString());

            // Tick a check box (its toggle behaviour), and take a picture of the window.
            var toggled = await Call("act", new() { ["action"] = "click", ["automationId"] = "EmailUpdatesCheckBox", ["window"] = "SettingsWindow" });
            Assert.Contains("toggled", toggled.GetProperty("method").GetString(), StringComparison.Ordinal);

            var shot = await _client.CallToolAsync("take_screenshot", new Dictionary<string, object?> { ["window"] = "SettingsWindow" });
            Assert.False(shot.IsError == true);
            var image = Assert.Single(shot.Content.OfType<ImageContentBlock>());
            Assert.Equal("image/png", image.MimeType);

            // Close the settings window with Cancel and wait for it to go.
            await Call("act", new() { ["action"] = "click", ["automationId"] = "SettingsCancelButton" });
            var gone = await Call("wait_for", new() { ["window"] = "SettingsWindow", ["until"] = "disappears", ["timeoutSeconds"] = 20 });
            Assert.True(gone.GetProperty("met").GetBoolean(), gone.GetProperty("summary").GetString());

            // Score the running app, matched to its source code.
            var score = await Call("score_live_app", new() { ["sourceFolder"] = Path.Combine(RepoRoot(), "samples", "PracticeApp") });
            Assert.False(string.IsNullOrEmpty(score.GetProperty("grade").GetString()));
            Assert.Contains(score.GetProperty("screens").EnumerateArray(), s => s.GetProperty("name").GetString() == "OrdersWindow");
        }
        finally
        {
            var closed = await Call("close_app", []);
            Assert.StartsWith("Closed", closed.GetProperty("summary").GetString(), StringComparison.Ordinal);
        }
    }
}

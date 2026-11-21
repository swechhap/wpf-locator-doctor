using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace WpfLocatorDoctor.Mcp.Tests;

/// <summary>
/// Starts the real MCP server (as an AI assistant would, over standard input/output) and talks to it with the official MCP client.
/// This checks the parts the Core tests can't: the tool list the assistant sees, the JSON it gets back, and clean errors.
/// </summary>
public sealed class McpServerTests : IAsyncLifetime
{
    private McpClient _client = null!;

    private static string ServerDll => Path.Combine(AppContext.BaseDirectory, "wpf-locator-doctor-mcp.dll");

    private static string PracticeAppFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WpfLocatorDoctor.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "samples", "PracticeApp");
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

    private async Task<(bool IsError, string Text)> Call(string tool, Dictionary<string, object?> args)
    {
        var result = await _client.CallToolAsync(tool, args);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        return (result.IsError == true, text);
    }

    [Fact]
    public void The_server_introduces_itself_and_explains_the_workflow()
    {
        Assert.Equal("wpf-locator-doctor", _client.ServerInfo?.Name);
        Assert.Contains("score_source", _client.ServerInstructions ?? "", StringComparison.Ordinal);
    }

    private static readonly string[] SourceToolNames =
        ["compare_to_baseline", "explain_rules", "list_problems", "score_source", "suggest_xaml_fixes"];

    private static readonly string[] LiveToolNames =
        ["act", "check_locator", "close_app", "open_app", "recommend_locators", "score_live_app", "snapshot_screen", "take_screenshot", "wait_for"];

    [Fact]
    public async Task The_assistant_sees_well_described_tools()
    {
        var tools = await _client.ListToolsAsync();
#if LIVE_TOOLS
        string[] expected = [.. SourceToolNames, .. LiveToolNames];
#else
        var expected = SourceToolNames;
#endif
        Assert.Equal(expected.Order(StringComparer.Ordinal), tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.InRange(SourceToolNames.Length + LiveToolNames.Length, 10, 15); // a small, well-chosen tool list

        foreach (var tool in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            Assert.False(string.IsNullOrWhiteSpace(tool.ProtocolTool.Title));
            var properties = tool.JsonSchema.GetProperty("properties");
            foreach (var p in properties.EnumerateObject())
                Assert.True(p.Value.TryGetProperty("description", out _), $"{tool.Name}.{p.Name} has no description.");
        }

        // The source tools only read, except score_source (which can save report files).
        Assert.All(tools.Where(t => SourceToolNames.Contains(t.Name) && t.Name != "score_source"), t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint));
        Assert.False(tools.Single(t => t.Name == "score_source").ProtocolTool.Annotations?.DestructiveHint);
#if LIVE_TOOLS
        // Tools that change the app say so, so assistants ask before using them.
        Assert.True(tools.Single(t => t.Name == "act").ProtocolTool.Annotations?.DestructiveHint);
        Assert.True(tools.Single(t => t.Name == "close_app").ProtocolTool.Annotations?.DestructiveHint);
        Assert.True(tools.Single(t => t.Name == "snapshot_screen").ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.Contains("open_app", _client.ServerInstructions ?? "", StringComparison.Ordinal);
#else
        Assert.Contains("Windows download", _client.ServerInstructions ?? "", StringComparison.Ordinal);
#endif
    }

    [Fact]
    public async Task Score_source_returns_the_score_as_json()
    {
        var (isError, text) = await Call("score_source", new() { ["path"] = PracticeAppFolder() });

        Assert.False(isError);
        using var json = JsonDocument.Parse(text);
        Assert.Equal("B", json.RootElement.GetProperty("grade").GetString());
        Assert.Equal(86.5, json.RootElement.GetProperty("score").GetDouble());
        Assert.Equal("MainWindow", json.RootElement.GetProperty("screens")[0].GetProperty("name").GetString());
        Assert.False(json.RootElement.TryGetProperty("savedReports", out _)); // empty fields are left out
    }

    [Fact]
    public async Task Xaml_snippets_stay_readable()
    {
        var (_, text) = await Call("suggest_xaml_fixes", new() { ["path"] = PracticeAppFolder(), ["screen"] = "MainWindow", ["limit"] = 3 });

        Assert.Contains("<MenuItem AutomationProperties.AutomationId=\\\"ExitMenuItem\\\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0022", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u003C", text, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(text);
        Assert.Equal(3, json.RootElement.GetProperty("shown").GetInt32());
    }

    [Fact]
    public async Task Other_tools_answer_too()
    {
        var (problemsError, problems) = await Call("list_problems", new() { ["path"] = PracticeAppFolder(), ["rule"] = "WT004" });
        Assert.False(problemsError);
        Assert.Contains("\"rule\":\"WT004\"", problems, StringComparison.Ordinal);

        var (rulesError, rules) = await Call("explain_rules", []);
        Assert.False(rulesError);
        Assert.Contains("WT012", rules, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compare_to_baseline_round_trips_a_saved_report()
    {
        var outDir = Directory.CreateTempSubdirectory("wld-mcp-").FullName;
        try
        {
            var (_, score) = await Call("score_source", new() { ["path"] = PracticeAppFolder(), ["saveReportTo"] = outDir });
            Assert.Contains("savedReports", score, StringComparison.Ordinal);

            var (isError, diff) = await Call("compare_to_baseline", new()
            {
                ["path"] = PracticeAppFolder(),
                ["baselineReport"] = Path.Combine(outDir, "testability-report.json"),
            });
            Assert.False(isError);
            using var json = JsonDocument.Parse(diff);
            Assert.Equal(0, json.RootElement.GetProperty("change").GetDouble());
            Assert.Equal(0, json.RootElement.GetProperty("missingIdCount").GetInt32());
        }
        finally
        {
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task Mistakes_come_back_as_plain_error_messages()
    {
        var (isError, text) = await Call("list_problems", new() { ["path"] = PracticeAppFolder(), ["screen"] = "Nope" });
        Assert.True(isError);
        Assert.Equal("No screen called 'Nope'. Screens found: MainWindow, SettingsWindow, AddressPanel.", text);

        (isError, text) = await Call("score_source", new() { ["path"] = "/definitely/not/here" });
        Assert.True(isError);
        Assert.StartsWith("Not found:", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--version", @"^\d+\.\d+\.\d+\s*$")]
    [InlineData("--help", "claude mcp add")]
    public async Task Running_it_by_hand_prints_help_instead_of_waiting(string flag, string expected)
    {
        using var process = Process.Start(new ProcessStartInfo("dotnet", [ServerDll, flag])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        Assert.Matches(expected, output);
    }
}

using WpfLocatorDoctor.Core.Assistant;
using WpfLocatorDoctor.Core.Live;

namespace WpfLocatorDoctor.Core.Tests;

/// <summary>
/// The live tools' decisions (what a snapshot shows, which locator is best, how to click, waiting), tested with a fake app.
/// No Windows or running app needed.
/// </summary>
public class LiveSessionTests
{
    // ------------------------------------------------------------------ fakes

    private sealed class Node(string controlType, string? id = null, string? name = null) : ILiveNode
    {
        public string ControlType { get; } = controlType;
        public string? AutomationId { get; } = id;
        public string? Name { get; } = name;
        public string? ClassName { get; init; }
        public bool IsKeyboardFocusable { get; init; } = true;
        public bool IsOffscreen { get; init; }
        public double Width { get; init; } = 100;
        public double Height { get; init; } = 30;
        public IReadOnlyList<string> Patterns { get; init; } = [];
        public IReadOnlyList<ILiveNode> Children { get; set; } = [];
        public bool IsEnabled { get; set; } = true;
        public bool IsPassword { get; init; }
        public string? Value { get; set; }
        public string? ToggleState { get; set; }
        public string? ExpandState { get; init; }
        public bool? IsSelected { get; init; }
    }

    private sealed class FakeApp(params Node[] windows) : ILiveApp
    {
        public List<Node> Windows { get; } = [.. windows];
        public List<(ILiveNode Element, LiveActionKind Action, string? Text)> Performed { get; } = [];
        public Action<ILiveNode, LiveActionKind, string?>? OnPerform { get; set; }
        public Action? OnRead { get; set; }
        public string Name { get; init; } = "PracticeApp";
        public int ProcessId => 42;
        public bool StartedByTool { get; init; } = true;
        public bool HasExited { get; set; }
        public bool Closed { get; private set; }
        public bool Disposed { get; private set; }
        public int Reads { get; private set; }

        public IReadOnlyList<ILiveNode> ReadWindows(bool includeChildren)
        {
            Reads++;
            OnRead?.Invoke();
            return Windows;
        }

        public void Perform(ILiveNode element, LiveActionKind action, string? text)
        {
            Performed.Add((element, action, text));
            OnPerform?.Invoke(element, action, text);
        }

        public byte[] CapturePng(ILiveNode element, ILiveNode window) => [0x89, (byte)'P', (byte)'N', (byte)'G'];
        public void Close() => Closed = true;
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeConnector(FakeApp app) : ILiveAppConnector
    {
        public LiveOpenRequest? Request { get; private set; }

        public ILiveApp Open(LiveOpenRequest request)
        {
            Request = request;
            return app;
        }
    }

    /// <summary>A clock that moves forward only when the session waits, so waiting tests run instantly.</summary>
    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;

        public Task Delay(TimeSpan time, CancellationToken _)
        {
            Now += time;
            return Task.CompletedTask;
        }
    }

    // ------------------------------------------------------------------ the practice-style app

    private static Node Button(string? id, string? name) => new("Button", id, name) { ClassName = "Button", Patterns = ["Invoke"] };

    private static Node Row(string? id, string text) => new("ListItem", id, text)
    {
        ClassName = "ListBoxItem",
        Patterns = ["SelectionItem"],
        Children = [Button("RemoveButton", "Remove"), new Node("Text", null, text) { IsKeyboardFocusable = false }],
    };

    private static Node OrdersWindow() => new("Window", "OrdersWindow", "Orders")
    {
        ClassName = "Window",
        Children =
        [
            new Node("TitleBar", "TitleBar") { Children = [Button("Close", "Close")] },
            new Node("Text", null, "Search:") { IsKeyboardFocusable = false },
            new Node("Edit", "SearchBox") { ClassName = "TextBox", Patterns = ["Value"], Value = "abc" },
            Button("SearchButton", "Search"),
            Button("Button_1", "Clear"),
            Button(null, "Refresh"),
            Button("ActionButton", "Delete"),
            Button("ActionButton", "Export"),
            new Node("CheckBox", "OnlyMineCheckBox", "Only my orders") { ClassName = "CheckBox", Patterns = ["Toggle"], ToggleState = "Off" },
            new Node("ComboBox", null, null) { ClassName = "ComboBox", Patterns = ["ExpandCollapse", "Selection"], ExpandState = "Collapsed" },
            new Node("Edit", "PasswordInput") { ClassName = "PasswordBox", Patterns = ["Value"], IsPassword = true, Value = "secret" },
            new Node("List", "OrdersList", "Recent orders")
            {
                ClassName = "ListBox",
                Patterns = ["Selection"],
                Children = [Row(null, "Order 1"), Row(null, "Order 2"), Row(null, "Order 3")],
            },
            new Node("Group", "Totals")
            {
                ClassName = "GroupBox",
                IsKeyboardFocusable = false,
                Children = [Button("RemoveButton", "Remove")],
            },
            new Node("Custom", null, null) { ClassName = "StarRating", IsKeyboardFocusable = false, Width = 120, Height = 24 },
            new Node("Pane") { ClassName = "StackPanel", IsKeyboardFocusable = false, Children = [Button("SaveButton", "Save")] },
        ],
    };

    private static (LiveSession Session, FakeApp App, FakeTime Time) Open(FakeApp? app = null, LiveSessionOptions? options = null)
    {
        app ??= new FakeApp(OrdersWindow());
        var time = new FakeTime();
        var session = new LiveSession(new FakeConnector(app), options ?? new LiveSessionOptions { Time = time, Delay = time.Delay });
        var exe = Path.Combine(Path.GetTempPath(), "PracticeApp.exe");
        File.WriteAllText(exe, "");
        session.Open(exePath: exe);
        return (session, app, time);
    }

    private static string RefOf(ScreenSnapshot snapshot, string contains) =>
        snapshot.Outline.First(l => l.Contains(contains, StringComparison.Ordinal)).Trim().Split(' ')[0];

    // ------------------------------------------------------------------ open / close

    [Fact]
    public void Opening_needs_either_an_exe_or_a_process()
    {
        var session = new LiveSession(new FakeConnector(new FakeApp()));
        Assert.Contains("either exePath", Assert.Throws<DoctorInputException>(() => session.Open()).Message, StringComparison.Ordinal);
        Assert.Contains("either exePath", Assert.Throws<DoctorInputException>(() => session.Open("a.exe", process: "x")).Message, StringComparison.Ordinal);
        Assert.StartsWith("Not found:", Assert.Throws<DoctorInputException>(() => session.Open("/nope/App.exe")).Message, StringComparison.Ordinal);
        Assert.Contains("isn't an .exe", Assert.Throws<DoctorInputException>(() => session.Open("/nope/App.dll")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Opening_reports_the_windows_and_the_next_step()
    {
        var app = new FakeApp(OrdersWindow());
        var connector = new FakeConnector(app);
        using var session = new LiveSession(connector);
        var status = session.Open(process: "PracticeApp", useUia2: true, waitSeconds: 500);

        Assert.Equal("PracticeApp", connector.Request!.Process);
        Assert.True(connector.Request.UseUia2);
        Assert.Equal(TimeSpan.FromSeconds(120), connector.Request.WaitForWindow); // capped
        Assert.Equal(["\"Orders\" #OrdersWindow"], status.Windows);
        Assert.Contains("snapshot_screen", status.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_allowed_apps_can_be_opened()
    {
        var options = new LiveSessionOptions { AllowedApps = LiveSessionOptions.ParseAllowedApps("MyApp.exe; Other") };
        Assert.Equal(["MyApp", "Other"], options.AllowedApps);

        var app = new FakeApp(OrdersWindow()) { Name = "PracticeApp" };
        using var session = new LiveSession(new FakeConnector(app), options);
        var error = Assert.Throws<DoctorInputException>(() => session.Open(process: "PracticeApp.exe"));
        Assert.Contains(LiveSessionOptions.AllowedAppsVariable, error.Message, StringComparison.Ordinal);

        // Attaching by process ID: the name is only known after attaching, so the app is let go again.
        Assert.Throws<DoctorInputException>(() => session.Open(process: "1234"));
        Assert.True(app.Disposed);
        Assert.Contains("No app is open", Assert.Throws<DoctorInputException>(() => session.Snapshot()).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Closing_closes_an_app_the_tool_started_but_only_lets_go_of_an_attached_one()
    {
        var (session, app, _) = Open();
        Assert.Equal("Closed PracticeApp.", session.Close().Summary);
        Assert.True(app.Closed);
        Assert.Equal("No app is open.", session.Close().Summary);

        var attached = new FakeApp(OrdersWindow()) { StartedByTool = false };
        using var other = new LiveSession(new FakeConnector(attached));
        other.Open(process: "PracticeApp");
        Assert.Contains("still running", other.Close().Summary, StringComparison.Ordinal);
        Assert.False(attached.Closed);
        Assert.True(attached.Disposed);
    }

    [Fact]
    public void Tools_explain_when_the_app_has_closed()
    {
        var (session, app, _) = Open();
        app.HasExited = true;
        Assert.Contains("has closed", Assert.Throws<DoctorInputException>(() => session.Snapshot()).Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ snapshot

    [Fact]
    public void Snapshot_shows_interesting_elements_with_refs_states_and_problems()
    {
        var (session, _, _) = Open();
        var snap = session.Snapshot();
        var outline = string.Join("\n", snap.Outline);

        Assert.StartsWith("e1 Window \"Orders\" #OrdersWindow", snap.Outline[0], StringComparison.Ordinal);
        Assert.Contains("  e", snap.Outline[1], StringComparison.Ordinal); // children are indented
        Assert.Contains("Edit #SearchBox = \"abc\"", outline, StringComparison.Ordinal);
        Assert.Contains("Button \"Clear\" #Button_1 ⚠ ID looks machine-made", outline, StringComparison.Ordinal);
        Assert.Contains("Button \"Refresh\" ⚠ no AutomationId", outline, StringComparison.Ordinal);
        Assert.Contains("Button \"Delete\" #ActionButton ⚠ ID used 2 times", outline, StringComparison.Ordinal);
        Assert.Contains("CheckBox \"Only my orders\" #OnlyMineCheckBox [unchecked]", outline, StringComparison.Ordinal);
        Assert.Contains("ComboBox [collapsed] ⚠ no AutomationId", outline, StringComparison.Ordinal);
        Assert.Contains("List \"Recent orders\" #OrdersList ⚠ rows can't be told apart", outline, StringComparison.Ordinal);
        Assert.Contains("Custom(StarRating) ⚠ test tools can't see inside", outline, StringComparison.Ordinal);
        Assert.Contains("Text \"Search:\"", outline, StringComparison.Ordinal);

        // Passwords are never shown; window furniture and a button's inner text are left out.
        Assert.Contains("Edit #PasswordInput [password]", outline, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", outline, StringComparison.Ordinal);
        Assert.DoesNotContain("TitleBar", outline, StringComparison.Ordinal);
        // A plain layout pane is skipped but its button is shown.
        Assert.DoesNotContain("StackPanel", outline, StringComparison.Ordinal);
        Assert.Contains("#SaveButton", outline, StringComparison.Ordinal);

        Assert.Contains("⚠", snap.Summary, StringComparison.Ordinal);
        Assert.Contains("ActionButton", snap.Summary, StringComparison.Ordinal);
        Assert.Equal(snap.Shown, snap.Total);
    }

    [Fact]
    public void Snapshot_is_size_limited_and_says_so()
    {
        var (session, _, _) = Open();
        var snap = session.Snapshot(maxElements: 5);
        Assert.Equal(5, snap.Outline.Count);
        Assert.True(snap.Total > 5);
        Assert.Contains("Showing 5 of", snap.Summary, StringComparison.Ordinal);

        var everything = session.Snapshot(interestingOnly: false, maxElements: 1000);
        Assert.Contains(everything.Outline, l => l.Contains("Pane(StackPanel)", StringComparison.Ordinal));
    }

    [Fact]
    public void Snapshot_can_show_one_window_and_explains_unknown_ones()
    {
        var settings = new Node("Window", "SettingsWindow", "Settings") { Children = [Button("SettingsOkButton", "OK")] };
        var (session, _, _) = Open(new FakeApp(OrdersWindow(), settings));

        var snap = session.Snapshot(window: "settings");
        Assert.Equal(["\"Settings\" #SettingsWindow"], snap.Windows);
        Assert.Contains(snap.Notes, n => n.Contains("Orders", StringComparison.Ordinal));

        var error = Assert.Throws<DoctorInputException>(() => session.Snapshot(window: "Nope"));
        Assert.Equal("No window called 'Nope'. Open windows: \"Orders\" #OrdersWindow, \"Settings\" #SettingsWindow.", error.Message);
    }

    // ------------------------------------------------------------------ recommend

    [Fact]
    public void A_unique_meaningful_id_is_the_best_locator()
    {
        var (session, _, _) = Open();
        var advice = session.RecommendLocators(RefOf(session.Snapshot(), "#SearchButton"));

        var best = advice.Locators[0];
        Assert.Equal("high", best.Strength);
        Assert.Equal("SearchButton", best.Locator.AutomationId);
        Assert.Equal("OrdersWindow", best.Locator.Window);
        Assert.Equal("window.FindFirstDescendant(cf => cf.ByAutomationId(\"SearchButton\"))", best.FlaUi);
        Assert.Equal("driver.FindElement(MobileBy.AccessibilityId(\"SearchButton\"))", best.Appium);
        Assert.Null(advice.SuggestedAutomationId);
        Assert.Null(advice.AppFix);

        // Weaker options follow, worst last. (The title bar's Close button is the first Button, as a real search would see it.)
        Assert.Equal("low", advice.Locators[^1].Strength);
        Assert.Equal("window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))[1]", advice.Locators[^1].FlaUi);
        Assert.Equal("driver.FindElement(By.XPath(\"(//Button)[2]\"))", advice.Locators[^1].Appium);
    }

    [Fact]
    public void A_machine_made_id_is_ranked_below_the_name_and_an_app_fix_is_suggested()
    {
        var (session, _, _) = Open();
        var advice = session.RecommendLocators(RefOf(session.Snapshot(), "#Button_1"));

        Assert.Equal("Clear", advice.Locators[0].Locator.Name);
        Assert.Equal("medium", advice.Locators[0].Strength);
        Assert.Contains(advice.Locators, o => o.Locator.AutomationId == "Button_1" && o.Strength == "low");
        Assert.Equal("ClearButton", advice.SuggestedAutomationId);
        Assert.Contains("None is strong", advice.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repeated_id_is_found_inside_its_uniquely_identified_parent()
    {
        var (session, _, _) = Open();
        var snap = session.Snapshot();
        // The RemoveButton in the "Totals" group (the rows' RemoveButtons share the ID).
        var reference = snap.Outline.SkipWhile(l => !l.Contains("#Totals", StringComparison.Ordinal))
            .First(l => l.Contains("#RemoveButton", StringComparison.Ordinal)).Trim().Split(' ')[0];

        var best = session.RecommendLocators(reference).Locators[0];
        Assert.Equal("high", best.Strength);
        Assert.Equal(new LocatorQuery(AutomationId: "RemoveButton", Within: "Totals", Window: "OrdersWindow"), best.Locator);
        Assert.Equal("window.FindFirstDescendant(cf => cf.ByAutomationId(\"Totals\")).FindFirstDescendant(cf => cf.ByAutomationId(\"RemoveButton\"))", best.FlaUi);

        // Every recommended locator really finds exactly that element.
        foreach (var option in session.RecommendLocators(reference).Locators)
            Assert.True(session.CheckLocator(option.Locator).Unique, option.Locator.Describe());
    }

    [Fact]
    public void Duplicate_ids_get_a_control_type_or_name_locator()
    {
        var (session, _, _) = Open();
        var advice = session.RecommendLocators(RefOf(session.Snapshot(), "\"Export\" #ActionButton"));
        Assert.Equal(new LocatorQuery(Name: "Export", ControlType: "Button", Window: "OrdersWindow"), advice.Locators[0].Locator);
        Assert.Equal("driver.FindElement(By.XPath(\"//Button[@Name='Export']\"))", advice.Locators[0].Appium);
        Assert.Equal("ExportButton", advice.SuggestedAutomationId);
    }

    [Fact]
    public void Unknown_refs_are_explained()
    {
        var (session, _, _) = Open();
        Assert.Contains("take a snapshot first", Assert.Throws<DoctorInputException>(() => session.RecommendLocators("e1")).Message, StringComparison.Ordinal);
        session.Snapshot();
        Assert.Contains("take a new snapshot", Assert.Throws<DoctorInputException>(() => session.RecommendLocators("e999")).Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ check

    [Fact]
    public void Checking_a_locator_counts_its_matches()
    {
        var (session, _, _) = Open();

        var unique = session.CheckLocator(new LocatorQuery(AutomationId: "SaveButton"));
        Assert.True(unique.Unique);
        Assert.Contains("finds exactly one element", unique.Summary, StringComparison.Ordinal);

        var twice = session.CheckLocator(new LocatorQuery(AutomationId: "ActionButton"));
        Assert.False(twice.Unique);
        Assert.Equal(2, twice.Count);
        Assert.Equal(2, twice.Matches.Count);

        Assert.True(session.CheckLocator(new LocatorQuery(AutomationId: "ActionButton", Index: 2)).Unique);
        Assert.Equal(4, session.CheckLocator(new LocatorQuery(AutomationId: "RemoveButton")).Count);
        Assert.True(session.CheckLocator(new LocatorQuery(AutomationId: "RemoveButton", Within: "Totals")).Unique);
        Assert.True(session.CheckLocator(new LocatorQuery(Name: "Only my orders", ControlType: "checkbox")).Unique);
    }

    [Fact]
    public void A_locator_that_finds_nothing_lists_close_matches()
    {
        var (session, _, _) = Open();
        var check = session.CheckLocator(new LocatorQuery(AutomationId: "searchbutton"));
        Assert.Equal(0, check.Count);
        Assert.Contains(check.CloseMatches, l => l.Contains("#SearchButton", StringComparison.Ordinal));
        Assert.Contains("renamed", check.Summary, StringComparison.Ordinal);

        Assert.Throws<DoctorInputException>(() => session.CheckLocator(new LocatorQuery(Window: "Orders")));
        Assert.Throws<DoctorInputException>(() => session.CheckLocator(new LocatorQuery(AutomationId: "X", Window: "Nope")));
        Assert.Throws<DoctorInputException>(() => session.CheckLocator(new LocatorQuery(AutomationId: "X", Index: 0)));
    }

    // ------------------------------------------------------------------ act

    [Theory]
    [InlineData("#SearchButton", "click", null, LiveActionKind.Invoke)]
    [InlineData("#OnlyMineCheckBox", "click", null, LiveActionKind.Toggle)]
    [InlineData("ComboBox", "click", null, LiveActionKind.Expand)]
    [InlineData("ComboBox", "select", "Open", LiveActionKind.SelectChildByName)]
    [InlineData("\"Order 2\"", "click", null, LiveActionKind.Select)]
    [InlineData("#SearchBox", "type", "Order 42", LiveActionKind.SetValue)]
    [InlineData("#SearchBox", "focus", null, LiveActionKind.Focus)]
    [InlineData("Custom(StarRating)", "click", null, LiveActionKind.MouseClick)]
    [InlineData("ComboBox", "type", "Open", LiveActionKind.TypeText)]
    public async Task Actions_use_what_the_element_supports(string line, string action, string? text, LiveActionKind expected)
    {
        var (session, app, _) = Open();
        var result = await session.ActAsync(action, RefOf(session.Snapshot(), line), null, text);

        var (_, kind, sentText) = Assert.Single(app.Performed);
        Assert.Equal(expected, kind);
        Assert.Equal(text, sentText);
        Assert.StartsWith("Did it:", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Acting_by_locator_needs_exactly_one_match_and_reports_new_windows()
    {
        var (session, app, _) = Open();
        var settings = new Node("Window", "SettingsWindow", "Settings");
        app.OnPerform = (_, _, _) => app.Windows.Add(settings);

        var error = await Assert.ThrowsAsync<DoctorInputException>(() => session.ActAsync("click", null, new LocatorQuery(AutomationId: "ActionButton"), null));
        Assert.Contains("finds 2 elements", error.Message, StringComparison.Ordinal);

        var result = await session.ActAsync("click", null, new LocatorQuery(AutomationId: "SaveButton"), null);
        Assert.Equal(["\"Settings\" #SettingsWindow"], result.NewWindows);
        Assert.Contains("New window(s)", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabled_or_unsuitable_elements_are_refused_with_a_reason()
    {
        var (session, app, _) = Open();
        var snap = session.Snapshot();
        ((Node)app.Windows[0].Children.First(c => c.AutomationId == "SearchButton")).IsEnabled = false;

        var disabled = await Assert.ThrowsAsync<DoctorInputException>(() => session.ActAsync("click", RefOf(snap, "#SearchButton"), null, null));
        Assert.Contains("disabled", disabled.Message, StringComparison.Ordinal);
        var noText = await Assert.ThrowsAsync<DoctorInputException>(() => session.ActAsync("type", RefOf(snap, "#SearchBox"), null, null));
        Assert.Contains("text to type", noText.Message, StringComparison.Ordinal);
        var unknown = await Assert.ThrowsAsync<DoctorInputException>(() => session.ActAsync("drag", RefOf(snap, "#SearchBox"), null, null));
        Assert.Contains("click, type, select", unknown.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<DoctorInputException>(() => session.ActAsync("click", null, null, null));
        Assert.Empty(app.Performed);
    }

    // ------------------------------------------------------------------ wait

    [Fact]
    public async Task Waiting_returns_as_soon_as_the_element_appears()
    {
        var (session, app, _) = Open();
        var reads = 0;
        app.OnRead = () =>
        {
            if (++reads == 4) app.Windows.Add(new Node("Window", "SettingsWindow", "Settings") { Children = [Button("SettingsOkButton", "OK")] });
        };

        var result = await session.WaitForAsync(new LocatorQuery(AutomationId: "SettingsOkButton"));
        Assert.True(result.Met);
        Assert.Equal(0.8, result.Seconds); // found on the 4th look, after three 0.25 s pauses (0.75 s, rounded)
        Assert.StartsWith("e", result.Element, StringComparison.Ordinal);
        Assert.Contains("(in \"Settings\" #SettingsWindow)", result.Element, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Waiting_for_a_window_to_close_and_for_an_element_to_be_enabled()
    {
        var (session, app, _) = Open();
        var settings = new Node("Window", "SettingsWindow", "Settings");
        app.Windows.Add(settings);
        var reads = 0;
        app.OnRead = () =>
        {
            if (++reads == 3) app.Windows.Remove(settings);
        };
        var gone = await session.WaitForAsync(new LocatorQuery(Window: "SettingsWindow"), "disappears");
        Assert.True(gone.Met);

        var search = (Node)app.Windows[0].Children.First(c => c.AutomationId == "SearchButton");
        search.IsEnabled = false;
        reads = 0;
        app.OnRead = () =>
        {
            if (++reads == 2) search.IsEnabled = true;
        };
        var enabled = await session.WaitForAsync(new LocatorQuery(AutomationId: "SearchButton"), "enabled");
        Assert.True(enabled.Met);
        Assert.Contains("is enabled", enabled.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Waiting_too_long_is_an_answer_not_an_error()
    {
        var (session, _, _) = Open();
        var result = await session.WaitForAsync(new LocatorQuery(AutomationId: "Searchbutton"), timeoutSeconds: 2);
        Assert.False(result.Met);
        Assert.Contains("Gave up after 2 s", result.Summary, StringComparison.Ordinal);
        Assert.Contains(result.CloseMatches, l => l.Contains("#SearchButton", StringComparison.Ordinal));

        await Assert.ThrowsAsync<DoctorInputException>(() => session.WaitForAsync(new LocatorQuery(AutomationId: "X"), "vanishes"));
        await Assert.ThrowsAsync<DoctorInputException>(() => session.WaitForAsync(new LocatorQuery()));
    }

    // ------------------------------------------------------------------ screenshot and score

    [Fact]
    public void Screenshots_can_be_saved_to_a_folder()
    {
        var (session, _, _) = Open();
        var dir = Directory.CreateTempSubdirectory("wld-shot-").FullName;
        try
        {
            var shot = session.Screenshot(saveTo: dir);
            Assert.Equal(4, shot.Png.Length);
            Assert.StartsWith(dir, shot.SavedTo, StringComparison.Ordinal);
            Assert.EndsWith(".png", shot.SavedTo, StringComparison.Ordinal);
            Assert.True(File.Exists(shot.SavedTo));
            Assert.Contains("window \"Orders\" #OrdersWindow", shot.Summary, StringComparison.Ordinal);

            var element = session.Screenshot(RefOf(session.Snapshot(), "#SaveButton"));
            Assert.Contains("Button \"Save\" #SaveButton", element.Summary, StringComparison.Ordinal);
            Assert.Null(element.SavedTo);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Scoring_the_running_app_uses_the_same_rules_as_the_live_scan()
    {
        var (session, _, _) = Open();
        var report = session.Score();

        Assert.Equal("PracticeApp", report.Target);
        Assert.Equal("OrdersWindow", Assert.Single(report.Screens).Name);
        Assert.NotNull(report.Score);
        Assert.Contains(report.TopProblems, p => p.Rule == "WT004"); // ActionButton twice
        Assert.Contains(report.Notes, n => n.Contains("Only windows that were open", StringComparison.Ordinal));
    }
}

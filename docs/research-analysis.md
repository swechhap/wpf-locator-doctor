# WPF Locator Helper: Research and Analysis (before building anything)

_Written 17 November 2026. Research only. Nothing has been built yet._

---

## 1. The short answer

1. **Your idea is sound, and the timing is good, but you are not the first.** In the last year several free, open-source "MCP servers" for Windows desktop apps have appeared (an MCP server is a small program that gives an AI assistant like Claude tools to use). Two are aimed specifically at WPF (`wpf-buddy-mcp` and `WpfPilot`). One open-source library (`AutomationSandbox`) focuses only on repairing broken locators. All of them are very young: a handful of commits, few users, and no proof yet that they work on real, large company apps.
2. **No existing tool joins the whole chain together.** Nobody yet goes all the way from "this locator broke" → "here is the right element now" → "here is the corrected test code" → "here is a pull request (PR, a proposed code change for a teammate to review)" → "and here is the change to the app itself so this never breaks again." Each piece exists somewhere, but not together and not for WPF.
3. **Nobody gives a trustworthy "testability score" for a WPF app.** Microsoft's free accessibility checker (Accessibility Insights / Axe.Windows) checks things like "does this button have a name", but it has **no rule about Automation IDs at all**. I found no tool that reads your app's XAML files (the files that describe each WPF screen) and reports which controls are missing IDs. This is your clearest gap and the easiest one to win.
4. **My recommendation:** build **one core engine** and offer it in three ways:
   - as a **library** that test projects add, so broken locators can be repaired *during a normal test run* even without an AI;
   - as a **command-line tool** that runs in your build pipeline and prints a testability score (and can fail the build if the score drops);
   - as an **MCP server**, so an AI assistant can explore the app, find locators, explain failures, and prepare code fixes and PRs.
5. **One practical warning:** WPF and the Windows automation system only run on Windows. You are on a Mac, so you will need a Windows machine or a Windows virtual machine to build and test this. Also, these tools need a real, unlocked Windows desktop. They do not work on a locked screen or a "headless" build server without a logged-in user session.

---

## 2. A few words explained in plain English

| Word | What it means |
|---|---|
| **Locator** | The "address" a test uses to find something on screen, for example "the button whose ID is `SaveButton`". |
| **UI Automation (UIA)** | The built-in Windows system that lets other programs (screen readers, test tools) "see" what is inside an app window: its buttons, text boxes, lists, and so on. Every WPF test tool sits on top of this. |
| **Automation tree** | The family-tree style list of everything in a window, as UI Automation sees it. Window → panel → button, and so on. |
| **AutomationId** | A fixed ID a developer gives to a control specifically so tests can find it. It is the best kind of locator because it doesn't change when text or language changes. |
| **Name** | The human-readable label UI Automation reports (often the text on the button). Good for screen readers, but weak for tests because it changes with wording and language. |
| **Control type** | What kind of thing it is: button, text box, check box, list item, etc. |
| **XAML** | The text files in a WPF project that describe each screen's layout and controls. |
| **x:Name** | A name a developer gives a control in XAML so their own code can use it. WPF also reuses it as the AutomationId if no AutomationId was set. |
| **FlaUI** | The most popular free .NET library for driving Windows apps from tests. **FlaUInspect** is its "point and see" inspect tool. |
| **MCP** | Model Context Protocol. A standard way to give an AI assistant a set of tools. An "MCP server" is the program that provides the tools. |
| **Self-healing** | When a test can't find an element with its old locator, the tool works out which element was meant, uses it, and records the fix. |

---

## 3. How WPF actually shows its controls to test tools

Knowing this explains why locators are often missing or unreliable.

### 3.1 Where the AutomationId comes from

When a test tool asks a WPF control for its AutomationId, WPF checks three places **in this order** and uses the first one that has a value. I confirmed this in WPF's own source code:

1. `AutomationProperties.AutomationId`, the value set on purpose for testing. **Best.**
2. `x:Uid`, an ID used for translating the app into other languages. These are often **generated automatically by a build tool** with names like `Button_12`. If a screen changes, the numbers shift, so these are **unstable**.
3. `x:Name`, the developer's own name for the control. Often decent, but developers rename these freely because they don't know a test depends on them.

**What this means for you:** a good scoring tool must not just ask "is there an AutomationId?" It must also ask **where it came from**, because a "deliberate" ID is far more trustworthy than a borrowed or generated one. Inspect tools never tell you this.

### 3.2 Common reasons controls are missing or unreliable in WPF

1. **Custom controls that never introduce themselves.** If a developer builds their own control and doesn't write a small piece of code called an "automation peer" (the bit that tells UI Automation "I exist, I'm a button, here's my ID"), the control is **invisible** to test tools. Any AutomationId set on it goes nowhere. This is very common in company apps and in some third-party control libraries.
2. **Lists and grids that only create visible rows.** To stay fast, WPF lists and data grids often only create the rows you can currently see (this is called "virtualization"). Rows further down simply don't exist yet, so a tool can't find them until you scroll.
3. **The same ID repeated in every row.** If a template (a reusable design for list rows) contains a button with `x:Name="DeleteButton"`, **every row** gets a button with the ID `DeleteButton`. The ID exists but is not unique, so the test must also find the right row first.
4. **Controls that live inside templates.** Many built-in controls are made of smaller parts. Some of those parts are hidden from the automation tree on purpose, and some are not, which confuses people.
5. **Pop-ups, menus, and drop-downs** appear as separate windows in the automation tree, not inside the screen they belong to. People search the wrong place.
6. **Text that changes with language.** If tests use the Name (the visible text), they break the moment the app is translated or someone rewords a label.
7. **Things drawn as pictures.** Charts, canvases, and some fancy controls are just drawings. There is nothing inside them for a test tool to find.

---

## 4. Why inspect tools like FlaUInspect feel unstable

Your experience is common. The main reasons:

- **Each question is a call to another program.** Every time the inspect tool asks "what are your children?" it has to talk to your running app. A big screen with thousands of elements means thousands of calls, so it becomes slow, or seems to freeze.
- **If your app is busy, the inspect tool waits.** If the app's main thread is loading data, the inspector hangs until the app responds.
- **Two different "engines".** Windows has an older and a newer version of UI Automation (FlaUI calls them UIA2 and UIA3). They sometimes give different answers for the same WPF control. FlaUInspect makes you choose at start-up, and most people don't know which to pick.
- **Hover-to-find picks the wrong layer.** Pointing the mouse at a control often selects a small text piece inside the button instead of the button itself.
- **Permissions.** If the app runs "as administrator", a normal inspect tool can't see inside it.
- **It shows facts, not advice.** The tool shows you dozens of properties but never says "use this one, it's unique and stable" or "this ID appears 40 times, don't use it alone". The judgement is left to the tester. **This is the real pain**, more than the instability.

---

## 5. What exists today

### 5.1 Inspect tools (to look at an app)

| Tool | What it is | Limits |
|---|---|---|
| **Inspect.exe** | Microsoft's basic inspector, part of the Windows SDK. | Old-fashioned, no advice, no export of good locators. |
| **Accessibility Insights for Windows** | Microsoft's free accessibility checker. Built on the **Axe.Windows** library. | Checks names, control types, and behaviours for accessibility. **Has no rule about AutomationIds.** Not designed for testers. |
| **FlaUInspect** | Free inspector built on FlaUI. A version 3.0 appeared in 2026. | Slow on big screens, freezes, no recommendations (see section 4). |
| **Snoop** | A free WPF-only tool that looks *inside* the app's own structure (the visual tree), not just what UI Automation sees. | Great for developers. It shows WPF's internal view, which is not what tests use, so it can mislead testers. |

### 5.2 Ways to write and run WPF tests

| Tool | Status |
|---|---|
| **FlaUI** (C#) | The main free choice for WPF. Active. |
| **WinAppDriver** (Microsoft) | Effectively **abandoned**. Appium's own documentation says Microsoft hasn't maintained it for years. |
| **Appium + NovaWindows driver** | The current replacement for WinAppDriver if you want Appium/Selenium-style tests (Java, Python, C#, etc.). Supports WPF. |
| **Robot Framework + FlaUI library** | Used by teams who prefer keyword-style tests. |
| **Commercial**: Ranorex, TestComplete, Tricentis Tosca, Katalon, UFT | Paid, all support WPF. Some advertise "self-healing", but I found no independent evidence of how well this works for WPF specifically. Ranorex is known for its locator repository and fallback locators. |

### 5.3 MCP servers for Windows desktop apps (the AI side)

| Project | What it does | Maturity |
|---|---|---|
| **shanselman/FlaUI-MCP** | General Windows apps. 12 tools: launch, snapshot the tree, click, type, screenshot. Elements are referred to by temporary reference numbers that only last for that session. | Most popular (about 115 stars). No locator advice, no healing, no test generation. |
| **CursorTouch/Windows-MCP** | General "AI controls your whole computer" tool written in Python. | Popular, but aimed at general computer control, not testing. |
| **lysiuchenko/wpf-buddy-mcp** | WPF-specific, **202 tools**: snapshots, actions, waits, assertions, selector building/ranking/**healing**/"brittle selector" detection, test generation, accessibility audits, and "testability scores". Optional add-on inside the app to see data bindings. | **Your closest overlap.** It has only one commit and about 34 stars. It's a very new, unproven project, and 202 tools is far too many for an AI to choose between reliably. It doesn't explain how its score works, and it doesn't fix test code or open PRs. |
| **skuzadev/wpfpilot-mcp** | WPF-specific: query, act, wait, assert, record and generate xUnit + FlaUI tests, optional in-app add-on. | Very early (2 stars). No scoring or healing. |
| **clif, Winapp-MCP, desktop-pilot-mcp** | Other general Windows automation MCP servers. | Early. Focus on driving the app, not on locator quality. |

### 5.4 Self-healing for desktop apps

| Project | How it works | Notes |
|---|---|---|
| **Healenium** | The best-known free self-healing library. | **Web (Selenium) only.** No desktop support. |
| **Power Automate for desktop: Self-healing (preview)** | When an element isn't found, it uses AI (it uses Claude Sonnet and GPT models) plus a screenshot to guess the right element and continue. | For Microsoft's automation flows, not test code. Microsoft itself warns it "might occasionally identify an unintended UI element". The fix is used at run time. It doesn't produce a code change for review. |
| **UiPath Healing Agent** | Similar idea for UiPath robots. | Paid. Robots, not test code. |
| **AutomationSandbox (open source, FlaUI-based)** | Captures the tree, then repairs locators by scoring candidates on control type, parent, position among siblings, name, location, and row. It only accepts a repair when confidence is high **and** the best match is clearly ahead of the second-best. AI is optional and needs two AI providers to agree. By default it only **suggests** and changes nothing. | **The most careful design I found.** But it is a beta (0.2.0-beta.9, November 2026) with no users yet and no PR or code-fix step. Worth studying closely. |

### 5.5 Checking source files (XAML) for missing IDs

**I found nothing.** I found no XAML checker, no Visual Studio warning, and no build-time rule that says "this button has no AutomationId". This is a wide-open space.

---

## 6. The gaps (where your project can stand out)

1. **No end-to-end "broken locator → corrected test code → PR" flow.** Existing tools stop at "here's a better locator" or "I continued the run".
2. **No fix on the app side.** The real long-term fix for a missing or weak locator is to add an `AutomationProperties.AutomationId` to the app's XAML. No tool proposes that change to the developers.
3. **No transparent, explainable testability score.** wpf-buddy-mcp mentions "testability scores" with no detail. Axe.Windows ignores AutomationIds. No one scores **where an ID came from**, whether it's **stable across launches**, or whether it **survives a language change**.
4. **No checking of source files.** Nobody reads the XAML to give coverage for *every* screen, including ones a tester never opened.
5. **No healing during plain test runs in the build pipeline.** AI-based healing needs an AI in the loop. Teams need a rule-based repair that runs inside FlaUI tests on the build server, then hands a report to the AI or a human afterwards.
6. **Recommendations, not just facts.** Testers want "use this locator, here's why, and here's how confident I am". Today's inspect tools show raw properties.
7. **Trust and safety.** A repair that clicks the wrong button can make a broken feature look like it passed. Existing tools say little about protecting against this. A careful design (review mode, confidence limits, clear reports) is a selling point.
8. **Proof on real apps.** None of the open-source projects show results on large, real company WPF apps with third-party controls (Telerik, DevExpress, Infragistics, Syncfusion). Being the tool that is **tested against these** would set you apart.

---

## 7. Goal 1: The locator, self-healing, and fix MCP. How it could work

### 7.1 The overall shape: one engine with three entry points

```
                 ┌───────────────────────────────┐
                 │          CORE ENGINE          │
                 │ - reads the live app (FlaUI)  │
                 │ - reads XAML source files     │
                 │ - ranks locators              │
                 │ - matches "lost" elements     │
                 │ - computes testability score  │
                 └───────┬──────────┬──────────┬─┘
                         │          │          │
          ┌──────────────┘          │          └───────────────┐
          ▼                         ▼                          ▼
  Library in test code     Command-line tool            MCP server
  (repairs locators         (testability score in        (AI assistant explores,
   during test runs,         the build pipeline,          finds locators,
   writes a repair report)   can fail the build)          prepares code fixes & PRs)
```

Why not "only an MCP"? An MCP server only works when an AI assistant is talking to it. Your test runs in the build pipeline usually have no AI. If healing lives *only* in the MCP, it won't help the nightly test run. Putting the logic in one shared engine means all three entry points behave the same.

### 7.2 Finding locators: "recommend, don't just show"

For any element the tester points at (or the AI asks about), the tool should return a **ranked list of locators with a reason and a confidence for each**, for example:

1. `AutomationId = "SaveButton"`: deliberate ID, unique on this window, seen the same in 3 launches. **Excellent.**
2. `Name = "Save" + ControlType = Button`: unique now, but will break if the label is reworded or translated. **Fair.**
3. Path by position (`Window/Pane[2]/Button[5]`): breaks if anything is added above it. **Poor, last resort.**

The preference order should be:

1. A deliberate AutomationId that's unique.
2. A deliberate AutomationId plus the row or parent it lives in (for repeated IDs in lists).
3. An x:Name-based ID (flagged as "borrowed").
4. Name + control type (flagged as "language-sensitive").
5. Position path (flagged as "fragile").

It should also generate the locator **in the format the team uses**: FlaUI C#, Appium/NovaWindows, or Robot Framework.

### 7.3 Self-healing: a careful design

**Step 1: Remember what each element looked like while tests were passing.** Each time a test finds an element successfully, save a small "fingerprint" of it: its AutomationId, Name, control type, class, its parent chain, its position among siblings, nearby label text, its size and position on screen, and what actions it supports (click, type, select). This gives the healer something to compare against later. Without it, healing is just guessing.

**Step 2: When a locator fails, look for the best match.** Score every likely candidate on the current screen against the saved fingerprint. Same control type and same parent count for a lot. A similar name or nearby label counts for a fair amount. Similar screen position counts for a little.

**Step 3: Only accept the match if it's clearly the right one.** Require **both** a high score **and** a big gap between the best and second-best candidates. If two buttons look equally likely, **refuse to guess** and fail with a clear explanation. This is the same careful approach AutomationSandbox uses, and it's the right one.

**Step 4: Choose a mode.**
- **Report-only (default):** the test still fails, but the report says "I think the element is now X, here is the new locator".
- **Heal-and-continue:** the test continues using the new element, **but is marked "passed with repair"**, never a plain pass, so nobody is fooled.

**Step 5: Use AI only for the hard cases, and as a second opinion.** A rule-based score handles most cases quickly, cheaply, and the same way every time. The AI (through the MCP) is used when rules aren't confident. It can look at a screenshot, read the test's intent ("click Save on the order form"), and explain its choice. AI should never silently decide a test result.

**Step 6: Write a repair report** (a simple file listing: test name, old locator, new locator, confidence, evidence, screenshot). This is what the next step uses.

### 7.4 Fixing the test code and opening a PR

**The MCP server doesn't need to do the git work itself.** AI coding assistants such as Claude Code can already edit files, run tests, and open pull requests with the GitHub tools. The MCP's job is to give the assistant what it can't get on its own:

- the repair report (what broke and the suggested new locator);
- **where in the test code the locator is defined**: page-object classes, constant files, or inline strings. The tool should search the test project for the old locator text and report the exact files and lines;
- a way to **re-check** the new locator against the live app before the change is proposed.

The assistant then edits the code, re-runs the affected tests, and opens a PR with the evidence. **A human always reviews and merges**. Nothing should be merged automatically.

**The better fix, on the app side:** for each repair, the tool can also suggest the XAML change that prevents it happening again, for example: "Add `AutomationProperties.AutomationId="SaveOrderButton"` to `OrderView.xaml` line 84". If the app's source code is available, this can be a second PR for the developers.

### 7.5 Keep the tool list small

wpf-buddy-mcp offers 202 tools. AI assistants pick the wrong tool more often when given that many. Aim for roughly **10 to 15 well-designed tools**, for example:

- start or attach to the app;
- take a snapshot of the current screen (with a size limit, and only "interesting" elements by default);
- find element / recommend locators for an element;
- check a locator (does it find exactly one element, now?);
- perform an action (click, type, select, expand);
- wait for something to appear or change;
- take a screenshot;
- heal a locator (given an old locator and fingerprint);
- read a test run's repair report;
- find where a locator is used in the test code;
- score this screen / score the whole app;
- suggest XAML fixes for missing IDs.

---

## 8. Goal 2: Rating an app's testability

### 8.1 Two ways to look, and why you need both

| | **Live scan** (looks at the running app) | **Source scan** (reads XAML files) |
|---|---|---|
| Sees | Exactly what tests will see, including third-party controls and controls created in code. | Every screen in the project, even ones nobody opened. |
| Misses | Screens the scan didn't navigate to, rows not scrolled into view, pop-ups not opened. | Controls built in code, controls from third-party libraries, whether a custom control is actually visible to UI Automation. |
| Best for | The true, final score. | Fast checks on every code change (no app launch needed), and pointing to the exact file and line to fix. |

**Recommendation:** use the source scan for quick, every-change checks and exact fix locations. Use the live scan for the real score. Combine them in one report.

### 8.2 What to measure

Count only things a user can interact with: buttons, text boxes, check boxes, radio buttons, drop-downs, list rows, menu items, tabs, links, sliders, tree items, and grid cells. Plain labels shouldn't lower the score, but they're reported separately.

| Check | Plain-English question | Suggested weight |
|---|---|---|
| **ID coverage** | Of all clickable/typeable things, how many have an AutomationId? | 30% |
| **ID quality** | Was the ID set on purpose (best), borrowed from x:Name (okay), or from an auto-generated x:Uid like `Button_12` (bad)? Does it look random (long numbers, GUIDs)? | 15% |
| **Uniqueness** | Is each ID unique on its window? If it repeats (as in list rows), is the row itself identifiable? | 20% |
| **Stability** | Launch the app twice: are the IDs the same? Switch the app's language: do the IDs stay the same? | 15% |
| **Visibility** | Are there "black holes": custom controls that appear as empty "custom" elements, or areas with no children where the screen clearly shows content? | 10% |
| **Names & behaviour** | Do controls have readable names (also good for accessibility)? Do buttons support "click" and text boxes support "set value" properly? (You can reuse Microsoft's Axe.Windows rules here.) | 10% |

Then give a grade per screen and for the whole app, for example **A** (90 and up), **B** (75–89), **C** (60–74), **D** (40–59), **F** (below 40).

### 8.3 What the report should look like

For each screen, show:

- the score, the grade, and the change since the last run;
- the top problems in plain English, e.g. "12 of 40 buttons have no ID", "`DeleteButton` appears 25 times (once per row) and the rows have no ID";
- for each problem, **the exact fix**: file, line, and the line of XAML to add;
- for lists, the standard fix: give each row an ID based on its data, e.g. `AutomationProperties.AutomationId="{Binding OrderNumber}"` in the row style.

The command-line version can **fail the build** if the score drops, so testability can't quietly get worse over time.

### 8.4 Getting the live scan to cover the whole app

A live scan only sees the screen that's open. Ways to reach more screens:

1. Let the AI assistant navigate through the app (via the MCP) and scan each screen it reaches. This is good for exploration, but results vary from run to run.
2. Reuse the team's existing test runs: as tests run, quietly record and score every screen they visit. This is free coverage.
3. Let the team list the screens and how to reach them in a simple settings file.

---

## 9. Should this be an MCP at all?

**Yes, but not only an MCP.**

- MCP is the right choice for the **interactive, thinking parts**: exploring an app, explaining why a locator is weak, choosing between close matches, and preparing code changes. These need an AI.
- The **repeatable, must-be-the-same-every-time parts** (scoring, rule-based healing, build pipeline checks) should be a normal library and a command-line tool. They must run without an AI, give the same answer every time, and cost nothing per run.

All three share one engine, so you only write the logic once.

---

## 10. Suggested technology (for when you build)

- **Language:** C# on .NET 8 or later. It's the natural fit for WPF, FlaUI, and the official C# MCP SDK (the `ModelContextProtocol` NuGet package, co-maintained by Microsoft and Anthropic).
- **Reading the live app:** FlaUI, using **UIA3 by default with UIA2 as a fallback** (some WPF controls behave better with UIA2). Use FlaUI's "caching" feature to read a whole tree in one go instead of thousands of small requests. This is the main fix for the slowness described in section 4.
- **Reading XAML files:** standard XML reading is enough for XAML files. It doesn't need a full WPF parser.
- **Accessibility rules:** reuse Microsoft's open-source **Axe.Windows** library rather than rewriting those checks.
- **A test app:** build a small WPF "practice app" deliberately full of problems (custom control without a peer, list with repeated IDs, auto-generated x:Uids, controls with no IDs, a pop-up, a long virtualized list). Use it to test every feature and to show demos.
- **Development setup:** you need **Windows** (a Windows PC, a Windows virtual machine on your Mac, or a cloud Windows machine). The MCP server can run on Windows while your AI assistant runs elsewhere, as long as there's a network connection between them.

---

## 11. Hard problems and risks to plan for

1. **False repairs.** The biggest risk. A healer that clicks the wrong button can hide a real bug. Safeguards: report-only by default, a clear "passed with repair" status, confidence limits, and a human always reviews.
2. **Speed on big screens.** Data grids with thousands of cells can make tree reading very slow. Read in bulk, limit depth, and skip inside large grids unless asked.
3. **Lists that only build visible rows.** The tool must scroll or use the list's own "find item" feature to reach hidden rows.
4. **Third-party control libraries** (Telerik, DevExpress, etc.) each behave a bit differently in UI Automation. They need testing one by one.
5. **Build servers.** Tests and live scans need a logged-in, unlocked Windows desktop. Many build servers don't have one by default. Document how to set this up.
6. **Apps running as administrator** need the tool to run as administrator too.
7. **Security.** An AI that can click anything in an app could do real damage on a production system. Add a "dry run" mode, a list of allowed apps, and never run against production data.
8. **Privacy.** Screenshots and screen text may include customer data. Hide sensitive fields before anything is sent to an AI.
9. **Competition moves fast.** wpf-buddy-mcp and AutomationSandbox appeared very recently. Focus on the gaps in section 6 rather than copying everything they do.

---

## 12. Build from scratch, or build on what exists?

- **Build from scratch, but borrow ideas and libraries freely.** All the projects I found are MIT-licensed (free to reuse with credit), but they are young, each takes a different direction, and none has the score-plus-healing-plus-PR focus you want. Depending on one of them now would tie you to someone else's early design choices.
- **Do use** FlaUI (stable, widely used) and Axe.Windows (Microsoft-maintained) as building blocks.
- **Study before building:** AutomationSandbox's careful healing approach, wpf-buddy-mcp's tool list (to see what *not* to overdo), and FlaUI-MCP's simple "snapshot with reference numbers" design.

---

## 13. A sensible order to build in (for later)

1. **Practice WPF app full of locator problems**, plus a Windows dev setup.
2. **Source scanner + live scanner + testability score** (command-line tool and report). This is your clearest gap and is useful on day one, even without AI.
3. **MCP server with a small toolset**: snapshot, recommend locators, check a locator, act, wait, score.
4. **Fingerprints + rule-based healing** in a library for FlaUI tests, report-only first.
5. **AI-assisted healing and code fixes**: find where the locator lives in the test code, propose the edit, re-run, and open a PR for human review.
6. **App-side fix suggestions**: XAML changes to add the missing IDs, as a separate PR.
7. **Support for other test styles** (Appium/NovaWindows, Robot Framework), and testing against popular third-party control libraries.

---

## 14. Questions for you before we build

1. Which test framework do you (or your target users) use today: FlaUI in C#, Appium, Robot Framework, or something else? This decides what code the tool generates and edits.
2. Will the people using this have the **app's source code** (so XAML scanning and app-side fixes are possible), or only the finished app?
3. Where do tests run: on testers' own PCs, or on build servers? This affects the "needs an unlocked desktop" problem.
4. Is there a specific WPF app (yours or a public sample) we should use as the first real-world test, besides the practice app?
5. Is this for personal or open-source use, or for a company? That affects how much weight to put on security, privacy, and admin controls.

---

## Sources

- FlaUInspect: https://gitee.com/github-rocks/FlaUInspect , https://www.nuget.org/packages/FlaUI.UIA3/
- WinAppDriver status & NovaWindows: https://github.com/appium/appium-windows-driver , https://github.com/AutomateThePlanet/appium-novawindows-driver , https://automatetheplanet.com/reviving-windows-app-automation-novawindows-driver-for-appium-2
- FlaUI-MCP: https://github.com/shanselman/FlaUI-MCP
- wpf-buddy-mcp: https://github.com/lysiuchenko/wpf-buddy-mcp
- WpfPilot MCP: https://github.com/skuzadev/wpfpilot-mcp
- Other Windows MCP servers: https://github.com/cmalpass/clif , https://github.com/epnielsen/Winapp-MCP , https://github.com/Smatjoy/desktop-pilot-mcp , https://lobehub.com/mcp/cursortouch-windows-mcp
- Axe.Windows rules (no AutomationId rule): https://github.com/microsoft/axe-windows/blob/main/docs/RulesDescription.md
- AutomationSandbox (desktop locator healing): https://libraries.io/nuget/AutomationSandbox.Discovery , https://github.com/mustafasercansak/automation-sandbox
- Power Automate desktop self-healing: https://learn.microsoft.com/en-us/power-automate/desktop-flows/self-healing
- WPF AutomationId order (AutomationId → x:Uid → x:Name): https://source.dot.net/PresentationFramework/System/Windows/Automation/Peers/FrameworkElementAutomationPeer.cs.html
- Custom controls need an automation peer: https://msdn.microsoft.com/library/cc165614.aspx , https://learn.microsoft.com/en-us/archive/blogs/patrickdanino/custom-controls-and-ui-automation
- Commercial tool comparisons (vendor-written, treat with caution): https://smartbear.com/blog/testcomplete-vs-tricentis-tosca-vs-ranorex-ai-powered-test-automation , https://www.shiplight.ai/blog/best-self-healing-test-tools

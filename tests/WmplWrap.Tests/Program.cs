using WmplWrap;
using WmplWrap.Desktop;

var first = new LibrarySnapshot(1, new DateTimeOffset(2026, 9, 20, 4, 5, 0, TimeSpan.Zero), "Eastern Standard Time",
[
    Track("one", "Existing", 10),
    Track("two", "Reset", 9)
]);
var second = new LibrarySnapshot(1, new DateTimeOffset(2026, 9, 23, 4, 5, 0, TimeSpan.Zero), "Eastern Standard Time",
[
    Track("one", "Existing", 14),
    Track("two", "Reset", 2),
    Track("three", "New without a baseline", 7)
]);

var report = Reporting.Compare(first, second);
Assert(report.Rows.Count == 2, "Only the changed established track and reset marker should appear.");
Assert(report.Rows.Single(row => row.Track.Id == "one").Listens == 4, "Cumulative-count delta should be 4.");
Assert(report.Rows.Single(row => row.Track.Id == "two").CounterWentBackwards, "A decrease should be flagged as a reset.");
Assert(report.Rows.Single(row => row.Track.Id == "two").Listens == 0, "A reset must never become negative or invented listens.");
Assert(report.NewTracksWithoutBaseline == 1, "New tracks must be excluded until their next baseline.");

var firstSeenReport = Reporting.Compare(first, second, includeFirstSeenCounts: true);
Assert(firstSeenReport.Rows.Single(row => row.Track.Id == "three") is { Listens: 7, FirstSeenListens: 7 }, "Enabled first-seen counts must include a newly discovered track's current WMP total.");

var third = new LibrarySnapshot(1, new DateTimeOffset(2026, 9, 24, 4, 5, 0, TimeSpan.Zero), "Eastern Standard Time",
[
    Track("one", "Existing", 16),
    Track("two", "Reset", 3),
    Track("three", "New without a baseline", 12)
]);
var strictIntervals = Reporting.CompareIntervals([first, second, third], first.CapturedAtUtc, includeFirstSeenCounts: false);
Assert(strictIntervals.Single(row => row.Track.Id == "three") is { Listens: 5, FirstSeenListens: 0 }, "Strict interval reports must omit an unknown initial count but retain later deltas.");
var firstSeenIntervals = Reporting.CompareIntervals([first, second, third], first.CapturedAtUtc, includeFirstSeenCounts: true);
Assert(firstSeenIntervals.Single(row => row.Track.Id == "three") is { Listens: 12, FirstSeenListens: 7 }, "First-seen interval reports must retain both the initial count and later increases.");

var graph = DashboardGraphBuilder.Build([first, second], new DashboardGraphOptions(
    DashboardGraphRange.AllTime,
    DashboardGraphMeasure.Listens,
    DashboardGraphGrouping.Total,
    DashboardGraphGranularity.Day,
    DashboardGraphMode.Activity,
    null,
    null,
    true,
    6,
    true));
Assert(graph.Summary.Contains("includes first-seen WMP counts", StringComparison.Ordinal), "Graphs must disclose when first-seen counts are included.");
Assert(graph.Series.Single().RatesPerDay[0] is null, "A baseline must not invent a rate of change.");
Assert(Math.Abs(graph.Series.Single().RatesPerDay[1]!.Value - (11d / 3d)) < 0.001, "Hover data must divide an interval's change by its actual elapsed duration.");

var navigation = new DashboardNavigationHistory(new DashboardNavigationState(DashboardPage.Overview, DataView.Tracks));
navigation.Navigate(new DashboardNavigationState(DashboardPage.Graphs, DataView.Tracks));
navigation.Navigate(new DashboardNavigationState(DashboardPage.Settings, DataView.Tracks));
navigation.Navigate(new DashboardNavigationState(DashboardPage.Data, DataView.Albums));
Assert(navigation.TryGoBack(out var backToSettings) && backToSettings.Page == DashboardPage.Settings, "Back must return to the most recently visited page, not the prior enum value.");
Assert(navigation.TryGoBack(out var backToGraphs) && backToGraphs.Page == DashboardPage.Graphs, "Back must preserve chronological history across multiple visits.");
Assert(navigation.TryGoForward(out var forwardToSettings) && forwardToSettings.Page == DashboardPage.Settings, "Forward must restore the next page in chronological history.");
navigation.Navigate(new DashboardNavigationState(DashboardPage.LatestSnapshot, DataView.Tracks));
Assert(!navigation.CanGoForward, "A new navigation after going back must clear forward history.");
var defaults = new DiscordRpcPreferences();
Assert(!defaults.Enabled, "Discord Rich Presence must default to disabled.");
Assert(defaults.ApplicationId == "1553580075688009838", "The shared Discord Application ID must be the default.");
Assert(defaults.KeepPresenceBetweenTracks, "Discord Rich Presence must keep a last track during short WMP handoffs by default.");
Assert(!defaults.KeepRunningWhenClosed, "Discord Rich Presence must not keep Wrap running after close by default.");

var playing = new WmpPlaybackSnapshot("Song", "Artist", "Album", "Album Artist", "file:///song.mp3", "03:00", 10, WmpPlaybackState.Playing);
var mapping = new DiscordAlbumArtMapping(" album artist ", " ALBUM ", "album_cover");
var elapsedStart = new DateTimeOffset(2026, 9, 28, 3, 0, 0, TimeSpan.Zero);
var mappedPresence = DiscordPresenceFormatter.Create(playing, [mapping], elapsedStart);
Assert(mappedPresence is { Details: "\u201cSong\u201d", State: "by Artist", LargeImageKey: "album_cover", GoogleSearchUrl: "https://www.google.com/search?q=Song%20Artist", UsesFallbackArtwork: false, ElapsedSinceUtc: not null }, "A matching artist and album must select the configured Discord asset, format its artist, and create a Google search link.");
var fallbackPresence = DiscordPresenceFormatter.Create(playing, []);
Assert(fallbackPresence is { LargeImageKey: "wmp_empty", UsesFallbackArtwork: true }, "Unmapped albums must use the fallback Discord asset.");
Assert(DiscordPresenceFormatter.Create(WmpPlaybackSnapshot.None, []) is null, "Stopped playback must clear Rich Presence.");
var pausedPresence = DiscordPresenceFormatter.Create(playing with { State = WmpPlaybackState.Paused, PositionSeconds = 75 }, []);
Assert(pausedPresence is { State: "by Artist \u00b7 Paused", ElapsedSinceUtc: null }, "Paused playback must clear Discord's live timer while retaining its artist context.");

var elapsedClock = new DiscordElapsedClock();
var firstObservation = elapsedClock.Observe(playing with { PositionSeconds = 75 }, elapsedStart);
Assert(firstObservation == elapsedStart - TimeSpan.FromSeconds(75), "The first elapsed timestamp must reflect WMP's current position.");
var continuedObservation = elapsedClock.Observe(playing with { PositionSeconds = 76 }, elapsedStart + TimeSpan.FromSeconds(1));
Assert(continuedObservation == firstObservation, "Normal playback must retain its original Discord elapsed timestamp.");
var seekObservation = elapsedClock.Observe(playing with { PositionSeconds = 130 }, elapsedStart + TimeSpan.FromSeconds(2));
Assert(seekObservation == elapsedStart + TimeSpan.FromSeconds(2) - TimeSpan.FromSeconds(130), "Seeking must rebase the Discord elapsed timestamp.");
Assert(elapsedClock.Observe(playing with { State = WmpPlaybackState.Paused }, elapsedStart) is null, "Paused playback must not keep an advancing elapsed timestamp.");

var handoffBuffer = new DiscordPresenceHandoffBuffer();
var noPlayback = WmpPlaybackSnapshot.None;
Assert(handoffBuffer.ShouldKeepLastPresence(noPlayback, true, elapsedStart), "The first brief WMP handoff gap must keep the last presence.");
Assert(handoffBuffer.ShouldKeepLastPresence(noPlayback, true, elapsedStart + TimeSpan.FromSeconds(4)), "A handoff gap shorter than the grace period must keep the last presence.");
Assert(!handoffBuffer.ShouldKeepLastPresence(noPlayback, true, elapsedStart + DiscordPresenceHandoffBuffer.GracePeriod), "The last presence must clear once the handoff grace period expires.");
Assert(!handoffBuffer.ShouldKeepLastPresence(noPlayback, false, elapsedStart), "The immediate-clear setting must bypass the handoff buffer.");

var tracker = new WmpPlaybackStateTracker();
tracker.Observe(playing, true);
tracker.Observe(playing, true);
tracker.Observe(playing, true);
var stalled = tracker.Observe(playing, true);
Assert(stalled.State == WmpPlaybackState.Paused, "Three unchanged playing samples must become paused.");
var explicitPause = tracker.Observe(playing with { State = WmpPlaybackState.Paused }, true);
Assert(explicitPause.State == WmpPlaybackState.Paused, "An explicit WMP pause must remain paused.");

var settingsDirectory = Path.Combine(Path.GetTempPath(), "WMPL-Wrap-Tests", Guid.NewGuid().ToString("N"));
try
{
    var settingsStore = new DashboardSettingsStore(settingsDirectory);
    var savedPreferences = new DashboardPreferences(false, false, new DiscordRpcPreferences(true, "1553580075688009838", false, [mapping], false, true), DesktopTheme.Dark, 312);
    settingsStore.Save(savedPreferences);
    var loadedPreferences = settingsStore.Load();
    Assert(loadedPreferences.DiscordRpc is { Enabled: true, DetectStalledPlayback: false, KeepPresenceBetweenTracks: false, KeepRunningWhenClosed: true }, "Discord preferences must persist with existing desktop settings.");
    Assert(loadedPreferences.DiscordRpc!.Mappings.Single().AssetKey == "album_cover", "Album-art mappings must persist locally.");
    Assert(loadedPreferences is { DesktopTheme: DesktopTheme.Dark, AccentHue: 312 }, "Appearance preferences must persist with existing desktop settings.");

    File.WriteAllText(Path.Combine(settingsDirectory, "desktop-settings.json"), "{\"IncludeBaselineSnapshot\":false,\"OpenInWmpOnDoubleClick\":false}");
    var migratedPreferences = settingsStore.Load();
    Assert(migratedPreferences is { DiscordRpc: null, DesktopTheme: DesktopTheme.Light, AccentHue: ThemeManager.DefaultAccentHue }, "Older settings files must remain readable and use the default appearance.");

    File.WriteAllText(Path.Combine(settingsDirectory, "desktop-settings.json"), "{\"DiscordRpc\":{\"Enabled\":true,\"ApplicationId\":\"1553580075688009838\",\"DetectStalledPlayback\":true,\"AlbumArtMappings\":[]}}");
    var upgradedDiscordPreferences = settingsStore.Load();
    Assert(upgradedDiscordPreferences.DiscordRpc is { KeepPresenceBetweenTracks: true, KeepRunningWhenClosed: false }, "Existing Discord settings must preserve handoff behavior and default background mode to off.");
}
finally
{
    if (Directory.Exists(settingsDirectory)) Directory.Delete(settingsDirectory, true);
}

Console.WriteLine("All reporting tests passed.");

static TrackSnapshot Track(string id, string title, long count) => new(id, $"file:///{id}.mp3", title, "Artist", "Album", "03:00", count);
static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

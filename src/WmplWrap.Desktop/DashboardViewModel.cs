using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace WmplWrap.Desktop;

public sealed class DashboardViewModel : INotifyPropertyChanged, IDisposable
{
    private const string ScheduledTaskName = "WMPL Wrap Daily Snapshot";
    private const string DefaultRepositoryUrl = "https://github.com/zaynedoc/WMPL-Wrap";
    private static string _sessionPeriod = "All time";
    private readonly SnapshotStore _store;
    private readonly DashboardSettingsStore _settingsStore;
    private readonly DiscordPresenceService _discordPresence;
    private readonly DashboardNavigationHistory _navigationHistory = new(new DashboardNavigationState(DashboardPage.Overview, DataView.Tracks));
    private IReadOnlyList<LibrarySnapshot> _snapshots = [];
    private DashboardPage _page = DashboardPage.Overview;
    private DataView _dataView = DataView.Tracks;
    private DashboardSnapshotOption? _selectedSnapshot;
    private bool _isCapturing;
    private bool _includeBaselineSnapshot = true;
    private bool _openInWmpOnDoubleClick = true;
    private bool _isCheckingForUpdates;
    private bool _discordRpcEnabled;
    private bool _detectStalledPlayback = true;
    private bool _keepDiscordPresenceBetweenTracks = true;
    private bool _keepDiscordPresenceRunningWhenClosed;
    private DesktopTheme _desktopTheme = DesktopTheme.Light;
    private int _accentHue = ThemeManager.DefaultAccentHue;
    private AutomaticSnapshotTaskState _automaticSnapshotTaskState = AutomaticSnapshotTaskState.Unavailable;
    private string _discordApplicationId = DiscordRpcPreferences.DefaultApplicationId;
    private string _discordStatus = "Discord Rich Presence is disabled";
    private string _discordNowPlaying = "No status is being shared";
    private string _discordArtworkStatus = "Fallback asset: wmp_empty";
    private string _discordLastError = "";
    private string _discordLastUpdated = "";
    private DiscordRpcConnectionState _discordRpcConnectionState = DiscordRpcConnectionState.Inactive;
    private DiscordAlbumArtMapping? _selectedAlbumArtMapping;
    private string _albumArtArtist = "";
    private string _albumArtTitle = "";
    private string _albumArtAssetKey = "";
    private ImageSource? _albumArtPreview;
    private string _albumArtPreviewStatus = "Select a mapping or use the current WMP track";
    private DashboardGraphRange _graphRange = DashboardGraphRange.PastMonth;
    private DashboardGraphMeasure _graphMeasure = DashboardGraphMeasure.Listens;
    private DashboardGraphGrouping _graphGrouping = DashboardGraphGrouping.Total;
    private DashboardGraphGranularity _graphGranularity = DashboardGraphGranularity.Auto;
    private DashboardGraphMode _graphMode = DashboardGraphMode.Activity;
    private int _graphTopSeriesLimit = 5;
    private bool _combineRemainingGraphSeries = true;
    private DateTime? _graphStartDate;
    private DateTime? _graphEndDate;
    private ISeries[] _graphSeries = [];
    private Axis[] _graphXAxes = [];
    private Axis[] _graphYAxes = [];
    private string _snapshotSummary = "Loading local snapshot history";
    private string _latestSnapshotCaption = "";
    private string _latestSnapshotSummary = "";
    private string _latestSnapshotEmptyMessage = "";
    private string _snapshotSummaryLabel = "RECORDED INTERVAL";
    private string _snapshotListenColumnHeader = "NEW LISTENS";
    private string _topCaption = "";
    private string _topEmptyMessage = "";
    private string _totalListens = "-";
    private string _totalListeningTime = "-";
    private string _metricPeriodCaption = "All time";
    private string _metricTopArtist = "-";
    private string _metricTopArtistCount = "";
    private string _schedulerState = "Checking automatic snapshots";
    private string _updateStatus = "Check GitHub for a newer signed release";
    private string _graphSummary = "Loading local listening activity";
    private string _graphEmptyMessage = "";

    public DashboardViewModel()
    {
        DataDirectory = ResolveDataDirectory();
        _store = new SnapshotStore(DataDirectory);
        _settingsStore = new DashboardSettingsStore(DataDirectory);
        var preferences = _settingsStore.Load();
        _includeBaselineSnapshot = preferences.IncludeBaselineSnapshot;
        _openInWmpOnDoubleClick = preferences.OpenInWmpOnDoubleClick;
        _desktopTheme = preferences.DesktopTheme;
        _accentHue = ThemeManager.NormalizeHue(preferences.AccentHue);
        ThemeManager.Apply(_desktopTheme, _accentHue);
        var discord = preferences.DiscordRpc ?? new DiscordRpcPreferences();
        _discordRpcEnabled = discord.Enabled;
        _detectStalledPlayback = discord.DetectStalledPlayback;
        _keepDiscordPresenceBetweenTracks = discord.KeepPresenceBetweenTracks;
        _keepDiscordPresenceRunningWhenClosed = discord.KeepRunningWhenClosed;
        _discordApplicationId = string.IsNullOrWhiteSpace(discord.ApplicationId) ? DiscordRpcPreferences.DefaultApplicationId : discord.ApplicationId;
        foreach (var mapping in discord.Mappings) AlbumArtMappings.Add(mapping);
        _albumArtPreview = AlbumArtResolver.DiscordFallback();
        _discordPresence = new DiscordPresenceService();
        _discordPresence.StatusChanged += OnDiscordStatusChanged;
        RefreshCommand = new RelayCommand(_ => Refresh());
        CaptureCommand = new AsyncRelayCommand(CaptureAsync, () => !_isCapturing);
        NavigateCommand = new RelayCommand(parameter => Navigate(parameter?.ToString()));
        BackCommand = new RelayCommand(_ => MoveBack());
        ForwardCommand = new RelayCommand(_ => MoveForward());
        OpenSchedulerCommand = new RelayCommand(_ => OpenScheduler());
        ToggleSchedulerCommand = new AsyncRelayCommand(ToggleSchedulerAsync, () => true);
        ShowSnapshotStatusCommand = new RelayCommand(_ => ShowSnapshotStatus());
        OpenGitHubCommand = new RelayCommand(_ => OpenGitHub());
        OpenReleasesCommand = new RelayCommand(_ => OpenReleases());
        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync, () => !_isCheckingForUpdates);
        SaveDiscordSettingsCommand = new RelayCommand(_ => SaveDiscordSettings());
        RefreshDiscordCommand = new AsyncRelayCommand(() => _discordPresence.RefreshAsync(), () => _discordRpcEnabled);
        ReconnectDiscordCommand = new RelayCommand(_ => ReconnectDiscord());
        OpenDiscordDeveloperPortalCommand = new RelayCommand(_ => OpenDiscordDeveloperPortal());
        OpenAlbumArtCommand = new RelayCommand(_ => Navigate("AlbumArt"));
        OpenDiscordSettingsCommand = new RelayCommand(_ => OpenDiscordSettings());
        SaveAlbumArtMappingCommand = new RelayCommand(_ => SaveAlbumArtMapping());
        DeleteAlbumArtMappingCommand = new RelayCommand(_ => DeleteAlbumArtMapping());
        UseCurrentTrackForAlbumArtCommand = new RelayCommand(_ => UseCurrentTrackForAlbumArt());
        ResetAppearanceCommand = new RelayCommand(_ => ResetAppearance());
        Refresh();
        RefreshSchedulerState();
        if (_discordRpcEnabled) StartDiscordPresence();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? DiscordSettingsRequested;
    public event Action? AppearanceChanged;
    public ObservableCollection<DashboardSnapshotChange> LatestSnapshotTracks { get; } = [];
    public ObservableCollection<DashboardSnapshotOption> SnapshotOptions { get; } = [];
    public ObservableCollection<DashboardSong> TopTracks { get; } = [];
    public ObservableCollection<DashboardAggregate> TopAlbums { get; } = [];
    public ObservableCollection<DashboardAggregate> TopArtists { get; } = [];
    public ObservableCollection<DashboardDataRow> DataRows { get; } = [];
    public ObservableCollection<DashboardGraphLegendItem> GraphLegendItems { get; } = [];
    public ObservableCollection<DiscordAlbumArtMapping> AlbumArtMappings { get; } = [];
    public IReadOnlyList<string> PeriodOptions { get; } = ["All time", "Past week", "Past month", "Past year"];
    public IReadOnlyList<string> ThemeOptions { get; } = ["Light", "Dark"];
    public IReadOnlyList<string> GraphRangeOptions { get; } = ["Past week", "Past month", "Past year", "All time", "Custom range"];
    public IReadOnlyList<string> GraphMeasureOptions { get; } = ["Listens", "Listening time", "Tracks listened"];
    public IReadOnlyList<string> GraphGroupingOptions { get; } = ["Total", "Artist", "Album", "Track"];
    public IReadOnlyList<string> GraphGranularityOptions { get; } = ["Auto", "Day", "Week", "Month"];
    public IReadOnlyList<string> GraphModeOptions { get; } = ["Activity", "Cumulative", "Breakdown"];
    public IReadOnlyList<string> GraphTopSeriesOptions { get; } = ["Top 5", "Top 10", "Top 20", "All series"];
    public ICommand RefreshCommand { get; }
    public ICommand CaptureCommand { get; }
    public ICommand NavigateCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand ForwardCommand { get; }
    public ICommand OpenSchedulerCommand { get; }
    public ICommand ToggleSchedulerCommand { get; }
    public ICommand ShowSnapshotStatusCommand { get; }
    public ICommand OpenGitHubCommand { get; }
    public ICommand OpenReleasesCommand { get; }
    public ICommand CheckForUpdatesCommand { get; }
    public ICommand SaveDiscordSettingsCommand { get; }
    public ICommand RefreshDiscordCommand { get; }
    public ICommand ReconnectDiscordCommand { get; }
    public ICommand OpenDiscordDeveloperPortalCommand { get; }
    public ICommand OpenAlbumArtCommand { get; }
    public ICommand OpenDiscordSettingsCommand { get; }
    public ICommand SaveAlbumArtMappingCommand { get; }
    public ICommand DeleteAlbumArtMappingCommand { get; }
    public ICommand UseCurrentTrackForAlbumArtCommand { get; }
    public ICommand ResetAppearanceCommand { get; } = null!;
    public string DataDirectory { get; }
    public Brush AccentPreview => ThemeManager.AccentBrush;
    public string AccentHueLabel => $"{AccentHueName(_accentHue)} ({_accentHue}°)";
    public bool IsDarkTheme => _desktopTheme == DesktopTheme.Dark;
    public string DataLocationLabel => $"";
    public string SnapshotSummary { get => _snapshotSummary; private set => Set(ref _snapshotSummary, value); }
    public string LatestSnapshotCaption { get => _latestSnapshotCaption; private set => Set(ref _latestSnapshotCaption, value); }
    public string LatestSnapshotSummary { get => _latestSnapshotSummary; private set => Set(ref _latestSnapshotSummary, value); }
    public string LatestSnapshotEmptyMessage { get => _latestSnapshotEmptyMessage; private set => Set(ref _latestSnapshotEmptyMessage, value); }
    public string SnapshotSummaryLabel { get => _snapshotSummaryLabel; private set => Set(ref _snapshotSummaryLabel, value); }
    public string SnapshotListenColumnHeader { get => _snapshotListenColumnHeader; private set => Set(ref _snapshotListenColumnHeader, value); }
    public string TopCaption { get => _topCaption; private set => Set(ref _topCaption, value); }
    public string TopEmptyMessage { get => _topEmptyMessage; private set => Set(ref _topEmptyMessage, value); }
    public string TotalListens { get => _totalListens; private set => Set(ref _totalListens, value); }
    public string TotalListeningTime { get => _totalListeningTime; private set => Set(ref _totalListeningTime, value); }
    public string MetricPeriodCaption { get => _metricPeriodCaption; private set => Set(ref _metricPeriodCaption, value); }
    public string MetricTopArtist { get => _metricTopArtist; private set => Set(ref _metricTopArtist, value); }
    public string MetricTopArtistCount { get => _metricTopArtistCount; private set => Set(ref _metricTopArtistCount, value); }
    public string SchedulerState { get => _schedulerState; private set => Set(ref _schedulerState, value); }
    public string AutomaticSnapshotsToggleText => _automaticSnapshotTaskState switch
    {
        AutomaticSnapshotTaskState.Enabled => "Disable automatic snapshots",
        AutomaticSnapshotTaskState.Disabled => "Enable automatic snapshots",
        AutomaticSnapshotTaskState.Missing => "Set up automatic snapshots",
        _ => "Refresh automatic snapshots"
    };
    public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }
    public string GraphSummary { get => _graphSummary; private set => Set(ref _graphSummary, value); }
    public string GraphEmptyMessage { get => _graphEmptyMessage; private set => Set(ref _graphEmptyMessage, value); }
    public string DiscordStatus { get => _discordStatus; private set => Set(ref _discordStatus, value); }
    public string DiscordNowPlaying { get => _discordNowPlaying; private set => Set(ref _discordNowPlaying, value); }
    public string DiscordArtworkStatus { get => _discordArtworkStatus; private set => Set(ref _discordArtworkStatus, value); }
    public string DiscordLastError { get => _discordLastError; private set => Set(ref _discordLastError, value); }
    public string DiscordLastUpdated { get => _discordLastUpdated; private set => Set(ref _discordLastUpdated, value); }
    public string DiscordFooterStatus => $"Discord RPC Status: {_discordRpcConnectionState switch
    {
        DiscordRpcConnectionState.Connecting => "Connecting...",
        DiscordRpcConnectionState.Active => "Active",
        _ => "Inactive"
    }}";
    public string DiscordFooterToolTip => $"{DiscordStatus}\n{DiscordNowPlaying}\nClick to open Discord Rich Presence settings";
    public string GraphHelpText => "Chart points use the ending snapshot date. Hover a point for its observed interval, change, and average daily rate.";
    public ImageSource? AlbumArtPreview { get => _albumArtPreview; private set => Set(ref _albumArtPreview, value); }
    public string AlbumArtPreviewStatus { get => _albumArtPreviewStatus; private set => Set(ref _albumArtPreviewStatus, value); }
    public ISeries[] GraphSeries { get => _graphSeries; private set => Set(ref _graphSeries, value); }
    public Axis[] GraphXAxes { get => _graphXAxes; private set => Set(ref _graphXAxes, value); }
    public Axis[] GraphYAxes { get => _graphYAxes; private set => Set(ref _graphYAxes, value); }
    public string CaptureButtonText => _isCapturing ? "Reading WMP" : "Capture snapshot";
    public bool CanCapture => !_isCapturing;
    public bool CanCheckForUpdates => !_isCheckingForUpdates;
    public string UpdateButtonText => _isCheckingForUpdates ? "Checking GitHub" : "Check for updates";
    public Visibility GraphChartVisibility => GraphSeries.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GraphEmptyVisibility => GraphSeries.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GraphCustomDateVisibility => _graphRange == DashboardGraphRange.Custom ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GraphSeriesOptionsVisibility => _graphGrouping == DashboardGraphGrouping.Total ? Visibility.Collapsed : Visibility.Visible;
    public Visibility AlbumArtVisibility => _page == DashboardPage.AlbumArt ? Visibility.Visible : Visibility.Collapsed;
    public string Breadcrumbs => _page switch
    {
        DashboardPage.LatestSnapshot => "Data  >  Snapshot history",
        DashboardPage.Graphs => "Library  >  Graphs",
        DashboardPage.Data => $"Data  >  {DataTitle}",
        DashboardPage.AlbumArt => "Library  >  Settings  >  Discord Rich Presence  >  Album art",
        DashboardPage.Settings => "Library  >  Settings",
        _ => "Library  >  Listening history"
    };
    public Visibility OverviewVisibility => _page == DashboardPage.Overview ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LatestSnapshotVisibility => _page == DashboardPage.LatestSnapshot ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GraphsVisibility => _page == DashboardPage.Graphs ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DataVisibility => _page == DashboardPage.Data ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SettingsVisibility => _page == DashboardPage.Settings ? Visibility.Visible : Visibility.Collapsed;
    public bool CanManageAlbumArt => _discordRpcEnabled;
    public bool ShouldKeepRunningInBackground => _discordRpcEnabled && _keepDiscordPresenceRunningWhenClosed;
    public bool CanGoBack => _navigationHistory.CanGoBack;
    public bool CanGoForward => _navigationHistory.CanGoForward;
    public string DataTitle => _dataView switch { DataView.Albums => "Top albums", DataView.Artists => "Top artists", _ => "Top songs" };
    public string DataCaption => TopCaption;
    public string DataContextColumn => _dataView switch { DataView.Albums => "TRACKS", DataView.Artists => "TOP ALBUM", _ => "ALBUM" };
    public string AppVersion => $"v{GetType().Assembly.GetName().Version?.ToString(3) ?? "1.1.0"}";

    public bool IncludeBaselineSnapshot
    {
        get => _includeBaselineSnapshot;
        set
        {
            if (_includeBaselineSnapshot == value) return;
            _includeBaselineSnapshot = value;
            SavePreferences();
            OnPropertyChanged();
            Refresh();
        }
    }

    public bool OpenInWmpOnDoubleClick
    {
        get => _openInWmpOnDoubleClick;
        set
        {
            if (_openInWmpOnDoubleClick == value) return;
            _openInWmpOnDoubleClick = value;
            SavePreferences();
            OnPropertyChanged();
        }
    }

    public string SelectedTheme
    {
        get => _desktopTheme.ToString();
        set
        {
            var theme = string.Equals(value, "Dark", StringComparison.OrdinalIgnoreCase) ? DesktopTheme.Dark : DesktopTheme.Light;
            if (_desktopTheme == theme) return;
            _desktopTheme = theme;
            ApplyAppearance();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDarkTheme));
            OnPropertyChanged(nameof(AccentPreview));
        }
    }

    public int AccentHue
    {
        get => _accentHue;
        set
        {
            var normalized = ThemeManager.NormalizeHue(value);
            if (_accentHue == normalized) return;
            _accentHue = normalized;
            ApplyAppearance();
            OnPropertyChanged();
            OnPropertyChanged(nameof(AccentHueLabel));
            OnPropertyChanged(nameof(AccentPreview));
        }
    }

    public bool DiscordRpcEnabled
    {
        get => _discordRpcEnabled;
        set
        {
            if (_discordRpcEnabled == value) return;
            _discordRpcEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanManageAlbumArt));
            OnPropertyChanged(nameof(ShouldKeepRunningInBackground));
            SaveDiscordSettings();
        }
    }

    public bool DetectStalledPlayback
    {
        get => _detectStalledPlayback;
        set
        {
            if (_detectStalledPlayback == value) return;
            _detectStalledPlayback = value;
            OnPropertyChanged();
            SavePreferences();
            if (_discordRpcEnabled) StartDiscordPresence();
        }
    }

    public bool KeepDiscordPresenceBetweenTracks
    {
        get => _keepDiscordPresenceBetweenTracks;
        set
        {
            if (_keepDiscordPresenceBetweenTracks == value) return;
            _keepDiscordPresenceBetweenTracks = value;
            OnPropertyChanged();
            SavePreferences();
            if (_discordRpcEnabled) StartDiscordPresence();
        }
    }

    public bool KeepDiscordPresenceRunningWhenClosed
    {
        get => _keepDiscordPresenceRunningWhenClosed;
        set
        {
            if (_keepDiscordPresenceRunningWhenClosed == value) return;
            _keepDiscordPresenceRunningWhenClosed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShouldKeepRunningInBackground));
            SavePreferences();
        }
    }

    public string DiscordApplicationId
    {
        get => _discordApplicationId;
        set => Set(ref _discordApplicationId, value);
    }

    public DiscordAlbumArtMapping? SelectedAlbumArtMapping
    {
        get => _selectedAlbumArtMapping;
        set
        {
            if (_selectedAlbumArtMapping == value) return;
            _selectedAlbumArtMapping = value;
            OnPropertyChanged();
            if (value is null) return;
            AlbumArtArtist = value.AlbumArtist;
            AlbumArtTitle = value.AlbumTitle;
            AlbumArtAssetKey = value.AssetKey;
            RefreshAlbumArtPreview();
        }
    }

    public string AlbumArtArtist
    {
        get => _albumArtArtist;
        set { if (Set(ref _albumArtArtist, value)) RefreshAlbumArtPreview(); }
    }

    public string AlbumArtTitle
    {
        get => _albumArtTitle;
        set { if (Set(ref _albumArtTitle, value)) RefreshAlbumArtPreview(); }
    }

    public string AlbumArtAssetKey
    {
        get => _albumArtAssetKey;
        set => Set(ref _albumArtAssetKey, value);
    }

    public string SelectedGraphRange
    {
        get => GraphRangeLabel(_graphRange);
        set
        {
            var parsed = value == "Custom range" ? DashboardGraphRange.Custom : ParseGraphOption<DashboardGraphRange>(value);
            if (_graphRange == parsed) return;
            _graphRange = parsed;
            if (parsed == DashboardGraphRange.Custom && (_graphStartDate is null || _graphEndDate is null))
            {
                var anchor = _snapshots.Count == 0 ? DateTime.Today : ToEastern(_snapshots[^1].CapturedAtUtc).Date;
                _graphStartDate = anchor.AddMonths(-1);
                _graphEndDate = anchor;
                OnPropertyChanged(nameof(GraphStartDate));
                OnPropertyChanged(nameof(GraphEndDate));
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(GraphCustomDateVisibility));
            PopulateGraph(_snapshots);
        }
    }

    public string SelectedGraphMeasure
    {
        get => GraphMeasureLabel(_graphMeasure);
        set { var parsed = ParseGraphOption<DashboardGraphMeasure>(value); if (_graphMeasure != parsed) { _graphMeasure = parsed; OnPropertyChanged(); PopulateGraph(_snapshots); } }
    }

    public string SelectedGraphGrouping
    {
        get => _graphGrouping.ToString();
        set { var parsed = ParseGraphOption<DashboardGraphGrouping>(value); if (_graphGrouping != parsed) { _graphGrouping = parsed; OnPropertyChanged(); PopulateGraph(_snapshots); } }
    }

    public string SelectedGraphGranularity
    {
        get => _graphGranularity.ToString();
        set { var parsed = ParseGraphOption<DashboardGraphGranularity>(value); if (_graphGranularity != parsed) { _graphGranularity = parsed; OnPropertyChanged(); PopulateGraph(_snapshots); } }
    }

    public string SelectedGraphMode
    {
        get => _graphMode.ToString();
        set { var parsed = ParseGraphOption<DashboardGraphMode>(value); if (_graphMode != parsed) { _graphMode = parsed; OnPropertyChanged(); PopulateGraph(_snapshots); } }
    }

    public string SelectedGraphTopSeries
    {
        get => _graphTopSeriesLimit <= 0 ? "All series" : $"Top {_graphTopSeriesLimit}";
        set
        {
            var limit = value switch { "Top 10" => 10, "Top 20" => 20, "All series" => 0, _ => 5 };
            if (_graphTopSeriesLimit == limit) return;
            _graphTopSeriesLimit = limit;
            OnPropertyChanged();
            PopulateGraph(_snapshots);
        }
    }

    public bool CombineRemainingGraphSeries
    {
        get => _combineRemainingGraphSeries;
        set
        {
            if (_combineRemainingGraphSeries == value) return;
            _combineRemainingGraphSeries = value;
            OnPropertyChanged();
            PopulateGraph(_snapshots);
        }
    }

    public DateTime? GraphStartDate
    {
        get => _graphStartDate;
        set { if (_graphStartDate == value) return; _graphStartDate = value; OnPropertyChanged(); PopulateGraph(_snapshots); }
    }

    public DateTime? GraphEndDate
    {
        get => _graphEndDate;
        set { if (_graphEndDate == value) return; _graphEndDate = value; OnPropertyChanged(); PopulateGraph(_snapshots); }
    }

    public string SelectedPeriod
    {
        get => _sessionPeriod;
        set
        {
            if (_sessionPeriod == value) return;
            _sessionPeriod = value;
            OnPropertyChanged();
            PopulateInsights(_store.LoadAll());
        }
    }

    public DashboardSnapshotOption? SelectedSnapshot
    {
        get => _selectedSnapshot;
        set
        {
            if (_selectedSnapshot?.CapturedAtUtc == value?.CapturedAtUtc) return;
            _selectedSnapshot = value;
            OnPropertyChanged();
            PopulateSelectedSnapshot(_snapshots, value);
        }
    }

    private void Navigate(string? target)
    {
        var page = DashboardPage.Overview;
        var dataView = _dataView;
        if (target?.StartsWith("Data:", StringComparison.OrdinalIgnoreCase) == true)
        {
            dataView = Enum.TryParse<DataView>(target[5..], true, out var view) ? view : DataView.Tracks;
            page = DashboardPage.Data;
        }
        else
        {
            page = Enum.TryParse<DashboardPage>(target, true, out var parsed) ? parsed : DashboardPage.Overview;
        }

        if (_navigationHistory.Navigate(new DashboardNavigationState(page, dataView)))
            ApplyNavigation(new DashboardNavigationState(page, dataView));
    }

    private void MoveBack()
    {
        if (_navigationHistory.TryGoBack(out var target)) ApplyNavigation(target);
    }

    private void MoveForward()
    {
        if (_navigationHistory.TryGoForward(out var target)) ApplyNavigation(target);
    }

    private void ApplyNavigation(DashboardNavigationState target)
    {
        _page = target.Page;
        _dataView = target.DataView;
        if (_page == DashboardPage.Data) Refresh();
        OnPropertyChanged(nameof(Breadcrumbs));
        OnPropertyChanged(nameof(OverviewVisibility));
        OnPropertyChanged(nameof(LatestSnapshotVisibility));
        OnPropertyChanged(nameof(GraphsVisibility));
        OnPropertyChanged(nameof(DataVisibility));
        OnPropertyChanged(nameof(SettingsVisibility));
        OnPropertyChanged(nameof(AlbumArtVisibility));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(DataTitle));
        OnPropertyChanged(nameof(DataCaption));
        OnPropertyChanged(nameof(DataContextColumn));
        if (_page == DashboardPage.Settings) RefreshSchedulerState();
        if (_page == DashboardPage.AlbumArt) RefreshAlbumArtPreview();
    }

    private void OpenDiscordSettings()
    {
        Navigate("Settings");
        DiscordSettingsRequested?.Invoke();
    }

    private void Refresh()
    {
        try { Populate(_store.LoadAll()); }
        catch
        {
            _snapshots = [];
            SnapshotOptions.Clear();
            _selectedSnapshot = null;
            OnPropertyChanged(nameof(SelectedSnapshot));
            ClearAll();
            SnapshotSummary = "The local snapshot history could not be read";
            LatestSnapshotCaption = "Snapshot history unavailable";
            LatestSnapshotSummary = "No interval data is available";
            LatestSnapshotEmptyMessage = "Check the data location shown below";
            SnapshotSummaryLabel = "RECORDED INTERVAL";
            SnapshotListenColumnHeader = "NEW LISTENS";
            TopCaption = "Snapshot history unavailable";
            TopEmptyMessage = "Check the data location shown below";
            PopulateGraph([]);
        }
    }

    private async Task CaptureAsync()
    {
        _isCapturing = true;
        OnPropertyChanged(nameof(CaptureButtonText));
        OnPropertyChanged(nameof(CanCapture));
        try
        {
            var snapshot = await StaWorker.Run(() => new WmpLibraryScanner().Capture(DateTimeOffset.UtcNow));
            _store.Save(snapshot);
            Populate(_store.LoadAll(), selectLatestSnapshot: true);
        }
        finally
        {
            _isCapturing = false;
            OnPropertyChanged(nameof(CaptureButtonText));
            OnPropertyChanged(nameof(CanCapture));
        }
    }

    private void Populate(IReadOnlyList<LibrarySnapshot> snapshots, bool selectLatestSnapshot = false)
    {
        _snapshots = snapshots;
        if (snapshots.Count == 0)
        {
            SnapshotOptions.Clear();
            _selectedSnapshot = null;
            OnPropertyChanged(nameof(SelectedSnapshot));
            ClearAll();
            SnapshotSummary = "No local baseline yet";
            LatestSnapshotCaption = "No changes observed";
            LatestSnapshotSummary = "Capture a first snapshot to begin";
            LatestSnapshotEmptyMessage = "Capture a first snapshot to begin";
            SnapshotSummaryLabel = "RECORDED INTERVAL";
            SnapshotListenColumnHeader = "NEW LISTENS";
            TopCaption = "Waiting for a WMP library snapshot";
            TopEmptyMessage = "Your first snapshot will populate this list";
            MetricPeriodCaption = SelectedPeriod;
            PopulateGraph(snapshots);
            return;
        }

        var latest = snapshots[^1];
        SnapshotSummary = snapshots.Count == 1
            ? $"First baseline saved {ToEastern(latest.CapturedAtUtc):MMM d h:mm tt}"
            : $"{snapshots.Count:N0} snapshots recorded  ·  latest {ToEastern(latest.CapturedAtUtc):MMM d h:mm tt}";
        PopulateSnapshotHistory(snapshots, selectLatestSnapshot);
        PopulateInsights(snapshots);
        PopulateGraph(snapshots);
    }

    private void PopulateGraph(IReadOnlyList<LibrarySnapshot> snapshots)
    {
        var graph = DashboardGraphBuilder.Build(snapshots, new DashboardGraphOptions(
            _graphRange,
            _graphMeasure,
            _graphGrouping,
            _graphGranularity,
            _graphMode,
            _graphStartDate,
            _graphEndDate,
            _includeBaselineSnapshot,
            _graphTopSeriesLimit,
            _combineRemainingGraphSeries));

        GraphSummary = graph.Summary;
        GraphEmptyMessage = graph.EmptyMessage;
        GraphXAxes =
        [
            new Axis
            {
                Labels = graph.Labels,
                LabelsPaint = new SolidColorPaint(SKColor.Parse(ThemeManager.GraphLabelColorHex)),
                SeparatorsPaint = new SolidColorPaint(SKColor.Parse(ThemeManager.GraphRuleColorHex)),
                TextSize = 11,
                ForceStepToMin = true,
                MinStep = 1
            }
        ];
        GraphYAxes =
        [
            new Axis
            {
                Labeler = value => FormatGraphValue(value, _graphMeasure),
                LabelsPaint = new SolidColorPaint(SKColor.Parse(ThemeManager.GraphLabelColorHex)),
                SeparatorsPaint = new SolidColorPaint(SKColor.Parse(ThemeManager.GraphRuleColorHex)),
                TextSize = 11,
                MinLimit = 0
            }
        ];

        GraphSeries = graph.Series.Select((series, index) => CreateGraphSeries(series, index, graph.Tooltips)).ToArray();
        GraphLegendItems.Clear();
        for (var index = 0; index < graph.Series.Length; index++)
            GraphLegendItems.Add(new DashboardGraphLegendItem(graph.Series[index].Name, ToBrush(GraphColors[index % GraphColors.Length])));
        OnPropertyChanged(nameof(GraphChartVisibility));
        OnPropertyChanged(nameof(GraphEmptyVisibility));
        OnPropertyChanged(nameof(GraphSeriesOptionsVisibility));
    }

    private ISeries CreateGraphSeries(DashboardGraphSeries series, int colorIndex, IReadOnlyList<string> tooltips)
    {
        var color = GraphColors[colorIndex % GraphColors.Length];
        Func<LiveChartsCore.Kernel.ChartPoint, string> xTooltip = point => GraphTooltip(tooltips, point.Index);
        Func<LiveChartsCore.Kernel.ChartPoint, string> yTooltip = point => FormatGraphTooltipValue(series, point.Index, point.Coordinate.PrimaryValue, _graphMeasure, _graphMode);

        if (_graphMode == DashboardGraphMode.Cumulative)
        {
            return new LineSeries<double>
            {
                Name = series.Name,
                Values = series.Values,
                Stroke = new SolidColorPaint(color) { StrokeThickness = 3 },
                Fill = null,
                GeometrySize = 7,
                XToolTipLabelFormatter = xTooltip,
                YToolTipLabelFormatter = yTooltip
            };
        }

        if (_graphMode == DashboardGraphMode.Breakdown || _graphGrouping != DashboardGraphGrouping.Total)
        {
            return new ExactPointStackedColumnSeries
            {
                Name = series.Name,
                Values = series.Values,
                Fill = new SolidColorPaint(color),
                Stroke = null,
                StackGroup = 0,
                XToolTipLabelFormatter = xTooltip,
                YToolTipLabelFormatter = yTooltip
            };
        }

        return new ColumnSeries<double>
        {
            Name = series.Name,
            Values = series.Values,
            Fill = new SolidColorPaint(color),
            Stroke = null,
            MaxBarWidth = 42,
            XToolTipLabelFormatter = xTooltip,
            YToolTipLabelFormatter = yTooltip
        };
    }

    private static string GraphTooltip(IReadOnlyList<string> tooltips, int index) => index >= 0 && index < tooltips.Count ? tooltips[index] : "Observed snapshot interval";
    private static string FormatGraphValue(double value, DashboardGraphMeasure measure) => measure == DashboardGraphMeasure.ListeningTime
        ? FormatDuration(value)
        : value.ToString("N0", CultureInfo.CurrentCulture);
    private static string FormatGraphTooltipValue(DashboardGraphSeries series, int index, double displayedValue, DashboardGraphMeasure measure, DashboardGraphMode mode)
    {
        var unit = measure switch
        {
            DashboardGraphMeasure.ListeningTime => "listening time",
            DashboardGraphMeasure.TracksListened => "distinct tracks",
            _ => "listens"
        };
        var total = $"{FormatGraphValue(displayedValue, measure)} {unit}";
        if (mode == DashboardGraphMode.Cumulative)
        {
            var change = index >= 0 && index < series.IntervalValues.Length ? series.IntervalValues[index] : 0;
            var rate = index >= 0 && index < series.RatesPerDay.Length ? series.RatesPerDay[index] : null;
            return rate is null
                ? $"Total: {total}"
                : $"Total: {total}\nChange: +{FormatGraphValue(change, measure)} {unit}\nRate: +{FormatGraphValue(rate.Value, measure)}/day";
        }

        var currentRate = index >= 0 && index < series.RatesPerDay.Length ? series.RatesPerDay[index] : null;
        return currentRate is null
            ? $"Recorded: {total}"
            : $"Change: +{total}\nRate: +{FormatGraphValue(currentRate.Value, measure)}/day";
    }
    private static T ParseGraphOption<T>(string value) where T : struct, Enum
    {
        var normalized = value.Replace(" ", "", StringComparison.Ordinal);
        return Enum.TryParse<T>(normalized, true, out var parsed) ? parsed : default;
    }
    private static string GraphRangeLabel(DashboardGraphRange range) => range switch
    {
        DashboardGraphRange.PastWeek => "Past week",
        DashboardGraphRange.PastMonth => "Past month",
        DashboardGraphRange.PastYear => "Past year",
        DashboardGraphRange.Custom => "Custom range",
        _ => "All time"
    };
    private static string GraphMeasureLabel(DashboardGraphMeasure measure) => measure switch
    {
        DashboardGraphMeasure.ListeningTime => "Listening time",
        DashboardGraphMeasure.TracksListened => "Tracks listened",
        _ => "Listens"
    };
    private static readonly SKColor[] GraphColors =
    [
        SKColor.Parse("#176DB4"), SKColor.Parse("#237548"), SKColor.Parse("#935B16"),
        SKColor.Parse("#7851A9"), SKColor.Parse("#A2436D"), SKColor.Parse("#75879A")
    ];
    private static SolidColorBrush ToBrush(SKColor color) => new(Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue));

    private void PopulateSnapshotHistory(IReadOnlyList<LibrarySnapshot> snapshots, bool selectLatestSnapshot)
    {
        var priorSelection = _selectedSnapshot?.CapturedAtUtc;
        SnapshotOptions.Clear();
        for (var index = snapshots.Count - 1; index >= 0; index--)
        {
            var capturedAt = snapshots[index].CapturedAtUtc;
            var label = ToEastern(capturedAt).ToString("MMM d, yyyy h:mm tt", CultureInfo.CurrentCulture);
            SnapshotOptions.Add(new DashboardSnapshotOption(capturedAt, index == snapshots.Count - 1 ? $"{label} (latest)" : label));
        }

        _selectedSnapshot = selectLatestSnapshot
            ? SnapshotOptions.FirstOrDefault()
            : SnapshotOptions.FirstOrDefault(option => option.CapturedAtUtc == priorSelection) ?? SnapshotOptions.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedSnapshot));
        PopulateSelectedSnapshot(snapshots, _selectedSnapshot);
    }

    private void PopulateSelectedSnapshot(IReadOnlyList<LibrarySnapshot> snapshots, DashboardSnapshotOption? selection)
    {
        LatestSnapshotTracks.Clear();
        if (selection is null)
        {
            LatestSnapshotCaption = "No snapshots recorded";
            LatestSnapshotSummary = "Capture a first snapshot to begin";
            LatestSnapshotEmptyMessage = "Capture a first snapshot to begin";
            SnapshotSummaryLabel = "RECORDED INTERVAL";
            SnapshotListenColumnHeader = "NEW LISTENS";
            return;
        }

        var selectedIndex = -1;
        for (var index = 0; index < snapshots.Count; index++)
        {
            if (snapshots[index].CapturedAtUtc == selection.CapturedAtUtc)
            {
                selectedIndex = index;
                break;
            }
        }

        if (selectedIndex <= 0)
        {
            var baselineRows = snapshots[0].Tracks
                .Where(track => track.PlayCount > 0)
                .OrderByDescending(track => track.PlayCount)
                .ThenBy(track => track.Title, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            LatestSnapshotCaption = $"Baseline captured {ToEastern(selection.CapturedAtUtc):MMM d h:mm tt}";
            LatestSnapshotSummary = baselineRows.Length == 0
                ? "No recorded WMP plays at baseline"
                : $"{baselineRows.Length:N0} tracks with {baselineRows.Sum(track => track.PlayCount):N0} recorded WMP plays";
            LatestSnapshotEmptyMessage = "No recorded WMP plays at this baseline";
            SnapshotSummaryLabel = "BASELINE SNAPSHOT";
            SnapshotListenColumnHeader = "BASELINE PLAYS";
            foreach (var track in baselineRows)
                LatestSnapshotTracks.Add(DashboardSnapshotChange.FromBaseline(track, LatestSnapshotTracks.Count + 1));
            return;
        }

        var earlier = snapshots[selectedIndex - 1];
        var selected = snapshots[selectedIndex];
        var report = Reporting.Compare(earlier, selected, IncludeBaselineSnapshot);
        var increases = report.Rows.Where(row => row.Listens > 0).ToArray();
        var firstSeenListens = increases.Sum(row => row.FirstSeenListens);
        LatestSnapshotCaption = $"Changes from {ToEastern(earlier.CapturedAtUtc):MMM d h:mm tt} to {ToEastern(selected.CapturedAtUtc):MMM d h:mm tt}";
        LatestSnapshotSummary = increases.Length == 0
            ? "No new or first-seen WMP counts"
            : firstSeenListens > 0
                ? $"{increases.Length:N0} tracks with {increases.Sum(row => row.Listens):N0} listens, including {firstSeenListens:N0} first-seen WMP plays"
                : $"{increases.Length:N0} tracks with {increases.Sum(row => row.Listens):N0} new listens";
        foreach (var row in increases)
            LatestSnapshotTracks.Add(DashboardSnapshotChange.From(row.Track, row.Listens, row.FirstSeenListens, LatestSnapshotTracks.Count + 1));
        LatestSnapshotEmptyMessage = "No new or first-seen WMP counts in this snapshot interval";
        SnapshotSummaryLabel = "RECORDED INTERVAL";
        SnapshotListenColumnHeader = "LISTENS";
    }

    private void PopulateInsights(IReadOnlyList<LibrarySnapshot> snapshots)
    {
        TopTracks.Clear();
        TopAlbums.Clear();
        TopArtists.Clear();
        var entries = GetPeriodTracks(snapshots, out var caption, out var emptyMessage, out var metricPeriodCaption, out var includesFirstSeenCounts);
        MetricPeriodCaption = metricPeriodCaption;
        TopCaption = includesFirstSeenCounts ? $"{caption} · includes first-seen WMP counts" : caption;
        TopEmptyMessage = emptyMessage;
        OnPropertyChanged(nameof(DataCaption));

        foreach (var entry in entries.OrderByDescending(entry => entry.Count).ThenBy(entry => entry.Track.Title).Take(5))
            TopTracks.Add(DashboardSong.From(entry.Track, $"{entry.Count:N0}", TopTracks.Count + 1));

        foreach (var group in entries.GroupBy(entry => EmptyAsUnknown(entry.Track.Album, "Unknown album"))
                     .Select(group => new { Name = group.Key, Count = group.Sum(entry => entry.Count), First = group.First().Track })
                     .OrderByDescending(group => group.Count).ThenBy(group => group.Name).Take(5))
            TopAlbums.Add(new DashboardAggregate(group.Name, group.First.Artist, $"{group.Count:N0}", AlbumArtResolver.For(group.First), new WmpOpenTarget(WmpOpenKind.Album, group.First.SourceUrl, group.Name)));

        foreach (var group in entries.GroupBy(entry => EmptyAsUnknown(entry.Track.Artist, "Unknown artist"))
                     .Select(group => new { Name = group.Key, Count = group.Sum(entry => entry.Count), First = group.First().Track })
                     .OrderByDescending(group => group.Count).ThenBy(group => group.Name).Take(5))
            TopArtists.Add(new DashboardAggregate(group.Name, group.First.Album, $"{group.Count:N0}", AlbumArtResolver.For(group.First), new WmpOpenTarget(WmpOpenKind.Artist, group.First.SourceUrl, group.Name)));

        PopulateDataRows(entries);

        TotalListens = entries.Sum(entry => entry.Count).ToString("N0", CultureInfo.CurrentCulture);
        TotalListeningTime = FormatDuration(entries.Sum(entry => DurationSeconds(entry.Track.Duration) * entry.Count));
        var topArtist = TopArtists.FirstOrDefault();
        MetricTopArtist = topArtist?.Title ?? "-";
        MetricTopArtistCount = topArtist is null ? "" : $"{topArtist.CountLabel} listens";
    }

    private void PopulateDataRows(IReadOnlyList<TrackTally> entries)
    {
        DataRows.Clear();
        if (_dataView == DataView.Tracks)
        {
            foreach (var entry in entries.OrderByDescending(entry => entry.Count).ThenBy(entry => entry.Track.Title))
                DataRows.Add(new DashboardDataRow(DataRows.Count + 1, entry.Track.Title, EmptyAsUnknown(entry.Track.Artist, "Unknown artist"), EmptyAsUnknown(entry.Track.Album, "Unknown album"), $"{entry.Count:N0}", AlbumArtResolver.For(entry.Track), new WmpOpenTarget(WmpOpenKind.Track, entry.Track.SourceUrl, entry.Track.Title)));
            return;
        }

        if (_dataView == DataView.Albums)
        {
            foreach (var group in entries.GroupBy(entry => EmptyAsUnknown(entry.Track.Album, "Unknown album"))
                         .Select(group => new { Name = group.Key, Count = group.Sum(entry => entry.Count), TrackCount = group.Count(), First = group.First().Track })
                         .OrderByDescending(group => group.Count).ThenBy(group => group.Name))
                DataRows.Add(new DashboardDataRow(DataRows.Count + 1, group.Name, EmptyAsUnknown(group.First.Artist, "Unknown artist"), $"{group.TrackCount:N0} tracks", $"{group.Count:N0}", AlbumArtResolver.For(group.First), new WmpOpenTarget(WmpOpenKind.Album, group.First.SourceUrl, group.Name)));
            return;
        }

        foreach (var group in entries.GroupBy(entry => EmptyAsUnknown(entry.Track.Artist, "Unknown artist"))
                     .Select(group => new { Name = group.Key, Count = group.Sum(entry => entry.Count), First = group.First().Track })
                     .OrderByDescending(group => group.Count).ThenBy(group => group.Name))
            DataRows.Add(new DashboardDataRow(DataRows.Count + 1, group.Name, EmptyAsUnknown(group.First.Album, "Unknown album"), group.First.Album, $"{group.Count:N0}", AlbumArtResolver.For(group.First), new WmpOpenTarget(WmpOpenKind.Artist, group.First.SourceUrl, group.Name)));
    }

    private IReadOnlyList<TrackTally> GetPeriodTracks(
        IReadOnlyList<LibrarySnapshot> snapshots,
        out string caption,
        out string emptyMessage,
        out string metricPeriodCaption,
        out bool includesFirstSeenCounts)
    {
        includesFirstSeenCounts = false;
        if (snapshots.Count == 0)
        {
            caption = "Waiting for a WMP library snapshot";
            emptyMessage = "Your first snapshot will populate this list";
            metricPeriodCaption = SelectedPeriod;
            return [];
        }

        var latest = snapshots[^1];
        var baseline = snapshots[0];
        if (SelectedPeriod == "All time")
        {
            caption = FormatDataRange(baseline.CapturedAtUtc, latest.CapturedAtUtc);
            metricPeriodCaption = caption;
            if (IncludeBaselineSnapshot)
            {
                emptyMessage = "No audio tracks found in the latest snapshot";
                var allTimeTallies = latest.Tracks.Where(track => track.PlayCount > 0).Select(track => new TrackTally(track, track.PlayCount, track.PlayCount)).ToArray();
                includesFirstSeenCounts = allTimeTallies.Length > 0;
                return allTimeTallies;
            }

            if (snapshots.Count < 2)
            {
                emptyMessage = "Capture one more snapshot to calculate observed listens";
                return [];
            }

            emptyMessage = "No play-count increases observed since the first snapshot";
            var observedAllTime = ToTallies(Reporting.CompareIntervals(snapshots, baseline.CapturedAtUtc, false));
            return observedAllTime;
        }

        var span = SelectedPeriod switch { "Past week" => TimeSpan.FromDays(7), "Past month" => TimeSpan.FromDays(31), "Past year" => TimeSpan.FromDays(365), _ => TimeSpan.Zero };
        var comparisonStart = latest.CapturedAtUtc - span;
        caption = FormatDataRange(comparisonStart, latest.CapturedAtUtc);
        metricPeriodCaption = caption;
        var includesBaselineDate = comparisonStart <= baseline.CapturedAtUtc;

        if (snapshots.Count < 2)
        {
            if (IncludeBaselineSnapshot && includesBaselineDate)
            {
                emptyMessage = "No audio tracks found in the first snapshot";
                var baselineTallies = BaselineTallies(baseline);
                includesFirstSeenCounts = baselineTallies.Count > 0;
                return baselineTallies;
            }

            emptyMessage = "Capture one more snapshot to calculate new listens";
            return [];
        }

        emptyMessage = "No play-count increases observed in this period";
        var entries = ToTallies(Reporting.CompareIntervals(snapshots, comparisonStart, IncludeBaselineSnapshot));
        if (IncludeBaselineSnapshot && includesBaselineDate)
            entries = IncludeBaselineTallies(baseline, entries);

        includesFirstSeenCounts = entries.Any(entry => entry.FirstSeenListens > 0);
        return entries;
    }

    private static IReadOnlyList<TrackTally> BaselineTallies(LibrarySnapshot baseline) =>
        baseline.Tracks.Where(track => track.PlayCount > 0).Select(track => new TrackTally(track, track.PlayCount, track.PlayCount)).ToArray();

    private static IReadOnlyList<TrackTally> ToTallies(PeriodReport report) =>
        ToTallies(report.Rows);

    private static IReadOnlyList<TrackTally> ToTallies(IEnumerable<ReportRow> rows) =>
        rows.Where(row => row.Listens > 0).Select(row => new TrackTally(row.Track, row.Listens, row.FirstSeenListens)).ToArray();

    private static IReadOnlyList<TrackTally> IncludeBaselineTallies(LibrarySnapshot baseline, IReadOnlyList<TrackTally> observed)
    {
        var totals = BaselineTallies(baseline).ToDictionary(entry => entry.Track.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in observed)
        {
            if (totals.TryGetValue(entry.Track.Id, out var existing))
                totals[entry.Track.Id] = new TrackTally(entry.Track, existing.Count + entry.Count, existing.FirstSeenListens + entry.FirstSeenListens);
            else
                totals[entry.Track.Id] = entry;
        }
        return totals.Values.ToArray();
    }

    private void RefreshSchedulerState()
    {
        _automaticSnapshotTaskState = ReadAutomaticSnapshotTaskState();
        SchedulerState = _automaticSnapshotTaskState switch
        {
            AutomaticSnapshotTaskState.Enabled => "Daily snapshots are enabled",
            AutomaticSnapshotTaskState.Disabled => "Automatic snapshots are disabled. The scheduled task is kept so you can enable it again.",
            AutomaticSnapshotTaskState.Missing => "No automatic snapshot task found. Set one up with the command in the README.",
            _ => "Unable to check automatic snapshots"
        };
        OnPropertyChanged(nameof(AutomaticSnapshotsToggleText));
    }

    private async Task ToggleSchedulerAsync()
    {
        if (_automaticSnapshotTaskState == AutomaticSnapshotTaskState.Missing)
        {
            SchedulerState = "No automatic snapshot task found. Use the README setup command to create one first.";
            return;
        }

        if (_automaticSnapshotTaskState == AutomaticSnapshotTaskState.Unavailable)
        {
            SchedulerState = "Automatic snapshots could not be changed. Open Task Scheduler to check its availability.";
            return;
        }

        var enable = _automaticSnapshotTaskState == AutomaticSnapshotTaskState.Disabled;
        var changed = await Task.Run(() => SetAutomaticSnapshotTaskEnabled(enable));
        if (!changed)
        {
            SchedulerState = "Automatic snapshots could not be changed. Open Task Scheduler to check the task.";
            return;
        }

        RefreshSchedulerState();
    }

    private static AutomaticSnapshotTaskState ReadAutomaticSnapshotTaskState()
    {
        object? service = null;
        object? folder = null;
        object? task = null;
        try
        {
            var serviceType = Type.GetTypeFromProgID("Schedule.Service");
            if (serviceType is null) return AutomaticSnapshotTaskState.Unavailable;

            service = Activator.CreateInstance(serviceType);
            if (service is null) return AutomaticSnapshotTaskState.Unavailable;
            dynamic scheduler = service;
            scheduler.Connect();
            folder = scheduler.GetFolder("\\");
            dynamic root = folder;
            task = root.GetTask(ScheduledTaskName);
            dynamic registeredTask = task;
            return Convert.ToBoolean(registeredTask.Enabled, CultureInfo.InvariantCulture)
                ? AutomaticSnapshotTaskState.Enabled
                : AutomaticSnapshotTaskState.Disabled;
        }
        catch (COMException error) when ((uint)error.HResult == 0x80070002)
        {
            return AutomaticSnapshotTaskState.Missing;
        }
        catch
        {
            return AutomaticSnapshotTaskState.Unavailable;
        }
        finally
        {
            ReleaseComObject(task);
            ReleaseComObject(folder);
            ReleaseComObject(service);
        }
    }

    private static bool SetAutomaticSnapshotTaskEnabled(bool enabled)
    {
        object? service = null;
        object? folder = null;
        object? task = null;
        try
        {
            var serviceType = Type.GetTypeFromProgID("Schedule.Service");
            if (serviceType is null) return false;

            service = Activator.CreateInstance(serviceType);
            if (service is null) return false;
            dynamic scheduler = service;
            scheduler.Connect();
            folder = scheduler.GetFolder("\\");
            dynamic root = folder;
            task = root.GetTask(ScheduledTaskName);
            dynamic registeredTask = task;
            registeredTask.Enabled = enabled;
            return Convert.ToBoolean(registeredTask.Enabled, CultureInfo.InvariantCulture) == enabled;
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseComObject(task);
            ReleaseComObject(folder);
            ReleaseComObject(service);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    private void ShowSnapshotStatus()
    {
        try
        {
            MessageBox.Show(SnapshotStatus.Describe(_store.LoadAll()), "Snapshot status", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch
        {
            MessageBox.Show("The local snapshot history could not be read", "Snapshot status", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        _isCheckingForUpdates = true;
        OnPropertyChanged(nameof(CanCheckForUpdates));
        OnPropertyChanged(nameof(UpdateButtonText));

        try
        {
            var repositoryUrl = Environment.GetEnvironmentVariable("WMPL_WRAP_REPOSITORY_URL") ?? DefaultRepositoryUrl;
            var release = await GitHubReleaseChecker.GetLatestAsync(repositoryUrl);
            var installedVersion = GetType().Assembly.GetName().Version ?? new Version(1, 1, 0);

            if (release is null)
            {
                UpdateStatus = "No published GitHub release is available yet";
                MessageBox.Show("No published GitHub release is available yet.", "Check for updates", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (release.Version > installedVersion)
            {
                UpdateStatus = $"WMPL Wrap v{release.Version.ToString(3)} is available";
                var result = MessageBox.Show(
                    $"WMPL Wrap v{release.Version.ToString(3)} is available.\n\nOpen its GitHub release page?",
                    "Update available",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);
                if (result == MessageBoxResult.Yes)
                    Process.Start(new ProcessStartInfo(release.ReleaseUrl) { UseShellExecute = true });
                return;
            }

            UpdateStatus = $"WMPL Wrap {AppVersion} is up to date";
            MessageBox.Show($"WMPL Wrap {AppVersion} is up to date.", "Check for updates", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error)
        {
            UpdateStatus = "Unable to check GitHub for updates";
            MessageBox.Show($"An update check could not be completed.\n\n{error.Message}", "Check for updates", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _isCheckingForUpdates = false;
            OnPropertyChanged(nameof(CanCheckForUpdates));
            OnPropertyChanged(nameof(UpdateButtonText));
        }
    }

    public void OpenInWmp(object? item)
    {
        if (!OpenInWmpOnDoubleClick || item is not IOpenInWmpTarget { OpenTarget: { } target }) return;
        try
        {
            WmpLibraryLauncher.Open(target);
        }
        catch (Exception error)
        {
            MessageBox.Show($"Windows Media Player could not open this item\n\n{error.Message}", "Open in Windows Media Player", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveDiscordSettings()
    {
        if (!_discordRpcEnabled)
        {
            SavePreferences();
            _discordPresence.Stop();
            return;
        }

        if (!TryNormalizeDiscordApplicationId(out var applicationId))
        {
            _discordRpcEnabled = false;
            OnPropertyChanged(nameof(DiscordRpcEnabled));
            OnPropertyChanged(nameof(CanManageAlbumArt));
            DiscordStatus = "Enter a valid numeric Discord Application ID before enabling Rich Presence";
            DiscordLastError = "Discord Application IDs contain digits only.";
            SavePreferences();
            _discordPresence.Stop();
            return;
        }

        _discordApplicationId = applicationId;
        OnPropertyChanged(nameof(DiscordApplicationId));
        SavePreferences();
        StartDiscordPresence();
    }

    private void StartDiscordPresence()
    {
        if (!TryNormalizeDiscordApplicationId(out var applicationId))
        {
            DiscordStatus = "Enter a valid numeric Discord Application ID before enabling Rich Presence";
            return;
        }

        _discordApplicationId = applicationId;
        _discordPresence.Start(CreateDiscordPreferences());
    }

    private void ReconnectDiscord()
    {
        if (!_discordRpcEnabled)
        {
            DiscordStatus = "Enable Discord Rich Presence first";
            return;
        }
        SaveDiscordSettings();
    }

    private void SaveAlbumArtMapping()
    {
        var artist = DiscordAlbumArtMapping.Normalize(AlbumArtArtist);
        var title = DiscordAlbumArtMapping.Normalize(AlbumArtTitle);
        var assetKey = AlbumArtAssetKey.Trim();
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(assetKey))
        {
            AlbumArtPreviewStatus = "Album artist, album title, and Discord asset key are all required";
            return;
        }

        var mapping = new DiscordAlbumArtMapping(artist, title, assetKey);
        var existingIndex = AlbumArtMappings
            .Select((candidate, index) => (candidate, index))
            .FirstOrDefault(entry => string.Equals(DiscordAlbumArtMapping.Normalize(entry.candidate.AlbumArtist), artist, StringComparison.OrdinalIgnoreCase)
                && string.Equals(DiscordAlbumArtMapping.Normalize(entry.candidate.AlbumTitle), title, StringComparison.OrdinalIgnoreCase)).index;
        var matching = AlbumArtMappings.FirstOrDefault(candidate => string.Equals(DiscordAlbumArtMapping.Normalize(candidate.AlbumArtist), artist, StringComparison.OrdinalIgnoreCase)
            && string.Equals(DiscordAlbumArtMapping.Normalize(candidate.AlbumTitle), title, StringComparison.OrdinalIgnoreCase));

        if (matching is null) AlbumArtMappings.Add(mapping);
        else AlbumArtMappings[existingIndex] = mapping;
        SelectedAlbumArtMapping = mapping;
        SavePreferences();
        if (_discordRpcEnabled) StartDiscordPresence();
        AlbumArtPreviewStatus = "Mapping saved locally. Discord resolves the configured asset key when it receives the presence.";
    }

    private void DeleteAlbumArtMapping()
    {
        if (SelectedAlbumArtMapping is not { } mapping) return;
        AlbumArtMappings.Remove(mapping);
        SelectedAlbumArtMapping = null;
        AlbumArtArtist = "";
        AlbumArtTitle = "";
        AlbumArtAssetKey = "";
        SavePreferences();
        if (_discordRpcEnabled) StartDiscordPresence();
        RefreshAlbumArtPreview();
    }

    private void UseCurrentTrackForAlbumArt()
    {
        if (_discordPresence.CurrentPlayback is not { HasMedia: true } playback)
        {
            AlbumArtPreviewStatus = "Start a track in Windows Media Player, then try again";
            return;
        }

        SelectedAlbumArtMapping = null;
        AlbumArtArtist = playback.EffectiveAlbumArtist;
        AlbumArtTitle = playback.Album;
        AlbumArtAssetKey = "";
        RefreshAlbumArtPreview();
    }

    private void RefreshAlbumArtPreview()
    {
        var current = _discordPresence.CurrentPlayback;
        if (current is { HasMedia: true })
        {
            var localArtwork = AlbumArtResolver.For(current);
            AlbumArtPreview = localArtwork ?? AlbumArtResolver.DiscordFallback();
            AlbumArtPreviewStatus = localArtwork is null
                ? "No local cover was found for the current WMP track; showing the bundled fallback preview"
                : "Local cover found for the current WMP track. Discord asset availability is verified by Discord when sent.";
            return;
        }

        var artist = DiscordAlbumArtMapping.Normalize(AlbumArtArtist);
        var album = DiscordAlbumArtMapping.Normalize(AlbumArtTitle);
        var historicalTrack = _snapshots.Reverse().SelectMany(snapshot => snapshot.Tracks).FirstOrDefault(track =>
            string.Equals(DiscordAlbumArtMapping.Normalize(track.Artist), artist, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(DiscordAlbumArtMapping.Normalize(track.Album), album, StringComparison.OrdinalIgnoreCase));
        AlbumArtPreview = historicalTrack is null ? AlbumArtResolver.DiscordFallback() : AlbumArtResolver.For(historicalTrack) ?? AlbumArtResolver.DiscordFallback();
        AlbumArtPreviewStatus = historicalTrack is null
            ? "No local matching track was found; showing the bundled fallback preview"
            : "Previewing locally resolved artwork from the selected album";
    }

    private void OnDiscordStatusChanged(DiscordRpcStatus status)
    {
        void Apply()
        {
            DiscordStatus = status.Summary;
            DiscordNowPlaying = string.IsNullOrWhiteSpace(status.NowPlaying) ? "No status is being shared" : status.NowPlaying;
            DiscordArtworkStatus = string.IsNullOrWhiteSpace(status.Artwork) ? "Fallback asset: wmp_empty" : status.Artwork;
            DiscordLastError = status.LastError;
            DiscordLastUpdated = status.LastUpdatedAtUtc is { } updated ? $"Last update {ToEastern(updated):h:mm:ss tt}" : "";
            _discordRpcConnectionState = status.ConnectionState;
            OnPropertyChanged(nameof(DiscordFooterStatus));
            OnPropertyChanged(nameof(DiscordFooterToolTip));
            RefreshAlbumArtPreview();
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Apply();
        else dispatcher.BeginInvoke(new Action(Apply));
    }

    private DiscordRpcPreferences CreateDiscordPreferences() => new(
        _discordRpcEnabled,
        _discordApplicationId.Trim(),
        _detectStalledPlayback,
        AlbumArtMappings.ToArray(),
        _keepDiscordPresenceBetweenTracks,
        _keepDiscordPresenceRunningWhenClosed);

    private bool TryNormalizeDiscordApplicationId(out string applicationId)
    {
        applicationId = _discordApplicationId.Trim();
        return applicationId.Length is >= 17 and <= 20 && applicationId.All(char.IsDigit);
    }

    private void SavePreferences() => _settingsStore.Save(new DashboardPreferences(
        _includeBaselineSnapshot,
        _openInWmpOnDoubleClick,
        CreateDiscordPreferences(),
        _desktopTheme,
        _accentHue));

    private void ApplyAppearance()
    {
        ThemeManager.Apply(_desktopTheme, _accentHue);
        PopulateGraph(_snapshots);
        SavePreferences();
        AppearanceChanged?.Invoke();
    }

    private void ResetAppearance()
    {
        _desktopTheme = DesktopTheme.Light;
        _accentHue = ThemeManager.DefaultAccentHue;
        ApplyAppearance();
        OnPropertyChanged(nameof(SelectedTheme));
        OnPropertyChanged(nameof(AccentHue));
        OnPropertyChanged(nameof(AccentHueLabel));
        OnPropertyChanged(nameof(AccentPreview));
        OnPropertyChanged(nameof(IsDarkTheme));
    }

    private static string AccentHueName(int hue) => ThemeManager.NormalizeHue(hue) switch
    {
        >= 345 or < 15 => "Red",
        < 45 => "Orange",
        < 70 => "Gold",
        < 155 => "Green",
        < 195 => "Teal",
        < 250 => "Blue",
        < 300 => "Violet",
        _ => "Rose"
    };

    private static void OpenScheduler() => Process.Start(new ProcessStartInfo("taskschd.msc") { UseShellExecute = true });
    private static void OpenGitHub() => Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("WMPL_WRAP_REPOSITORY_URL") ?? DefaultRepositoryUrl) { UseShellExecute = true });
    private void OpenDiscordDeveloperPortal()
    {
        var target = TryNormalizeDiscordApplicationId(out var applicationId)
            ? $"https://discord.com/developers/applications/{applicationId}/rich-presence/assets"
            : "https://discord.com/developers/applications";
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }
    private static void OpenReleases()
    {
        var repositoryUrl = Environment.GetEnvironmentVariable("WMPL_WRAP_REPOSITORY_URL") ?? DefaultRepositoryUrl;
        Process.Start(new ProcessStartInfo($"{repositoryUrl.TrimEnd('/')}/releases") { UseShellExecute = true });
    }

    private static string ResolveDataDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("WMPL_WRAP_DATA");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "data");
            if (Directory.Exists(candidate)) return candidate;
        }

        return Path.Combine(Environment.CurrentDirectory, "data");
    }
    private static string EmptyAsUnknown(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
    private static double DurationSeconds(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
    private static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} m" : $"{span.Minutes} m";
    }
    private static string FormatDataRange(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        var start = ToEastern(startUtc);
        var end = ToEastern(endUtc);
        return start.Year == end.Year
            ? $"Data from {start:MMM d} to {end:MMM d}"
            : $"Data from {start:MMM d, yyyy} to {end:MMM d, yyyy}";
    }
    private void ClearAll() { LatestSnapshotTracks.Clear(); TopTracks.Clear(); TopAlbums.Clear(); TopArtists.Clear(); DataRows.Clear(); TotalListens = "-"; TotalListeningTime = "-"; MetricTopArtist = "-"; MetricTopArtistCount = ""; }
    private static DateTimeOffset ToEastern(DateTimeOffset utc) => TimeZoneInfo.ConvertTimeBySystemTimeZoneId(utc, "Eastern Standard Time");
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(property);
        return true;
    }
    private void OnPropertyChanged([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    public void Dispose()
    {
        _discordPresence.StatusChanged -= OnDiscordStatusChanged;
        _discordPresence.Dispose();
    }
}

internal static class GitHubReleaseChecker
{
    private static readonly HttpClient Client = CreateClient();

    public static async Task<GitHubRelease?> GetLatestAsync(string repositoryUrl)
    {
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var repositoryUri) ||
            !string.Equals(repositoryUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The project repository is not a valid GitHub URL.");

        var segments = repositoryUri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) throw new InvalidOperationException("The project repository could not be identified.");

        var endpoint = $"https://api.github.com/repos/{segments[0]}/{segments[1]}/releases/latest";
        using var response = await Client.GetAsync(endpoint);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var payload = JsonSerializer.Deserialize<GitHubReleasePayload>(json);
        if (payload is null || string.IsNullOrWhiteSpace(payload.TagName) || string.IsNullOrWhiteSpace(payload.HtmlUrl))
            throw new InvalidOperationException("GitHub returned an incomplete release record.");

        var tag = payload.TagName.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(tag, out var version))
            throw new InvalidOperationException($"The latest release tag '{payload.TagName}' is not a supported version number.");

        return new GitHubRelease(version, payload.HtmlUrl);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WMPL-Wrap-Update-Check");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private sealed record GitHubReleasePayload(
        [property: System.Text.Json.Serialization.JsonPropertyName("tag_name")] string? TagName,
        [property: System.Text.Json.Serialization.JsonPropertyName("html_url")] string? HtmlUrl);
}

internal sealed record GitHubRelease(Version Version, string ReleaseUrl);

public interface IOpenInWmpTarget
{
    WmpOpenTarget OpenTarget { get; }
}

public sealed record DashboardSong(int Rank, string Title, string ArtistAlbum, string CountLabel, ImageSource? Artwork, WmpOpenTarget OpenTarget) : IOpenInWmpTarget
{
    public static DashboardSong From(TrackSnapshot track, string count, int rank) => new(rank, track.Title, string.IsNullOrWhiteSpace(track.Album) ? track.Artist : $"{track.Artist} · {track.Album}", count, AlbumArtResolver.For(track), new WmpOpenTarget(WmpOpenKind.Track, track.SourceUrl, track.Title));
}

public sealed record DashboardAggregate(string Title, string Subtitle, string CountLabel, ImageSource? Artwork, WmpOpenTarget OpenTarget) : IOpenInWmpTarget;
public sealed record DashboardDataRow(int Rank, string Title, string Subtitle, string Context, string CountLabel, ImageSource? Artwork, WmpOpenTarget OpenTarget) : IOpenInWmpTarget;
public sealed record DashboardGraphLegendItem(string Name, System.Windows.Media.Brush Color);
public sealed record DashboardSnapshotOption(DateTimeOffset CapturedAtUtc, string Label)
{
    public override string ToString() => Label;
}
public sealed record DashboardSnapshotChange(int Rank, string Title, string Artist, string Album, string Duration, string ListensLabel, string TotalCountLabel, ImageSource? Artwork, WmpOpenTarget OpenTarget) : IOpenInWmpTarget
{
    public static DashboardSnapshotChange FromBaseline(TrackSnapshot track, int rank) => new(
        rank,
        track.Title,
        string.IsNullOrWhiteSpace(track.Artist) ? "Unknown artist" : track.Artist,
        string.IsNullOrWhiteSpace(track.Album) ? "Unknown album" : track.Album,
        FormatTrackDuration(track.Duration),
        track.PlayCount.ToString("N0", CultureInfo.CurrentCulture),
        track.PlayCount.ToString("N0", CultureInfo.CurrentCulture),
        AlbumArtResolver.For(track),
        new WmpOpenTarget(WmpOpenKind.Track, track.SourceUrl, track.Title));

    public static DashboardSnapshotChange From(TrackSnapshot track, long listens, long firstSeenListens, int rank) => new(
        rank,
        track.Title,
        string.IsNullOrWhiteSpace(track.Artist) ? "Unknown artist" : track.Artist,
        string.IsNullOrWhiteSpace(track.Album) ? "Unknown album" : track.Album,
        FormatTrackDuration(track.Duration),
        firstSeenListens > 0 ? $"{listens:N0} first-seen" : $"+{listens:N0}",
        track.PlayCount.ToString("N0", CultureInfo.CurrentCulture),
        AlbumArtResolver.For(track),
        new WmpOpenTarget(WmpOpenKind.Track, track.SourceUrl, track.Title));

    private static string FormatTrackDuration(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) return "—";
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}" : $"{span.Minutes}:{span.Seconds:D2}";
    }
}
internal sealed record TrackTally(TrackSnapshot Track, long Count, long FirstSeenListens = 0);
internal sealed record DashboardPreferences(
    bool IncludeBaselineSnapshot = true,
    bool OpenInWmpOnDoubleClick = true,
    DiscordRpcPreferences? DiscordRpc = null,
    DesktopTheme DesktopTheme = global::WmplWrap.Desktop.DesktopTheme.Light,
    int AccentHue = ThemeManager.DefaultAccentHue);

internal sealed class DashboardSettingsStore(string dataDirectory)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path = Path.Combine(Path.GetFullPath(dataDirectory), "desktop-settings.json");

    public DashboardPreferences Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<DashboardPreferences>(File.ReadAllText(_path), Json) ?? new DashboardPreferences()
                : new DashboardPreferences();
        }
        catch { return new DashboardPreferences(); }
    }

    public void Save(DashboardPreferences preferences)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(preferences, Json));
        }
        catch { /* Preferences are non-essential and stay active for this session */ }
    }
}

internal enum DashboardPage { Overview, Graphs, Data, LatestSnapshot, Settings, AlbumArt }
internal enum DataView { Tracks, Albums, Artists }
internal enum AutomaticSnapshotTaskState { Unavailable, Missing, Enabled, Disabled }

internal readonly record struct DashboardNavigationState(DashboardPage Page, DataView DataView);

/// <summary>
/// Tracks the user's actual visit order rather than relying on the declaration order of pages.
/// </summary>
internal sealed class DashboardNavigationHistory(DashboardNavigationState initial)
{
    private readonly List<DashboardNavigationState> _back = [];
    private readonly List<DashboardNavigationState> _forward = [];

    public DashboardNavigationState Current { get; private set; } = initial;
    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    public bool Navigate(DashboardNavigationState target)
    {
        if (target == Current) return false;
        _back.Add(Current);
        _forward.Clear();
        Current = target;
        return true;
    }

    public bool TryGoBack(out DashboardNavigationState target)
    {
        if (_back.Count == 0)
        {
            target = Current;
            return false;
        }

        _forward.Add(Current);
        target = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        Current = target;
        return true;
    }

    public bool TryGoForward(out DashboardNavigationState target)
    {
        if (_forward.Count == 0)
        {
            target = Current;
            return false;
        }

        _back.Add(Current);
        target = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        Current = target;
        return true;
    }
}

internal sealed class RelayCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute(parameter);
}

internal sealed class AsyncRelayCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? parameter) => canExecute();
    public async void Execute(object? parameter) { if (canExecute()) await execute(); }
}

internal static class StaWorker
{
    public static Task<T> Run<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { completion.SetResult(work()); } catch (Exception ex) { completion.SetException(ex); } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}

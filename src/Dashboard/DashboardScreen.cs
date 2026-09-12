// Prospero Explorer
// Copyright (C) 2026 SvenGDK

using ProsperoExplorer.Shell;
using SharpProspero.Application;
using SharpProspero.Interop;
using SharpProspero.Interop.Net;
using SharpProspero.Memory;
using SharpProspero.Platform;
using SharpProspero.Storage;
using SharpProspero.Text;
using SharpProspero.Ui;
using System;
using System.Collections.Generic;

namespace ProsperoExplorer.Dashboard;

/// <summary>
/// Everything the console will say about itself, in six tabs: the machine, who is signed in, the
/// network, the managed heap and the memory pools, the mounted storage, and the display.
/// </summary>
/// <remarks>
/// Every reading is a service call that can refuse, and a dashboard whose job is to report the state of
/// the machine is exactly the page most likely to be opened when something is already wrong. So each
/// value is read behind a guard and a refusal becomes the word "unavailable" in its own row, leaving
/// the rest of the page intact.
/// </remarks>
internal sealed class DashboardScreen : ExplorerScreen
{
    /// <summary>What a row shows when the service behind it refused.</summary>
    private const string Unavailable = "unavailable";

    /// <summary>What a row shows when the reading has no value rather than having failed.</summary>
    private const string NotApplicable = "-";

    // Storage and the user's own settings change only when the user goes and changes them, which they
    // cannot do while this page is in front of them. Listing six directories sixty times a second to
    // watch for that would be file access the page has no use for, so those two groups refresh on a
    // timer instead.
    private const double SlowRefreshSeconds = 1.0;

    /// <summary>The mount points worth reporting on: the writable area, the package, and the USB ports.</summary>
    private static readonly string[] MountPoints =
        ["/data", "/app0", "/mnt/usb0", "/mnt/usb1", "/mnt/usb2", "/mnt/usb3"];

    // The machine. None of these can change while the module runs, so they are read once.
    private readonly KeyValueRow _systemSoftware = new("System software");
    private readonly KeyValueRow _allowedSdk = new("Allowed SDK");
    private readonly KeyValueRow _consoleId = new("Console identifier");
    private readonly KeyValueRow _processors = new("Processors");
    private readonly KeyValueRow _supportedBuild = new("Supported build");
    private readonly KeyValueRow _supportedRange = new("Supported firmware");

    // The user's own settings.
    private readonly KeyValueRow _systemName = new("System name");
    private readonly KeyValueRow _language = new("Language");
    private readonly KeyValueRow _dateFormat = new("Date format");
    private readonly KeyValueRow _timeFormat = new("Time format");
    private readonly KeyValueRow _timeZone = new("Time zone");
    private readonly KeyValueRow _summerTime = new("Summer time");

    // Who is signed in. There are four sign-in slots, so four rows are enough; the unused ones hide.
    private readonly KeyValueRow _signedInAs = new("Signed in as");
    private readonly KeyValueRow _signedInId = new("User identifier");
    private readonly KeyValueRow _profileCount = new("Logged in");
    private readonly KeyValueRow[] _profiles = [new(""), new(""), new(""), new("")];

    // The network connection.
    private readonly KeyValueRow _netState = new("State");
    private readonly KeyValueRow _netLink = new("Link");
    private readonly KeyValueRow _netAddress = new("Address");
    private readonly KeyValueRow _netMask = new("Subnet mask");
    private readonly KeyValueRow _netGateway = new("Gateway");
    private readonly KeyValueRow _netDns = new("Primary name server");
    private readonly KeyValueRow _netName = new("Network name");
    private readonly KeyValueRow _netMac = new("Hardware address");
    private readonly KeyValueRow _netSignal = new("Signal strength");
    private readonly KeyValueRow _netMtu = new("Largest packet");

    // The managed heap and the memory pools underneath it.
    private readonly KeyValueRow _heapInUse = new("Heap in use");
    private readonly KeyValueRow _heapAllocated = new("Allocated in total");
    private readonly KeyValueRow _heapCeiling = new("Ceiling");
    private readonly KeyValueRow _heapPressure = new("Pressure");
    private readonly KeyValueRow _heapCollections = new("Collections");
    private readonly KeyValueRow _flexibleAvailable = new("Flexible memory free");
    private readonly KeyValueRow _largestDirect = new("Largest direct block");

    // The mounted storage, one row per mount point.
    private readonly KeyValueRow[] _mounts;

    // The display.
    private readonly KeyValueRow _safeArea = new("Safe area");
    private readonly KeyValueRow _inBackground = new("In background");
    private readonly KeyValueRow _systemOverlay = new("System overlay");

    // The tabs, held so only the one on screen is read each frame, and the visual read-outs: the dials
    // show a fraction at a glance, the ring turns while the link is still coming up.
    private readonly TabView _tabs = new();
    private readonly Gauge _pressureGauge = new() { Diameter = 84 };
    private readonly Gauge _safeAreaGauge = new() { Diameter = 84 };
    private readonly Spinner _networkSpinner = new() { Visible = false };

    private NetworkInfo? _network;
    private bool _networkReported;
    private bool _refreshing = true;
    private double _sinceSlowRefresh;

    // The tab last refreshed, so switching to another reads it at once rather than after the slow timer.
    private int _lastTab = -1;

    /// <summary>Creates the dashboard.</summary>
    /// <param name="shell">The shell the page runs in.</param>
    public DashboardScreen(ExplorerShell shell) : base(shell)
    {
        _mounts = new KeyValueRow[MountPoints.Length];
        for (int i = 0; i < MountPoints.Length; i++)
            _mounts[i] = new KeyValueRow(MountPoints[i]);
    }

    /// <inheritdoc/>
    public override string Title => "System information";

    /// <inheritdoc/>
    public override string Hint => "Left and Right change tab, Circle goes back.";

    /// <inheritdoc/>
    protected override UiElement BuildRoot()
    {
        _tabs.Add("Console", BuildConsoleTab());
        _tabs.Add("Users", BuildUsersTab());
        _tabs.Add("Network", BuildNetworkTab());
        _tabs.Add("Memory", BuildMemoryTab());
        _tabs.Add("Storage", BuildStorageTab());
        _tabs.Add("Display", BuildDisplayTab());

        return new StackPanel()
            .Add(new Label("Live, straight from the system services. A reading the console refuses shows as unavailable.")
            {
                TextColor = Shell.Theme.TextMuted,
            })
            .Add(new Separator())
            .Add(_tabs);
    }

    /// <inheritdoc/>
    public override void OnShown()
    {
        // The status service is started once and kept: starting it per frame would be a service call
        // for every reading, and the page is on screen for as long as the user is reading it.
        if (_network is null && !_networkReported)
            OpenNetwork();

        // Filled in before the first frame is drawn, so the page never shows a column of blanks while
        // it waits for its first tick. Every tab is read once here; from then on only the one on screen
        // is read each frame.
        try
        {
            ReadFixedFacts();
            RefreshSystemSettings();
            RefreshMounts();
            RefreshUsers();
            RefreshNetwork();
            RefreshMemory();
            RefreshDisplay();
        }
        catch (Exception error)
        {
            _refreshing = false;
            Shell.ReportFailure("The dashboard could not be filled in", error);
        }

        _sinceSlowRefresh = 0;
    }

    /// <inheritdoc/>
    public override void Tick(FrameContext context)
    {
        if (!_refreshing)
            return;

        try
        {
            // Only the tab on screen is read, so the page is not making user, network, memory and six
            // directory reads every frame for rows nobody is looking at. Switching tab reads the new one
            // at once; the slow-changing tabs otherwise refresh on the timer.
            int tab = _tabs.SelectedIndex;
            bool switched = tab != _lastTab;
            _lastTab = tab;

            _sinceSlowRefresh += context.DeltaSeconds;
            bool slow = _sinceSlowRefresh >= SlowRefreshSeconds;
            if (slow)
                _sinceSlowRefresh = 0;

            RefreshTab(tab, slow || switched, context.DeltaSeconds);
        }
        catch (Exception error)
        {
            // Each reading already turns a refusal into a value, so anything arriving here is outside
            // them. Stop refreshing rather than repeating the same failure sixty times a second; what
            // is on screen stays readable.
            _refreshing = false;
            Shell.ReportFailure("The dashboard stopped updating", error);
        }
    }

    /// <inheritdoc/>
    protected override void OnDispose()
    {
        _network?.Dispose();
        _network = null;
    }

    private UiElement BuildConsoleTab()
        => new StackPanel()
            .Add(_systemSoftware)
            .Add(_allowedSdk)
            .Add(_consoleId)
            .Add(_processors)
            .Add(_supportedBuild)
            .Add(_supportedRange)
            .Add(new Separator())
            .Add(_systemName)
            .Add(_language)
            .Add(_dateFormat)
            .Add(_timeFormat)
            .Add(_timeZone)
            .Add(_summerTime);

    private UiElement BuildUsersTab()
    {
        var panel = new StackPanel()
            .Add(_signedInAs)
            .Add(_signedInId)
            .Add(_profileCount)
            .Add(new Separator());
        foreach (KeyValueRow row in _profiles)
            panel.Add(row);
        return panel;
    }

    private UiElement BuildNetworkTab()
        => new StackPanel()
            .Add(_netState)
            .Add(_networkSpinner)
            .Add(_netLink)
            .Add(_netAddress)
            .Add(_netMask)
            .Add(_netGateway)
            .Add(_netDns)
            .Add(new Separator())
            .Add(_netName)
            .Add(_netMac)
            .Add(_netSignal)
            .Add(_netMtu);

    private UiElement BuildMemoryTab()
        => new StackPanel()
            .Add(_heapInUse)
            .Add(_heapAllocated)
            .Add(_heapCeiling)
            .Add(_heapPressure)
            .Add(_heapCollections)
            .Add(new Separator())
            .Add(_flexibleAvailable)
            .Add(_largestDirect)
            .Add(new Separator())
            .Add(new Label("Heap pressure") { TextColor = Shell.Theme.TextMuted })
            .Add(_pressureGauge);

    private UiElement BuildStorageTab()
    {
        var panel = new StackPanel()
            .Add(new Label("The application writes to /data. The package is mounted read-only at /app0.")
            {
                TextColor = Shell.Theme.TextMuted,
            })
            .Add(new Separator());
        foreach (KeyValueRow row in _mounts)
            panel.Add(row);
        return panel;
    }

    private UiElement BuildDisplayTab()
        => new StackPanel()
            .Add(_safeArea)
            .Add(_inBackground)
            .Add(_systemOverlay)
            .Add(new Separator())
            .Add(new Label("Safe area") { TextColor = Shell.Theme.TextMuted })
            .Add(_safeAreaGauge);

    private void OpenNetwork()
    {
        try
        {
            _network = NetworkInfo.Open();
        }
        catch (ProsperoException error)
        {
            // Reported once. The Network tab says so itself for as long as the page is open, and
            // repeating it on every return to the page would be noise.
            _networkReported = true;
            Shell.ReportFailure("Network status is not available", error);
        }
    }

    // Read once: the software version, the identifier and the core count belong to the machine, and
    // the supported range belongs to this build. None of them move while the module runs.
    private void ReadFixedFacts()
    {
        _systemSoftware.Value = Read(() => SystemInfo.SystemSoftwareVersion);
        _allowedSdk.Value = Read(() => Describe(FirmwareSupport.AllowedSdkVersion));
        _consoleId.Value = Read(() => SystemInfo.ConsoleId);
        _processors.Value = Read(() => SystemInfo.ProcessorCount.ToString());
        _supportedBuild.Value = Read(() => YesNo(FirmwareSupport.IsSupported));
        _supportedRange.Value = Read(() => FirmwareSupport.SupportedRange.ToString());
    }

    // Reads the tab on screen. The live tabs are read every frame they are shown; the console's
    // user-settings group and the storage mounts change only when the user goes and changes them, so
    // they are read on the slow timer (or at once on a switch to the tab) rather than every frame.
    private void RefreshTab(int tab, bool slow, double deltaSeconds)
    {
        switch (tab)
        {
            case 0:
                if (slow)
                    RefreshSystemSettings();
                break;
            case 1:
                RefreshUsers();
                break;
            case 2:
                RefreshNetwork();
                _networkSpinner.Advance((float)deltaSeconds);
                break;
            case 3:
                RefreshMemory();
                break;
            case 4:
                if (slow)
                    RefreshMounts();
                break;
            case 5:
                RefreshDisplay();
                break;
            default:
                break;
        }
    }

    private void RefreshSystemSettings()
    {
        _systemName.Value = Read(() => SystemParameters.SystemName);
        _language.Value = Read(() => Describe(SystemParameters.Language));
        _dateFormat.Value = Read(() => Describe(SystemParameters.DateFormat));
        _timeFormat.Value = Read(() => Describe(SystemParameters.TimeFormat));
        _timeZone.Value = Read(() => DescribeOffset(SystemParameters.TimeZoneMinutes));
        _summerTime.Value = Read(() => YesNo(SystemParameters.IsSummerTime));
    }

    // Listing a mount to count its entries walks the whole directory, so it is done only for the storage
    // tab while it is on screen rather than for every mount every second.
    private void RefreshMounts()
    {
        for (int i = 0; i < _mounts.Length; i++)
            _mounts[i].Value = DescribeMount(MountPoints[i]);
    }

    private void RefreshUsers()
    {
        _signedInAs.Value = Read(() => Users.InitialUserName);
        _signedInId.Value = Read(() => Users.InitialUserId.ToString());

        // A refused list and an empty one mean different things to the reader, so they are kept apart.
        IReadOnlyList<UserProfile>? profiles = ReadProfiles();
        _profileCount.Value = profiles is null ? Unavailable : Count(profiles.Count, "profile");

        int shown = profiles?.Count ?? 0;
        for (int i = 0; i < _profiles.Length; i++)
        {
            bool present = i < shown;
            _profiles[i].Visible = present;
            if (!present)
                continue;
            _profiles[i].Name = "Slot " + (i + 1);
            _profiles[i].Value = $"{profiles![i].Name} ({profiles[i].Id})";
        }
    }

    private void RefreshNetwork()
    {
        if (_network is null)
        {
            SetNetworkRows(Unavailable);
            _networkSpinner.Visible = false;
            return;
        }

        try
        {
            NetCtlState state = _network.State;
            bool connected = state == NetCtlState.IpObtained;

            // The device kind is only meaningful once the link is up, and the wireless fields only on a
            // wireless link, so an unconnected console reports a state rather than a column of blanks
            // that look like failures.
            bool wireless = connected && _network.Device == NetCtlDevice.Wireless;

            // The ring turns only while the link is still coming up, and stops once it is connected or
            // reported down.
            _networkSpinner.Visible = state is NetCtlState.Connecting or NetCtlState.IpObtaining;

            _netState.Value = Describe(state);
            _netLink.Value = connected ? (wireless ? "wireless" : "wired") : NotApplicable;
            _netAddress.Value = connected ? Or(_network.IpAddress, NotApplicable) : NotApplicable;
            _netMask.Value = connected ? Or(_network.SubnetMask, NotApplicable) : NotApplicable;
            _netGateway.Value = connected ? Or(_network.DefaultGateway, NotApplicable) : NotApplicable;
            _netDns.Value = connected ? Or(_network.PrimaryDns, NotApplicable) : NotApplicable;
            _netName.Value = wireless ? Or(_network.Ssid, NotApplicable) : NotApplicable;
            _netMac.Value = Or(_network.MacAddress, NotApplicable);
            _netSignal.Value = wireless ? _network.SignalStrength + "%" : NotApplicable;
            _netMtu.Value = connected ? _network.Mtu + " bytes" : NotApplicable;
        }
        catch (ProsperoException)
        {
            SetNetworkRows(Unavailable);
            _networkSpinner.Visible = false;

            // Every reading goes through this one handle, so once the service behind it has gone the
            // rows would say unavailable for as long as the page lives. Letting the handle go is what
            // lets a fresh one be started the next time the page comes to the front.
            _network?.Dispose();
            _network = null;
        }
    }

    private void RefreshMemory()
    {
        HeapSnapshot heap = HeapMonitor.Capture();
        _heapInUse.Value = TextFormat.ByteSize(heap.HeapSizeBytes);
        _heapAllocated.Value = TextFormat.ByteSize(heap.TotalAllocatedBytes);

        // A ceiling of zero is the collector saying it was never given one, not a ceiling of nothing.
        _heapCeiling.Value = heap.HardLimitBytes > 0 ? TextFormat.ByteSize(heap.HardLimitBytes) : "unset";
        _heapPressure.Value = Percent(heap.Pressure);
        _pressureGauge.Value = (float)heap.Pressure;
        _heapCollections.Value = heap.CollectionCount.ToString();

        _flexibleAvailable.Value = Read(() => TextFormat.ByteSize((long)SystemMemory.AvailableFlexibleBytes()));
        _largestDirect.Value = Read(() => TextFormat.ByteSize((long)SystemMemory.LargestFreeDirectBytes()));
    }

    private void RefreshDisplay()
    {
        // The ratio is read once for both the row and the dial. The dial is hidden when the reading is
        // refused so it does not sit at nothing as if the safe area were empty.
        try
        {
            float ratio = SystemControl.DisplaySafeAreaRatio;
            _safeArea.Value = Percent(ratio);
            _safeAreaGauge.Value = ratio;
            _safeAreaGauge.Visible = true;
        }
        catch (ProsperoException)
        {
            _safeArea.Value = Unavailable;
            _safeAreaGauge.Visible = false;
        }

        // One call carries both flags, so the status is read once rather than once per row.
        try
        {
            SystemStatus status = SystemControl.GetStatus();
            _inBackground.Value = YesNo(status.IsInBackground);
            _systemOverlay.Value = YesNo(status.IsSystemUiOverlaid);
        }
        catch (ProsperoException)
        {
            _inBackground.Value = Unavailable;
            _systemOverlay.Value = Unavailable;
        }
    }

    private void SetNetworkRows(string value)
    {
        _netState.Value = value;
        _netLink.Value = value;
        _netAddress.Value = value;
        _netMask.Value = value;
        _netGateway.Value = value;
        _netDns.Value = value;
        _netName.Value = value;
        _netMac.Value = value;
        _netSignal.Value = value;
        _netMtu.Value = value;
    }

    /// <summary>The signed-in profiles, or null when the user service refused the list.</summary>
    private static IReadOnlyList<UserProfile>? ReadProfiles()
    {
        try
        {
            return Users.LoggedInUsers;
        }
        catch (ProsperoException)
        {
            return null;
        }
    }

    private static string DescribeMount(string path)
    {
        try
        {
            if (!FileSystem.Exists(path))
                return "not mounted";
            if (!FileSystem.IsDirectory(path))
                return "present";
            int count = FileSystem.EnumerateDirectory(path).Count;
            return count == 0 ? "mounted, empty" : "mounted, " + Count(count, "entry", "entries");
        }
        catch (ProsperoException)
        {
            return Unavailable;
        }
    }

    /// <summary>Runs <paramref name="read"/>, turning a refused service call into a readable value.</summary>
    private static string Read(Func<string> read)
    {
        try
        {
            string value = read();
            return string.IsNullOrEmpty(value) ? Unavailable : value;
        }
        catch (ProsperoException)
        {
            return Unavailable;
        }
    }

    private static string Or(string value, string fallback) => string.IsNullOrEmpty(value) ? fallback : value;

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static string Percent(double fraction) => (fraction * 100).ToString("F0") + "%";

    private static string Count(int count, string singular, string? plural = null)
        => count == 1 ? "1 " + singular : count + " " + (plural ?? singular + "s");

    // An absent version prints as an empty string, which would read as a blank row rather than as a
    // system that declined to answer.
    private static string Describe(FirmwareVersion version) => version.HasValue ? version.ToString() : Unavailable;

    private static string Describe(NetCtlState state) => state switch
    {
        NetCtlState.Disconnected => "not connected",
        NetCtlState.Connecting => "connecting",
        NetCtlState.IpObtaining => "obtaining an address",
        NetCtlState.IpObtained => "connected",
        _ => Unavailable,
    };

    private static string Describe(DateFormat format) => format switch
    {
        DateFormat.YearMonthDay => "year, month, day",
        DateFormat.DayMonthYear => "day, month, year",
        DateFormat.MonthDayYear => "month, day, year",
        _ => Unavailable,
    };

    private static string Describe(TimeFormat format) => format switch
    {
        TimeFormat.TwelveHour => "12-hour",
        TimeFormat.TwentyFourHour => "24-hour",
        _ => Unavailable,
    };

    // The language names are written out rather than left as the identifier, since this row is read by
    // the user rather than by a tool.
    private static string Describe(SystemLanguage language) => language switch
    {
        SystemLanguage.Japanese => "Japanese",
        SystemLanguage.EnglishUS => "English (United States)",
        SystemLanguage.French => "French",
        SystemLanguage.Spanish => "Spanish",
        SystemLanguage.German => "German",
        SystemLanguage.Italian => "Italian",
        SystemLanguage.Dutch => "Dutch",
        SystemLanguage.PortuguesePortugal => "Portuguese (Portugal)",
        SystemLanguage.Russian => "Russian",
        SystemLanguage.Korean => "Korean",
        SystemLanguage.ChineseTraditional => "Chinese (traditional)",
        SystemLanguage.ChineseSimplified => "Chinese (simplified)",
        SystemLanguage.Finnish => "Finnish",
        SystemLanguage.Swedish => "Swedish",
        SystemLanguage.Danish => "Danish",
        SystemLanguage.Norwegian => "Norwegian",
        SystemLanguage.Polish => "Polish",
        SystemLanguage.PortugueseBrazil => "Portuguese (Brazil)",
        SystemLanguage.EnglishUK => "English (United Kingdom)",
        SystemLanguage.Turkish => "Turkish",
        SystemLanguage.SpanishLatinAmerica => "Spanish (Latin America)",
        SystemLanguage.Arabic => "Arabic",
        SystemLanguage.FrenchCanada => "French (Canada)",
        SystemLanguage.Czech => "Czech",
        SystemLanguage.Hungarian => "Hungarian",
        SystemLanguage.Greek => "Greek",
        SystemLanguage.Romanian => "Romanian",
        SystemLanguage.Thai => "Thai",
        SystemLanguage.Vietnamese => "Vietnamese",
        SystemLanguage.Indonesian => "Indonesian",
        _ => "language " + (int)language,
    };

    private static string DescribeOffset(int minutes)
    {
        int magnitude = Math.Abs(minutes);
        return $"UTC{(minutes < 0 ? '-' : '+')}{magnitude / 60:D2}:{magnitude % 60:D2}";
    }
}

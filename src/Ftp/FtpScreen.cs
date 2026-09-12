// Prospero Explorer
// Copyright (C) 2026 SvenGDK

using ProsperoExplorer.Shell;
using SharpProspero.Application;
using SharpProspero.Interop;
using SharpProspero.Platform;
using SharpProspero.Ui;
using System;
using System.Collections.Generic;
using System.Text;

namespace ProsperoExplorer.Ftp;

/// <summary>
/// The page for the file service: whether it is listening, what to type into a client to reach it, how
/// many clients are connected, and what they have been doing.
///
/// The page owns none of that. The application owns the service and advances it every frame, so this
/// page only reads it: the service keeps running while the user is elsewhere, and coming back here
/// shows what happened in the meantime.
/// </summary>
internal sealed class FtpScreen : ExplorerScreen
{
    /// <summary>
    /// How many frames pass between readings of the network state. Opening the network service is not
    /// free and the address changes rarely, so twice a second is plenty.
    /// </summary>
    private const int NetworkReadInterval = 30;

    private readonly FtpService _service;
    private readonly KeyValueRow _stateRow = new("Service", "stopped");
    private readonly KeyValueRow _addressRow = new("Address", "");
    private readonly KeyValueRow _clientsRow = new("Clients", "none connected");
    private readonly KeyValueRow _loginRow = new("Sign in", "");
    private readonly KeyValueRow _folderRow = new("Folder", "/");
    private readonly KeyValueRow _writingRow = new("Writing", "");
    private readonly Button _toggle;
    private readonly Label _warning;
    private readonly Label _security;
    private readonly TextBlock _activity = new(NoActivity);

    private const string NoActivity = "Nothing yet.";

    private string _ipAddress = "";
    private bool _networkUp;
    private bool _networkRead;
    private int _sinceNetworkRead;
    private int _activityCountShown = -1;
    private string? _activityNewestShown;
    private bool _insecureStartConfirmed;

    /// <summary>Creates the page over the service the application owns.</summary>
    public FtpScreen(ExplorerShell shell, FtpService service) : base(shell)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
        _toggle = new Button("Start the service", Toggle);
        _warning = new Label("") { TextColor = shell.Theme.Accent, Visible = false };
        _security = new Label("") { TextColor = shell.Theme.Accent, Visible = false };
    }

    /// <inheritdoc/>
    public override string Title => "File service";

    /// <inheritdoc/>
    public override string Hint => "Cross starts or stops, Circle goes back.";

    /// <inheritdoc/>
    protected override UiElement BuildRoot()
        => new StackPanel()
            .Add(new Label("Reach the whole file system from a computer on the same network. Type the address below into any client that speaks the file transfer protocol.")
            {
                TextColor = Shell.Theme.TextMuted,
            })
            .Add(_warning)
            .Add(_security)
            .Add(new Separator())
            .Add(_stateRow)
            .Add(_addressRow)
            .Add(_clientsRow)
            .Add(_loginRow)
            .Add(_folderRow)
            .Add(_writingRow)
            .Add(new Separator())
            .Add(_toggle)
            .Add(new Label("The port, the sign-in and the folder are set in Settings.")
            {
                TextColor = Shell.Theme.TextMuted,
            })
            .Add(new Separator())
            .Add(new Label("Recent activity"))
            .Add(new ScrollView(_activity) { ViewHeight = 280 });

    /// <inheritdoc/>
    public override void OnShown()
    {
        // Coming back to the page, the address on screen may be stale; read it before the first draw.
        _networkRead = false;
        Refresh();
    }

    /// <inheritdoc/>
    public override void Tick(FrameContext context) => Refresh();

    private void Refresh()
    {
        ReadNetwork();
        ShowService();
        ShowActivity();
    }

    private void ReadNetwork()
    {
        if (_networkRead && ++_sinceNetworkRead < NetworkReadInterval)
            return;
        _sinceNetworkRead = 0;
        _networkRead = true;

        try
        {
            using NetworkInfo info = NetworkInfo.Open();
            _networkUp = info.IsConnected;
            _ipAddress = _networkUp ? info.IpAddress : "";
        }
        catch (ProsperoException)
        {
            // The network service could not be asked. Treat that the same as no connection: the page
            // says the service cannot be reached, which is what the user needs to know either way.
            _networkUp = false;
            _ipAddress = "";
        }
    }

    private void ShowService()
    {
        AppSettings settings = Shell.Settings;
        bool running = _service.IsRunning;
        int port = running ? _service.Port : settings.FtpPort;

        _stateRow.Value = running ? $"listening on port {port}" : "stopped";
        _addressRow.Value = _networkUp && _ipAddress.Length > 0
            ? $"ftp://{_ipAddress}:{port}"
            : "no network connection";
        _clientsRow.Value = _service.ClientCount switch
        {
            0 => "none connected",
            1 => "one connected",
            int count => $"{count} connected",
        };
        _loginRow.Value = settings.FtpRequireLogin ? $"required, as {settings.FtpUser}" : "not required";
        _folderRow.Value = string.IsNullOrWhiteSpace(settings.FtpRoot) ? "/" : settings.FtpRoot;
        _writingRow.Value = settings.FtpAllowWrite ? "allowed" : "off, clients can only read";
        _toggle.Text = running ? "Stop the service" : "Start the service";

        _warning.Visible = !_networkUp;
        if (!_networkUp)
        {
            _warning.Text = running
                ? "The network is not connected, so nothing can reach the service."
                : "The network is not connected. Connect it in the system settings, then start the service.";
        }

        // A standing notice while the settings leave the service wide open, so the risk is on the page
        // before the service is started and for as long as it runs.
        bool wideOpen = IsConfiguredWideOpen();
        _security.Visible = wideOpen;
        if (wideOpen)
        {
            _security.Text = "No sign-in, the whole file system in reach and writing on: while the service runs, anyone on this network can read, change and delete any file without a password.";
        }
    }

    // The list only changes when the service records something, so the text is rebuilt then rather
    // than on every frame.
    private void ShowActivity()
    {
        IReadOnlyList<string> lines = _service.Activity;
        string? newest = lines.Count > 0 ? lines[0] : null;
        if (lines.Count == _activityCountShown && ReferenceEquals(newest, _activityNewestShown))
            return;

        _activityCountShown = lines.Count;
        _activityNewestShown = newest;

        if (lines.Count == 0)
        {
            _activity.Text = NoActivity;
            return;
        }

        var text = new StringBuilder(lines.Count * 48);
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0)
                text.Append('\n');
            text.Append(lines[i]);
        }
        _activity.Text = text.ToString();
    }

    private void Toggle()
    {
        if (_service.IsRunning)
        {
            AskToStop();
            return;
        }

        // Starting the service while it reaches the whole file system with no sign-in and writing on
        // opens every file on the console to the network. That is a deliberate configuration and is
        // not blocked, but the risk is put in front of the user at the moment it takes effect.
        if (ShouldWarnBeforeStart())
        {
            Shell.Dialogs.Confirm(
                "Anyone on this network will be able to read, change and delete any file on the console without a password. Start the service anyway?",
                confirmed =>
                {
                    if (!confirmed)
                        return;
                    _insecureStartConfirmed = true;
                    StartService();
                });
            return;
        }

        StartService();
    }

    private void StartService()
    {
        try
        {
            _service.Start();
        }
        catch (Exception error)
        {
            // The user asked for this and nothing else will happen, so the reason gets a box rather
            // than a line that slides away.
            Shell.ReportFailure("The file service could not start", error, important: true);
            Refresh();
            return;
        }

        Shell.Notify(_networkUp && _ipAddress.Length > 0
            ? $"File service listening at ftp://{_ipAddress}:{_service.Port}."
            : $"File service listening on port {_service.Port}.");
        Refresh();
    }

    // True when the settings let the service reach the whole file system with no sign-in and writing
    // on, which lets anyone on the network read, change and delete any file the module can reach.
    private bool IsConfiguredWideOpen()
    {
        AppSettings settings = Shell.Settings;
        return !settings.FtpRequireLogin
            && settings.FtpAllowWrite
            && FtpService.RealRoot(settings.FtpRoot).Length == 0;
    }

    // The confirmation is asked once a session, and only when confirmations are on, so a user who has
    // chosen to run the service wide open is not asked again each time they start it.
    private bool ShouldWarnBeforeStart()
        => !_insecureStartConfirmed && Shell.Settings.ConfirmDestructive && IsConfiguredWideOpen();

    private void AskToStop()
    {
        int clients = _service.ClientCount;
        if (clients == 0 || !Shell.Settings.ConfirmDestructive)
        {
            StopService();
            return;
        }

        string who = clients == 1 ? "One client is" : $"{clients} clients are";
        Shell.Dialogs.Confirm($"{who} connected. Stop the service and disconnect them?", yes =>
        {
            if (yes)
                StopService();
        });
    }

    private void StopService()
    {
        try
        {
            _service.Stop();
            Shell.Notify("File service stopped.");
        }
        catch (Exception error)
        {
            Shell.ReportFailure("The file service could not be stopped", error);
        }
        Refresh();
    }

    // The service outlives this page - the application owns it - so nothing is released here.
}

// Prospero Explorer
// Copyright (C) 2026 SvenGDK

using ProsperoExplorer.Shell;
using SharpProspero.Application;
using SharpProspero.Interop;
using SharpProspero.Interop.Dialog;
using SharpProspero.Interop.Net;
using SharpProspero.Platform;
using SharpProspero.Text;
using SharpProspero.Ui;
using System;
using System.Collections.Generic;

namespace ProsperoExplorer.Web;

/// <summary>
/// Opening a web address in the system browser. The user types an address on the on-screen keyboard,
/// this checks it over, hands it to the browser and reports when the browser comes back. Addresses
/// opened during the run are kept as buttons that reopen one without typing it again.
/// </summary>
internal sealed class WebScreen : ExplorerScreen
{
    /// <summary>How many addresses the run remembers. Beyond this the oldest is dropped.</summary>
    private const int RecentLimit = 8;

    /// <summary>How often the connection is read again, in seconds.</summary>
    private const double NetworkCheckSeconds = 2d;

    /// <summary>The longest address the keyboard accepts, in characters.</summary>
    private const int AddressLength = 512;

    private readonly List<string> _recent = [];
    private readonly Button[] _recentButtons = new Button[RecentLimit];

    private readonly Button _homeButton = new("Open the home address");
    private readonly Label _recentHeader = new("Opened this run") { Visible = false };
    private readonly Separator _recentDivider = new() { Visible = false };
    private readonly Label _offlineNote = new("The network is not up. An address can still be tried.") { Visible = false };
    private readonly KeyValueRow _networkRow = new("Network", "checking");
    private readonly KeyValueRow _homeRow = new("Home address", "not set");

    // The connection is read through one open handle rather than opening the service for every look,
    // and only every couple of seconds, because the state does not change faster than a user reads it.
    private NetworkInfo? _network;
    private double _sinceNetworkCheck = NetworkCheckSeconds;
    private bool _networkUp;
    private string _networkState = "checking";

    private string _homeShown = "";
    private string? _homeAddress;
    private string? _lastOpened;

    private bool _browserOpen;
    private bool _browserShown;
    private double _openSeconds;

    public WebScreen(ExplorerShell shell) : base(shell)
    {
        for (int i = 0; i < _recentButtons.Length; i++)
        {
            int slot = i;
            _recentButtons[i] = new Button("", () => ReopenSlot(slot)) { Visible = false };
        }

        _offlineNote.TextColor = shell.Theme.TextMuted;
        _recentHeader.TextColor = shell.Theme.TextMuted;
        _homeButton.Activated = OpenHome;
        RefreshHome();
    }

    public override string Title => "Web address";

    public override string Hint => "Cross opens, Circle goes back.";

    protected override UiElement BuildRoot()
    {
        var menu = new ScrollMenu { ViewHeight = 520 };
        menu.Add(new Button("Enter an address", Ask));
        menu.Add(_homeButton);
        menu.Add(_recentDivider);
        menu.Add(_recentHeader);
        foreach (Button button in _recentButtons)
            menu.Add(button);
        menu.Add(new Separator());
        menu.Add(new Button("Set as the home address", SetHome));
        menu.Add(new Button("Forget the addresses opened this run", ClearRecent));

        return new StackPanel()
            .Add(new Label("The address is opened in the system browser, over this application.")
            {
                TextColor = Shell.Theme.TextMuted,
            })
            .Add(_offlineNote)
            .Add(new Separator())
            .Add(_networkRow)
            .Add(_homeRow)
            .Add(new Separator())
            .Add(menu);
    }

    public override void OnShown()
    {
        RefreshNetwork();
        RefreshHome();
    }

    public override void Tick(FrameContext context)
    {
        // The page keeps getting the frame while the browser is up, so the time it was open is counted
        // here rather than from a wall clock the application does not otherwise read. A browser that
        // truly opened is on screen as an overlay for at least a frame before it closes; one that could
        // not open never gets that far and its close is reported before any frame sees it. Watching for
        // the overlay is what tells a real close apart from a failure to open.
        if (_browserOpen)
        {
            if (Shell.Dialogs.IsBusy)
                _browserShown = true;
            _openSeconds += context.DeltaSeconds;
        }

        _sinceNetworkCheck += context.DeltaSeconds;
        if (_sinceNetworkCheck >= NetworkCheckSeconds)
            RefreshNetwork();

        if (!string.Equals(_homeShown, Shell.Settings.HomeUrl, StringComparison.Ordinal))
            RefreshHome();
    }

    /// <summary>Asks for an address, starting from the home address so it is one press to open it.</summary>
    private void Ask()
        => Shell.Dialogs.AskText(
            "Web address",
            Shell.Settings.HomeUrl,
            Entered,
            maxLength: AddressLength,
            type: ImeType.Url,
            placeholder: "https://");

    private void Entered(string? text)
    {
        if (text is null)
        {
            Shell.Status("Nothing was opened.");
            return;
        }

        if (!TryReadAddress(text, out string address, out bool schemeAdded, out string problem))
        {
            Shell.Dialogs.Alert(problem);
            return;
        }

        Open(address, schemeAdded);
    }

    private void OpenHome()
    {
        if (_homeAddress is null)
        {
            Shell.Status("No home address is set. Open an address, then set it as the home address.");
            return;
        }
        Open(_homeAddress, schemeAdded: false);
    }

    private void ReopenSlot(int slot)
    {
        if (slot < 0 || slot >= _recent.Count)
            return;
        Open(_recent[slot], schemeAdded: false);
    }

    /// <summary>
    /// Opens <paramref name="address"/>, first putting anything the user should know about it to them.
    /// A missing scheme was filled in on their behalf and a connection that is not up will most likely
    /// leave the browser with nothing to show, so both are said before the browser takes the screen,
    /// where a message on the strip underneath would not be read.
    /// </summary>
    private void Open(string address, bool schemeAdded)
    {
        List<string> notes = [];
        if (schemeAdded)
            notes.Add("The address had no scheme, so https was added.");
        if (!_networkUp)
            notes.Add($"The network is not up: {_networkState}.");

        if (notes.Count == 0)
        {
            Begin(address);
            return;
        }

        string message = string.Join("\n", notes) + $"\n\nOpen {address}?";
        Shell.Dialogs.Confirm(message, yes =>
        {
            if (yes)
                Begin(address);
            else
                Shell.Status("Nothing was opened.");
        });
    }

    private void Begin(string address)
    {
        Remember(address);
        _lastOpened = address;
        _openSeconds = 0d;
        _browserShown = false;
        _browserOpen = true;
        Shell.Status($"Opening {address}.");

        Shell.Dialogs.OpenBrowser(address, () =>
        {
            _browserOpen = false;

            // A browser that was never shown did not close; it failed to open - the module may be
            // absent, or the address refused. That is reported as the failure it is rather than as a
            // close that lasted no time.
            if (_browserShown)
                Shell.Notify($"The browser closed after {TextFormat.Duration(_openSeconds)}.");
            else
                Shell.Notify($"{address} could not be opened in the browser.");
        });
    }

    /// <summary>Writes the last address opened to the settings, so the next run starts from it.</summary>
    private void SetHome()
    {
        if (_lastOpened is null)
        {
            Shell.Status("Open an address first; the one you opened is the one that is kept.");
            return;
        }

        string address = _lastOpened;
        if (string.Equals(Shell.Settings.HomeUrl, address, StringComparison.Ordinal))
        {
            Shell.Status($"{address} is already the home address.");
            return;
        }

        if (Shell.Settings.ConfirmDestructive && _homeAddress is not null)
        {
            Shell.Dialogs.Confirm(
                $"Replace the home address?\n\n{_homeAddress}\nbecomes\n{address}",
                yes =>
                {
                    if (yes)
                        WriteHome(address);
                    else
                        Shell.Status("The home address was left as it was.");
                });
            return;
        }

        WriteHome(address);
    }

    private void WriteHome(string address)
    {
        Shell.Settings.HomeUrl = address;
        RefreshHome();
        if (Shell.Settings.Save())
            Shell.Notify($"The home address is now {address}.");
        else
            Shell.Status($"The home address is {address} for this run; the settings could not be written.");
    }

    private void ClearRecent()
    {
        if (_recent.Count == 0)
        {
            Shell.Status("No addresses have been opened this run.");
            return;
        }

        if (!Shell.Settings.ConfirmDestructive)
        {
            DropRecent();
            return;
        }

        Shell.Dialogs.Confirm(
            $"Forget the {_recent.Count} address(es) opened this run?",
            yes =>
            {
                if (yes)
                    DropRecent();
                else
                    Shell.Status("The addresses were kept.");
            });
    }

    private void DropRecent()
    {
        _recent.Clear();
        RefreshRecent();
        Shell.Notify("The addresses opened this run were forgotten.");
    }

    // Newest first, and an address opened twice moves back to the top rather than appearing twice.
    private void Remember(string address)
    {
        for (int i = _recent.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_recent[i], address, StringComparison.OrdinalIgnoreCase))
                _recent.RemoveAt(i);
        }

        _recent.Insert(0, address);
        while (_recent.Count > RecentLimit)
            _recent.RemoveAt(_recent.Count - 1);
        RefreshRecent();
    }

    // The buttons are made once and hidden when there is nothing for them to hold, because a menu's
    // children are fixed after it is built.
    private void RefreshRecent()
    {
        for (int i = 0; i < _recentButtons.Length; i++)
        {
            bool used = i < _recent.Count;
            _recentButtons[i].Visible = used;
            _recentButtons[i].Text = used ? _recent[i] : "";
        }

        bool any = _recent.Count > 0;
        _recentHeader.Visible = any;
        _recentDivider.Visible = any;
    }

    private void RefreshHome()
    {
        _homeShown = Shell.Settings.HomeUrl;
        _homeAddress = TryReadAddress(_homeShown, out string address, out _, out _) ? address : null;

        _homeButton.Enabled = _homeAddress is not null;
        _homeButton.Text = _homeAddress is not null
            ? $"Open the home address: {_homeAddress}"
            : "Open the home address - none is set";
        _homeRow.Value = _homeAddress ?? "not set";
    }

    private void RefreshNetwork()
    {
        _sinceNetworkCheck = 0d;
        try
        {
            _network ??= NetworkInfo.Open();
            NetCtlState state = _network.State;
            _networkUp = state == NetCtlState.IpObtained;
            _networkState = state switch
            {
                NetCtlState.IpObtained => $"connected as {AddressOrUnknown(_network.IpAddress)}",
                NetCtlState.IpObtaining => "connected, waiting for an address",
                NetCtlState.Connecting => "connecting",
                _ => "not connected",
            };
        }
        catch (ProsperoException error)
        {
            // The status service can refuse while the machine is bringing the link up. Let go of the
            // handle so the next look opens a fresh one instead of reusing one that has stopped
            // answering.
            _network?.Dispose();
            _network = null;
            _networkUp = false;
            _networkState = $"unavailable ({ExplorerShell.Describe(error)})";
        }

        _networkRow.Value = _networkState;
        _offlineNote.Visible = !_networkUp;
    }

    private static string AddressOrUnknown(string ipAddress)
        => ipAddress.Length > 0 ? ipAddress : "an unknown address";

    /// <summary>
    /// Checks what the user typed and puts it in the form the browser is handed.
    /// </summary>
    /// <param name="text">What came back from the keyboard.</param>
    /// <param name="address">The address to open, set only when this returns true.</param>
    /// <param name="schemeAdded">Whether https was filled in because the text carried no scheme.</param>
    /// <param name="problem">Why the text was refused, set only when this returns false.</param>
    private static bool TryReadAddress(string text, out string address, out bool schemeAdded, out string problem)
    {
        address = "";
        schemeAdded = false;
        problem = "";

        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            problem = "No address was entered.";
            return false;
        }

        foreach (char c in trimmed)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                problem = "An address cannot contain a space.";
                return false;
            }
        }

        int schemeLength = SchemeLength(trimmed);
        if (schemeLength < 0)
        {
            // Typing a bare host is what a keyboard invites, so the common case is filled in rather
            // than refused. The caller says so before the browser opens.
            address = "https://" + trimmed;
            schemeAdded = true;
        }
        else
        {
            string scheme = trimmed[..schemeLength];
            if (!scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                && !scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                problem = $"The browser opens http and https addresses only, and this one is {scheme}.";
                return false;
            }
            address = trimmed;
        }

        if (HostOf(address).Length == 0)
        {
            problem = $"{address} names no host to open.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// How many characters of <paramref name="text"/> are its scheme, or -1 when it carries none. A
    /// host with a service number after it - example.com:8080 - reads as a scheme by shape alone, so a
    /// colon followed by digits is taken as that number rather than as a scheme.
    /// </summary>
    private static int SchemeLength(string text)
    {
        int colon = text.IndexOf(':');
        if (colon <= 0)
            return -1;

        for (int i = 0; i < colon; i++)
        {
            char c = text[i];
            bool allowed = i == 0
                ? char.IsAsciiLetter(c)
                : char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.';
            if (!allowed)
                return -1;
        }

        if (colon + 1 < text.Length && char.IsAsciiDigit(text[colon + 1]))
            return -1;
        return colon;
    }

    private static string HostOf(string address)
    {
        int mark = address.IndexOf("://", StringComparison.Ordinal);
        if (mark < 0)
            return "";

        int start = mark + 3;
        int end = start;
        while (end < address.Length && address[end] is not ('/' or '?' or '#'))
            end++;
        return address[start..end];
    }

    protected override void OnDispose()
    {
        _network?.Dispose();
        _network = null;
    }
}

// Prospero Explorer
// Copyright (C) 2026 SvenGDK

using ProsperoExplorer.Browser;
using ProsperoExplorer.Dashboard;
using ProsperoExplorer.Ftp;
using ProsperoExplorer.Packages;
using ProsperoExplorer.Settings;
using ProsperoExplorer.Shell;
using ProsperoExplorer.Web;
using SharpProspero.Application;
using SharpProspero.Ui;
using System.Collections.Generic;

namespace ProsperoExplorer;

/// <summary>
/// The first page: one row per thing the application does. Everything else opens from here, and Circle
/// here leaves the application.
/// </summary>
internal sealed class HomeScreen : ExplorerScreen
{
    private readonly FtpService _ftp;
    private readonly KeyValueRow _serviceState = new("File service", "stopped");

    public HomeScreen(ExplorerShell shell, FtpService ftp) : base(shell) => _ftp = ftp;

    public override string Title => "Prospero Explorer";

    public override string Hint => "Cross opens, Circle leaves.";

    protected override UiElement BuildRoot()
    {
        var menu = new ScrollMenu { ViewHeight = 600 };

        // Browsing. The first entry opens wherever the settings say, when that can still be read, and
        // otherwise the first folder that can; a path fine on one run can be gone on the next, so it is
        // checked rather than trusted. Then one entry per folder this module can actually reach, worked
        // out by opening each rather than named here.
        Heading(menu, "Browse");
        menu.Add(new Button("Browse files", () =>
        {
            string? start = Places.StartingPoint(Shell.Settings.StartPath);
            if (start is null)
            {
                Shell.Dialogs.Alert("No folder can be read. Nothing this application is allowed to see answered.");
                return;
            }
            Shell.Push(new FileBrowserScreen(Shell, start));
        }));

        int readable = 0;
        foreach (PlaceProbe probe in Places.Probed)
        {
            if (!probe.Readable)
                continue;
            readable++;
            string path = probe.Place.Path;
            menu.Add(new Button($"{probe.Place.Name}   {path}", () => Shell.Push(new FileBrowserScreen(Shell, path))));
        }
        if (readable == 0)
            menu.Add(Muted("No folder could be read."));

        // Tools that act on the file system or the network.
        Heading(menu, "Tools");
        menu.Add(new Button("Install a package", () => Shell.Push(new PackageScreen(Shell))));
        menu.Add(new Button("File service (FTP)", () => Shell.Push(new FtpScreen(Shell, _ftp))));
        menu.Add(new Button("Open a web address", () => Shell.Push(new WebScreen(Shell))));

        // The machine and the application itself.
        Heading(menu, "System");
        menu.Add(new Button("System information", () => Shell.Push(new DashboardScreen(Shell))));
        menu.Add(new Button("Settings", () => Shell.Push(new SettingsScreen(Shell, _ftp))));
        menu.Add(new Button("Look for devices again", () =>
        {
            Places.Refresh();
            RebuildScreen();
            Shell.Notify($"{Places.Reachable.Count} folders can be read.");
        }));
        // Asks the companion daemon to grant this application the whole disk. A refusal or a missing
        // daemon is surfaced; a grant rescans and rebuilds this page so the newly reachable folders
        // appear in the Browse list above.
        menu.Add(new Button("Reach every folder", () =>
        {
            if (UnjailRequest.TryRequest(out string reason))
            {
                Places.Refresh();
                RebuildScreen();
                Shell.Notify($"The whole disk is reachable. {Places.Reachable.Count} folders can be read.");
            }
            else
            {
                Shell.Notify($"The file view was not widened: {reason}");
            }
        }));

        // The folders that refused, each with the reason it gave, kept to the bottom so they inform
        // without crowding out the actions. Without this a missing folder and one that is there but
        // will not open look the same from the sofa.
        IReadOnlyList<PlaceProbe> refused = Places.Unreachable;
        if (refused.Count > 0)
        {
            Heading(menu, "Could not be read");
            foreach (PlaceProbe probe in refused)
                menu.Add(Muted($"{probe.Place.Path}   -   {probe.Reason}"));
        }

        menu.Add(new Separator());
        menu.Add(new Button("Leave", Shell.RequestExit));

        return new StackPanel()
            .Add(Muted("Browse, view, unpack, play, install, and reach it all over the network."))
            .Add(menu)
            .Add(new Separator())
            .Add(_serviceState);
    }

    // A section heading inside the menu: a rule then an accented label, so the list reads as grouped
    // rather than as one long run.
    private void Heading(ScrollMenu menu, string text)
    {
        menu.Add(new Separator());
        menu.Add(new Label(text) { TextColor = Shell.Theme.Accent });
    }

    private Label Muted(string text) => new(text) { TextColor = Shell.Theme.TextMuted };

    public override void Tick(FrameContext context)
        => _serviceState.Value = _ftp.IsRunning
            ? $"listening on port {_ftp.Port}, {_ftp.ClientCount} connected"
            : "stopped";

    // Circle on the first page leaves the application, which is what the base does when nothing
    // handles it; saying so here keeps the intent in one place.
    protected override bool OnCancel() => false;
}

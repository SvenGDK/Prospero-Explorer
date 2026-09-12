// Prospero Explorer
// Copyright (C) 2026 SvenGDK

using ProsperoExplorer.Shell;
using SharpProspero.Application;
using SharpProspero.Interop.Pad;
using SharpProspero.Platform;
using SharpProspero.Storage;
using SharpProspero.Text;
using SharpProspero.Ui;
using System;
using System.Collections.Generic;

namespace ProsperoExplorer.Packages;

/// <summary>
/// Finds the packages sitting on the console and hands the selected one to the install service.
/// </summary>
/// <remarks>
/// The install service reports no progress of its own: it accepts a request and finishes the work on
/// its own afterwards. The page therefore says what it is doing at each step rather than moving a bar
/// that would be made up, and it says plainly that the install carries on once the request is taken.
/// </remarks>
internal sealed class PackageScreen : ExplorerScreen
{
    // A folder that is read is read whole, so only a couple are taken per frame: a slow removable drive
    // then costs a few frames of scanning instead of one long frame with nothing drawn.
    private const int FoldersPerFrame = 2;

    private const int NameColumn = 44;
    private const int FolderColumn = 30;

    private readonly List<FoundPackage> _found = [];
    private readonly List<string> _searchFolders = [];
    private readonly List<string> _unreadable = [];
    private readonly List<string> _deviceFolders = [];
    private readonly Queue<ScanTarget> _pending = new();

    private readonly ModalHost _root = new(new StackPanel());
    private readonly TextBlock _summary = new("Looking for packages.");
    private readonly KeyValueRow _devicesRow = new("Removable devices", "not checked yet");
    private readonly KeyValueRow _folderRow = new("Folder", "-");
    private readonly KeyValueRow _sizeRow = new("Size", "-");
    private readonly ListView _list = new();

    private UiElement? _menu;
    private PackageInstaller? _installer;
    private UsbStorage? _usb;
    private bool _started;
    private bool _scanning;

    /// <summary>Creates the page. Nothing is read until the page is shown.</summary>
    public PackageScreen(ExplorerShell shell) : base(shell)
    {
        _list.VisibleRows = shell.Settings.ListRows;
        _list.Activated = _ => InstallSelected();
        _list.SelectionChanged = _ => ShowSelection();
        _summary.TextColor = shell.Theme.TextMuted;
    }

    /// <inheritdoc />
    public override string Title => "Install a package";

    /// <inheritdoc />
    public override string Hint => "Cross installs, Triangle opens the menu, Circle goes back.";

    /// <inheritdoc />
    protected override UiElement BuildRoot()
    {
        _root.Content = new StackPanel()
            .Add(_summary)
            .Add(_devicesRow)
            .Add(new Separator())
            .Add(_list)
            .Add(new Separator())
            .Add(_folderRow)
            .Add(_sizeRow);
        return _root;
    }

    /// <inheritdoc />
    public override void OnShown()
    {
        if (_started)
            return;
        _started = true;
        RefreshDevices(announce: false);
        BeginScan();
    }

    /// <inheritdoc />
    public override void Tick(FrameContext context)
    {
        StepScan();

        // An overlay is answering the controller, so the page takes no button of its own.
        if (Shell.Dialogs.IsBusy)
            return;
        if (context.Pressed(ScePadButton.Triangle))
            ToggleMenu();
    }

    /// <inheritdoc />
    protected override bool OnCancel()
    {
        if (!_root.IsOpen)
            return false;
        _root.Close();
        return true;
    }

    /// <inheritdoc />
    protected override void OnDispose()
    {
        CloseInstaller();
        CloseUsb();
    }

    // The menu.

    private void ToggleMenu()
    {
        if (_root.IsOpen)
        {
            _root.Close();
            return;
        }
        _root.Show(_menu ??= BuildMenu());
    }

    private UiElement BuildMenu() => new StackPanel()
        .Add(new Label("Packages") { Scale = 3 })
        .Add(new Separator())
        .Add(new Button("Install the selected package", () => Run(InstallSelected)))
        .Add(new Button("Scan again", () => Run(BeginScan)))
        .Add(new Button("Check whether a title is installed", () => Run(AskAboutTitle)))
        .Add(new Button("Launch an installed title", () => Run(AskToLaunch)))
        .Add(new Button("Remove an installed title", () => Run(AskToUninstall)))
        .Add(new Button("Where it looks", () => Run(ShowSearchFolders)))
        .Add(new Button("Refresh removable devices", () => Run(RefreshDevicesAndRescan)))
        .Add(new Separator())
        .Add(new Button("Close this menu", () => _root.Close()));

    // Every menu item closes the menu first, so what it opens is not drawn behind a panel.
    private void Run(Action action)
    {
        _root.Close();
        action();
    }

    // Scanning.

    private void BeginScan()
    {
        _found.Clear();
        _unreadable.Clear();
        _pending.Clear();
        _searchFolders.Clear();

        foreach (string folder in Shell.Settings.PackageSearchFolders())
            AddSearchFolder(folder);

        // A drive the system mapped somewhere the settings do not name is still worth reading.
        foreach (string folder in _deviceFolders)
            AddSearchFolder(folder);

        foreach (string folder in _searchFolders)
            _pending.Enqueue(new ScanTarget(folder, 0));

        _scanning = _pending.Count > 0;
        _list.Clear();
        ShowSelection();
        _summary.Text = _scanning
            ? "Scanning."
            : "No folders to search. Set the search folders in Settings.";
    }

    private void AddSearchFolder(string folder)
    {
        if (folder.Length == 0)
            return;
        foreach (string known in _searchFolders)
        {
            if (string.Equals(known, folder, StringComparison.Ordinal))
                return;
        }
        _searchFolders.Add(folder);
    }

    private void StepScan()
    {
        if (!_scanning)
            return;

        for (int i = 0; i < FoldersPerFrame && _pending.Count > 0; i++)
            ReadFolder(_pending.Dequeue());

        if (_pending.Count > 0)
        {
            _summary.Text = $"Scanning. {_found.Count} found so far.";
            return;
        }

        _scanning = false;
        FinishScan();
    }

    private void ReadFolder(ScanTarget target)
    {
        IReadOnlyList<DirectoryEntry> entries;
        try
        {
            entries = FileSystem.EnumerateDirectory(target.Path);
        }
        catch (Exception)
        {
            // A folder that is absent, unmounted or closed to this process is skipped; the user is told
            // which ones were skipped rather than shown a failure per folder.
            _unreadable.Add(target.Path);
            return;
        }

        foreach (DirectoryEntry entry in entries)
        {
            if (entry.Name is "." or "..")
                continue;

            string full = PathUtil.Combine(target.Path, entry.Name);
            bool isDirectory;
            try
            {
                // A file system may leave the kind out of a directory record, and a name ending in the
                // package extension could still be a folder, so an unreported kind is asked for.
                isDirectory = entry.Type == FileEntryType.Unknown
                    ? FileSystem.IsDirectory(full)
                    : entry.IsDirectory;
            }
            catch (Exception)
            {
                continue;
            }

            if (isDirectory)
            {
                if (target.Depth == 0)
                    _pending.Enqueue(new ScanTarget(full, 1));
                continue;
            }

            if (FileKinds.Classify(full, isDirectory: false) != FileKind.Package)
                continue;

            long size;
            try
            {
                size = FileSystem.GetFileSize(full);
            }
            catch (Exception)
            {
                size = 0;
            }
            _found.Add(new FoundPackage(entry.Name, target.Path, full, size));
        }
    }

    private void FinishScan()
    {
        _found.Sort(static (left, right) =>
        {
            int byName = TextFormat.CompareNatural(left.Name, right.Name);
            return byName != 0 ? byName : string.CompareOrdinal(left.Folder, right.Folder);
        });

        _list.Clear();
        foreach (FoundPackage package in _found)
            _list.Add(Describe(package));
        _list.SelectedIndex = 0;
        ShowSelection();

        _summary.Text = _found.Count > 0
            ? $"{_found.Count} package{(_found.Count == 1 ? "" : "s")} found in {_searchFolders.Count} "
                + $"folder{(_searchFolders.Count == 1 ? "" : "s")}.{SkippedNote()}"
            : $"Nothing to install. Looked in {string.Join(", ", _searchFolders)}, and one level into "
                + $"each folder inside those.{SkippedNote()}";
    }

    private string SkippedNote()
        => _unreadable.Count == 0
            ? ""
            : $" {_unreadable.Count} folder{(_unreadable.Count == 1 ? " was" : "s were")} skipped "
                + "because they could not be read.";

    private static string Describe(FoundPackage package)
    {
        string name = Shorten(package.Name, NameColumn).PadRight(NameColumn);
        string size = TextFormat.ByteSize(package.Size).PadLeft(10);
        return $"{name} {size}  {Shorten(package.Folder, FolderColumn)}";
    }

    private void ShowSelection()
    {
        if (_found.Count == 0)
        {
            _folderRow.Value = "-";
            _sizeRow.Value = "-";
            return;
        }

        FoundPackage package = _found[Math.Clamp(_list.SelectedIndex, 0, _found.Count - 1)];
        _folderRow.Value = package.Folder;
        _sizeRow.Value = TextFormat.ByteSize(package.Size);
    }

    // Installing.

    private void InstallSelected()
    {
        if (_scanning)
        {
            Shell.Status("Still scanning. Wait for the list to settle.");
            return;
        }
        if (_found.Count == 0)
        {
            Shell.Status("There is nothing to install.");
            return;
        }

        FoundPackage package = _found[Math.Clamp(_list.SelectedIndex, 0, _found.Count - 1)];
        if (!Shell.Settings.ConfirmInstall)
        {
            BeginInstall(package);
            return;
        }

        Shell.Dialogs.Confirm(
            $"Install {package.Name}?\n\n{package.Folder}\n{TextFormat.ByteSize(package.Size)}",
            confirmed =>
            {
                if (confirmed)
                    BeginInstall(package);
                else
                    Shell.Status($"{package.Name} was left alone.");
            });
    }

    private void BeginInstall(FoundPackage package)
        => RunOnInstaller(
            $"Installing {package.Name}",
            $"Handing {package.Name} to the install service",
            $"{package.Name} could not be installed",
            installer => installer.Install(package.Path),
            () => Shell.Notify(
                $"{package.Name} was accepted. The system finishes the install on its own, and the "
                + "title appears on the home screen when it is done."));

    // Titles already on the console.

    private void AskAboutTitle()
        => Shell.Dialogs.AskText("Title id", "", entered =>
        {
            string? cleaned = Clean(entered);
            if (cleaned is null)
                return;

            string titleId = cleaned;
            bool exists = false;
            ulong size = 0;
            RunOnInstaller(
                "Checking",
                $"Asking about {titleId}",
                $"{titleId} could not be checked",
                installer =>
                {
                    exists = installer.AppExists(titleId);
                    if (exists)
                        size = installer.AppGetSize(titleId);
                },
                () => Shell.Dialogs.Alert(exists
                    ? $"{titleId} is installed.\n\nIt takes {TextFormat.ByteSize(ToLong(size))}."
                    : $"{titleId} is not installed."));
        }, maxLength: 32);

    private void AskToUninstall()
        => Shell.Dialogs.AskText("Title id to remove", "", entered =>
        {
            string? cleaned = Clean(entered);
            if (cleaned is null)
                return;

            string titleId = cleaned;
            if (!Shell.Settings.ConfirmDestructive)
            {
                BeginUninstall(titleId);
                return;
            }

            Shell.Dialogs.Confirm(
                $"Remove {titleId}?\n\nThe title and everything it saved go with it.",
                confirmed =>
                {
                    if (confirmed)
                        BeginUninstall(titleId);
                    else
                        Shell.Status($"{titleId} was left alone.");
                });
        }, maxLength: 32);

    private void BeginUninstall(string titleId)
        => RunOnInstaller(
            $"Removing {titleId}",
            $"Asking the install service to remove {titleId}",
            $"{titleId} could not be removed",
            installer => installer.Uninstall(titleId),
            () => Shell.Notify($"{titleId} was accepted for removal. The system finishes it on its own."));

    // Starting an installed title.

    private void AskToLaunch()
        => Shell.Dialogs.AskText("Title id to launch", "", entered =>
        {
            string? cleaned = Clean(entered);
            if (cleaned is null)
                return;

            string titleId = cleaned;
            if (titleId.Length != AppLauncher.TitleIdLength)
            {
                Shell.Status($"A title id is {AppLauncher.TitleIdLength} characters, like CUSA00000.");
                return;
            }

            // A launch replaces this application with the one started, so it is confirmed first: the
            // file explorer closes and does not come back on its own.
            Shell.Dialogs.Confirm(
                $"Launch {titleId}?\n\nThis closes the file explorer and starts that title.",
                confirmed =>
                {
                    if (confirmed)
                        LaunchTitle(titleId);
                    else
                        Shell.Status($"{titleId} was left alone.");
                });
        }, maxLength: AppLauncher.TitleIdLength);

    private void LaunchTitle(string titleId)
    {
        try
        {
            // A launch that takes replaces this application with the one started and does not return
            // here, so the line after it runs only when the system declined to start the title.
            AppLauncher.Launch(titleId);
            Shell.Status($"{titleId} did not start.");
        }
        catch (Exception error)
        {
            ReportPlatformFailure($"{titleId} could not be launched", error);
        }
    }

    // Where the packages are looked for.

    private void ShowSearchFolders()
    {
        string looked = _searchFolders.Count > 0
            ? string.Join("\n", _searchFolders)
            : "nothing is set";
        string skipped = _unreadable.Count > 0
            ? $"\n\nCould not be read:\n{string.Join("\n", _unreadable)}"
            : "";
        Shell.Dialogs.Alert(
            $"Each of these is read, and so is every folder one level inside it:\n\n{looked}{skipped}"
            + "\n\nChange the list in Settings.");
    }

    // Removable devices.

    private void RefreshDevicesAndRescan()
    {
        RefreshDevices(announce: true);
        BeginScan();
    }

    private void RefreshDevices(bool announce)
    {
        _deviceFolders.Clear();
        int attached;
        int unmapped = 0;
        try
        {
            UsbStorage usb = _usb ??= UsbStorage.Open();
            IReadOnlyList<UsbDevice> devices = usb.ListDevices();
            attached = devices.Count;
            foreach (UsbDevice device in devices)
            {
                if (device.MountPath.Length == 0)
                    unmapped++;
                else
                    _deviceFolders.Add(device.MountPath);
            }
        }
        catch (Exception error)
        {
            // The storage service is not part of what the module links against, so a system without it
            // simply has none. The listed folders are still read, which is where a drive usually is.
            CloseUsb();
            _devicesRow.Value = "cannot be listed; the search folders are still read";
            if (announce)
                ReportPlatformFailure("The removable devices could not be listed", error);
            return;
        }

        _devicesRow.Value = attached == 0
            ? "none attached"
            : _deviceFolders.Count > 0
                ? $"{attached} attached at {string.Join(", ", _deviceFolders)}"
                : $"{attached} attached, none mapped yet";

        if (!announce)
            return;
        if (unmapped > 0)
            OfferToMap();
        else
            Shell.Status(_devicesRow.Value);
    }

    private void OfferToMap()
        => Shell.Dialogs.Confirm(
            "A drive is attached but the system has not mapped it anywhere.\n\nAsk it to map the drive?",
            confirmed =>
            {
                if (!confirmed || _usb is null)
                    return;
                try
                {
                    _usb.RequestMap();
                    Shell.Notify("The system was asked to map the drive. Refresh again once it appears.");
                }
                catch (Exception error)
                {
                    ReportPlatformFailure("The drive could not be mapped", error);
                }
            });

    // Talking to the install service.

    private void RunOnInstaller(
        string caption,
        string busyMessage,
        string failureWhat,
        Action<PackageInstaller> work,
        Action succeeded)
    {
        int step = 0;
        Exception? failure = null;

        Shell.Dialogs.RunWithProgress(caption, dialog =>
        {
            // The service answers nothing until it is done, so each step names what is happening instead
            // of moving a bar. Loading the service and making the request are taken on separate frames
            // so the message for each one is on screen before the work behind it starts.
            switch (step)
            {
                case 0:
                    dialog.SetProgressMessage("Reaching the install service");
                    step = 1;
                    return false;

                case 1:
                    try
                    {
                        OpenInstaller();
                    }
                    catch (Exception error)
                    {
                        failure = error;
                        return true;
                    }
                    dialog.SetProgressMessage(busyMessage);
                    step = 2;
                    return false;

                default:
                    try
                    {
                        work(_installer!);
                    }
                    catch (Exception error)
                    {
                        failure = error;
                    }
                    return true;
            }
        },
        () =>
        {
            if (failure is null)
                succeeded();
            else
                ReportPlatformFailure(failureWhat, failure);
        });
    }

    private void OpenInstaller() => _installer ??= PackageInstaller.Open();

    private void CloseInstaller()
    {
        try
        {
            _installer?.Dispose();
        }
        catch (Exception)
        {
            // The service is being let go either way; a failure shutting it down changes nothing.
        }
        _installer = null;
    }

    private void CloseUsb()
    {
        try
        {
            _usb?.Dispose();
        }
        catch (Exception)
        {
            // As above: the handle is going away regardless.
        }
        _usb = null;
    }

    // A platform failure carries a code the system has its own wording for, in the user's language, so
    // the reason goes on the strip and the system's box is offered for the code behind it.
    private void ReportPlatformFailure(string what, Exception error)
    {
        Shell.ReportFailure(what, error);

        int code = ExplorerShell.CodeOf(error);
        if (code == 0)
            return;

        Shell.Dialogs.Confirm(
            $"{what}.\n\nShow the system's own message for this?",
            confirmed =>
            {
                if (confirmed)
                    Shell.Dialogs.ShowErrorCode(code);
            });
    }

    private static string? Clean(string? entered)
    {
        string trimmed = entered?.Trim() ?? "";
        return trimmed.Length == 0 ? null : trimmed;
    }

    // The size the service reports is unsigned and the formatter takes a signed count; a value past the
    // signed range cannot come from a real install, so it is pinned rather than wrapping negative.
    private static long ToLong(ulong value) => (long)Math.Min(value, (ulong)long.MaxValue);

    private static string Shorten(string value, int maxLength)
        => value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength - 3), "...");

    /// <summary>A package found by the scan, and where it was found.</summary>
    private readonly record struct FoundPackage(string Name, string Folder, string Path, long Size);

    /// <summary>A folder still to be read, and how far into the search folder it sits.</summary>
    private readonly record struct ScanTarget(string Path, int Depth);
}

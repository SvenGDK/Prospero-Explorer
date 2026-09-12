// Prospero Explorer
// Copyright (C) 2026 SvenGDK

using ProsperoExplorer.Archive;
using ProsperoExplorer.Images;
using ProsperoExplorer.Media;
using ProsperoExplorer.Packages;
using ProsperoExplorer.Shell;
using ProsperoExplorer.TextEditor;
using SharpProspero.Application;
using SharpProspero.Interop;
using SharpProspero.Interop.Pad;
using SharpProspero.Storage;
using SharpProspero.Text;
using SharpProspero.Ui;
using System;
using System.Collections.Generic;

namespace ProsperoExplorer.Browser;

/// <summary>
/// One folder, listed. Cross opens the row, Square marks it so several rows can be acted on at once,
/// and Triangle opens the actions for whatever is marked. Every folder gets its own page, so Circle
/// walks back the way the user came.
/// </summary>
internal sealed class FileBrowserScreen : ExplorerScreen
{
    // The name column is padded so the sizes line up. The font is fixed width, so a character count is
    // a column position.
    private const int NameColumn = 52;

    private readonly string _path;
    private readonly string _name;
    private readonly ListView _list = new();
    private readonly Label _pathLabel = new();
    private readonly Label _statusLabel = new();

    // Everything the folder holds, then what a find has narrowed that to, then the text of each shown
    // row. The find works on the held listing so it can widen and narrow without reading the folder
    // again, and the row text is kept so a mark toggle rewrites one row rather than the whole list.
    private readonly List<Entry> _all = [];
    private readonly List<Entry> _entries = [];
    private readonly List<string> _rows = [];
    private readonly HashSet<string> _marked = new(StringComparer.Ordinal);

    private IFileTask? _running;
    private string _lastProgressMessage = string.Empty;
    private string _filter = string.Empty;

    /// <summary>Lists <paramref name="path"/>.</summary>
    public FileBrowserScreen(ExplorerShell shell, string path) : base(shell)
    {
        string trimmed = (path ?? "/").TrimEnd('/');
        _path = trimmed.Length == 0 ? "/" : trimmed;
        string name = PathUtil.GetFileName(_path);
        _name = name.Length == 0 ? _path : name;

        _list.VisibleRows = shell.Settings.ListRows;
        _list.Activated = OpenRow;
        _pathLabel.TextColor = shell.Theme.TextMuted;
        _statusLabel.TextColor = shell.Theme.TextMuted;
    }

    /// <summary>
    /// Set when this listing was opened from the folder above it. Up then closes this page instead of
    /// opening a second copy of the parent, so walking down and back up leaves the stack as it was.
    /// </summary>
    public bool OpenedFromParent { get; init; }

    /// <inheritdoc />
    public override string Title => _name;

    /// <inheritdoc />
    public override string Hint => "Cross opens, Square marks, Triangle acts, Circle goes back.";

    /// <summary>The folder being listed.</summary>
    public string FolderPath => _path;

    /// <summary>
    /// What the actions apply to: everything marked, or the row under the cursor when nothing is marked.
    /// The way up is never among them.
    /// </summary>
    public IReadOnlyList<string> Targets()
    {
        var targets = new List<string>();
        if (_marked.Count > 0)
        {
            // The whole marked set is acted on, ordered by the full listing, so what happens matches the
            // "{N} marked" the status shows even while a find is narrowing the view. Because marks are
            // present, the action never falls through to the cursor row.
            foreach (Entry entry in _all)
            {
                if (!entry.IsUp && _marked.Contains(entry.Path))
                    targets.Add(entry.Path);
            }
            return targets;
        }

        Entry? current = Current();
        if (current is { IsUp: false } row)
            targets.Add(row.Path);
        return targets;
    }

    /// <summary>What the actions apply to, named for a caption or a question.</summary>
    public string TargetSummary
    {
        get
        {
            IReadOnlyList<string> targets = Targets();
            return targets.Count switch
            {
                0 => "nothing",
                1 => PathUtil.GetFileName(targets[0]),
                _ => $"{targets.Count} items",
            };
        }
    }

    /// <summary>Whether the row under the cursor is an archive, which is what unpacking needs.</summary>
    public bool CanExtract => Current() is { IsUp: false, IsDirectory: false, Kind: FileKind.Archive };

    /// <summary>Whether the row under the cursor is a file, which is what a check value needs.</summary>
    public bool CanChecksum => Current() is { IsUp: false, IsDirectory: false };

    /// <inheritdoc />
    protected override UiElement BuildRoot()
        => new StackPanel()
            .Add(_pathLabel)
            .Add(new Separator())
            .Add(_list)
            .Add(new Separator())
            .Add(_statusLabel);

    /// <inheritdoc />
    public override void OnShown() => Refresh();

    /// <inheritdoc />
    public override void Tick(FrameContext context)
    {
        // An overlay is on screen and owns the pad; acting on the same press twice would open an action
        // behind a dialog the user is still answering.
        if (Shell.Dialogs.IsBusy)
            return;

        if (context.Pressed(ScePadButton.Triangle))
            Shell.Push(new BrowserActionsScreen(Shell, this));
        else if (context.Pressed(ScePadButton.Square))
            ToggleMark();
    }

    /// <inheritdoc />
    protected override void OnDispose()
    {
        _running?.Dispose();
        _running = null;
    }

    /// <summary>Reads the folder again and rebuilds the listing, keeping the cursor and any find.</summary>
    public void Refresh()
    {
        string? wanted = Current()?.Path;
        int previousIndex = _list.SelectedIndex;

        try
        {
            Read();
        }
        catch (Exception error)
        {
            Shell.ReportFailure($"{_name} could not be read", error);
        }

        PruneMarks();
        ApplyFilter();
        BuildRows();

        int restored = wanted is null ? previousIndex : IndexOf(wanted);
        ShowRows(restored < 0 ? previousIndex : restored);

        UpdatePathLabel();
        UpdateStatus();
    }

    /// <summary>Opens the row under the cursor.</summary>
    public void OpenCurrent() => OpenRow(_list.SelectedIndex);

    /// <summary>Holds what is in play for a paste that leaves the originals in place.</summary>
    public void HoldForCopy()
    {
        IReadOnlyList<string> targets = Targets();
        if (targets.Count == 0)
        {
            Shell.Status("Nothing to copy.");
            return;
        }

        Clipboard.Copy(targets);
        Shell.Status($"Holding {Clipboard.Describe()}.");
        UpdateStatus();
    }

    /// <summary>Holds what is in play for a paste that removes the originals.</summary>
    public void HoldForMove()
    {
        IReadOnlyList<string> targets = Targets();
        if (targets.Count == 0)
        {
            Shell.Status("Nothing to move.");
            return;
        }

        Clipboard.Cut(targets);
        Shell.Status($"Holding {Clipboard.Describe()}.");
        UpdateStatus();
    }

    /// <summary>Puts what is held into this folder.</summary>
    public void Paste()
    {
        if (!Clipboard.HasContent)
        {
            Shell.Status("Nothing is held.");
            return;
        }

        var sources = new List<string>(Clipboard.Paths);
        bool moving = Clipboard.IsCut;

        ClashReport clash;
        try
        {
            clash = FileOperations.Clashes(sources, _path);
        }
        catch (ProsperoException error)
        {
            Shell.ReportFailure("The folder could not be checked", error);
            return;
        }

        if (clash.Count > 0 && Shell.Settings.ConfirmDestructive)
        {
            Shell.Dialogs.Confirm(
                ClashQuestion(clash),
                yes =>
                {
                    if (yes)
                        StartPaste(sources, moving);
                });
            return;
        }

        StartPaste(sources, moving);
    }

    // A file that is already here is written over; a folder that is already here is merged into, so
    // its own contents stay and only the names the paste also carries are overwritten. The question
    // says which, so a yes on a folder clash is not read as "throw away what is already in it".
    private static string ClashQuestion(ClashReport clash)
    {
        if (clash.Count == 1)
        {
            return clash.HasDirectory
                ? $"{clash.FirstName} is already here as a folder. Merge into it, overwriting anything they share?"
                : $"{clash.FirstName} already exists here. Replace it?";
        }

        return clash.HasDirectory
            ? $"{clash.Count} items already exist here, {clash.FirstName} among them. Overwrite the files and merge into the folders?"
            : $"{clash.Count} items already exist here, {clash.FirstName} among them. Replace all {clash.Count}?";
    }

    /// <summary>Asks for a new name for the row under the cursor.</summary>
    public void RenameCurrent()
    {
        Entry? current = Current();
        if (current is not { IsUp: false } row)
        {
            Shell.Status("Nothing to rename.");
            return;
        }

        Shell.Dialogs.AskText("New name", row.Name, text =>
        {
            if (text is null)
                return;
            try
            {
                string renamed = FileOperations.Rename(row.Path, text);

                // A mark names a path, so a marked row keeps its mark across a rename by moving it
                // onto the new name rather than being left on a path the folder no longer holds.
                if (_marked.Remove(row.Path))
                    _marked.Add(renamed);

                Shell.Notify($"Renamed to {PathUtil.GetFileName(renamed)}.");
            }
            catch (Exception error)
            {
                Shell.ReportFailure($"{row.Name} could not be renamed", error);
            }
            Refresh();
        });
    }

    /// <summary>Removes everything in play, asking first when the settings say to.</summary>
    public void DeleteTargets()
    {
        IReadOnlyList<string> targets = Targets();
        if (targets.Count == 0)
        {
            Shell.Status("Nothing to delete.");
            return;
        }

        string what = TargetSummary;
        if (!Shell.Settings.ConfirmDestructive)
        {
            Run(FileOperations.Delete(targets), $"Deleted {what}.");
            return;
        }

        Shell.Dialogs.Confirm(
            $"Delete {what}? This cannot be undone.",
            yes =>
            {
                if (yes)
                    Run(FileOperations.Delete(targets), $"Deleted {what}.");
            });
    }

    /// <summary>Asks for a name and creates an empty folder here.</summary>
    public void NewFolder()
        => Shell.Dialogs.AskText("Folder name", string.Empty, text =>
        {
            if (text is null)
                return;
            try
            {
                FileOperations.CreateFolder(_path, text);
                Shell.Notify($"Created {text}.");
            }
            catch (Exception error)
            {
                Shell.ReportFailure("The folder could not be created", error);
            }
            Refresh();
        });

    /// <summary>Asks for a name and creates an empty file here.</summary>
    public void NewFile()
        => Shell.Dialogs.AskText("File name", string.Empty, text =>
        {
            if (text is null)
                return;
            try
            {
                FileOperations.CreateFile(_path, text);
                Shell.Notify($"Created {text}.");
            }
            catch (Exception error)
            {
                Shell.ReportFailure("The file could not be created", error);
            }
            Refresh();
        });

    /// <summary>Opens the details of the row under the cursor.</summary>
    public void ShowProperties()
    {
        Entry? current = Current();
        if (current is not { IsUp: false } row)
        {
            Shell.Status("Nothing to show.");
            return;
        }

        Shell.Push(new PropertiesScreen(Shell, row.Path));
    }

    /// <summary>Asks for a name and packs everything in play into a new archive here.</summary>
    public void AddToArchive()
    {
        IReadOnlyList<string> targets = Targets();
        if (targets.Count == 0)
        {
            Shell.Status("Nothing to pack.");
            return;
        }

        string extension = Shell.Settings.ArchiveFormat == ArchiveFormat.Tar ? ".tar" : ".zip";
        string suggested = targets.Count == 1
            ? PathUtil.GetFileNameWithoutExtension(targets[0]) + extension
            : _name + extension;

        Shell.Dialogs.AskText("Archive name", suggested, text =>
        {
            if (text is null)
                return;
            string name = text.Trim();
            if (name.Length == 0 || name.IndexOf('/') >= 0)
            {
                Shell.Status("That name cannot be used.");
                return;
            }
            if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                name += extension;

            string archivePath = PathUtil.Combine(_path, name);
            if (!Shell.Settings.ConfirmDestructive || !SafeExists(archivePath))
            {
                StartPack(targets, archivePath, name);
                return;
            }

            // Writing the archive replaces whatever is already under that name, so an existing one is
            // named before it goes rather than after.
            Shell.Dialogs.Confirm(
                $"{name} already exists here. Replace it?",
                yes =>
                {
                    if (yes)
                        StartPack(targets, archivePath, name);
                });
        });
    }

    /// <summary>Unpacks the archive under the cursor into this folder.</summary>
    public void ExtractHere()
    {
        Entry? current = Current();
        if (current is not { IsUp: false, IsDirectory: false } row)
        {
            Shell.Status("Nothing to unpack.");
            return;
        }

        StartExtract(row.Path, row.Name);
    }

    /// <summary>Reads the file under the cursor through once and shows both of its check values.</summary>
    public void ComputeChecksum()
    {
        Entry? current = Current();
        if (current is not { IsUp: false, IsDirectory: false } row)
        {
            Shell.Status("A folder has no check value.");
            return;
        }

        ChecksumTask task = FileOperations.Checksum(row.Path);
        Drive(task, () =>
        {
            if (task.FailureCount > 0)
                Shell.Dialogs.Alert($"{row.Name} could not be read.\n\n{task.Failures[0]}");
            else
                Shell.Dialogs.Alert($"{row.Name}\n\nSHA-256\n{task.Sha256}\n\nCRC-32\n{task.Crc32:X8}");
        });
    }

    /// <summary>Whether the listing is currently narrowed by a find.</summary>
    public bool IsFiltered => _filter.Length > 0;

    /// <summary>Asks for a query and narrows the listing to the names that match, closest match first.</summary>
    public void Find()
        => Shell.Dialogs.AskText("Find in this folder", _filter, text =>
        {
            if (text is null)
                return;

            _filter = text.Trim();
            ApplyFilter();
            BuildRows();
            ShowRows(FirstRealRow());
            UpdatePathLabel();
            UpdateStatus();

            if (_filter.Length == 0)
                Shell.Status("Showing everything.");
        });

    /// <summary>Drops the find so the whole folder is listed again, keeping the cursor where it can.</summary>
    public void ClearFilter()
    {
        if (_filter.Length == 0)
        {
            Shell.Status("Nothing is hidden.");
            return;
        }

        string? kept = Current()?.Path;
        _filter = string.Empty;
        ApplyFilter();
        BuildRows();
        int index = kept is null ? 0 : IndexOf(kept);
        ShowRows(index < 0 ? 0 : index);
        UpdatePathLabel();
        UpdateStatus();
        Shell.Status("Showing everything.");
    }

    private void Read()
    {
        _all.Clear();

        if (_path != "/")
        {
            string parent = PathUtil.GetDirectoryName(_path);
            if (parent.Length == 0)
                parent = "/";
            _all.Add(new Entry("..", parent, true, -1, FileKind.Folder, string.Empty, true));
        }

        bool wantSizes = Shell.Settings.ShowSizes || Shell.Settings.Sort == SortOrder.Size;
        var listed = new List<Entry>();

        foreach (DirectoryEntry entry in FileSystem.EnumerateDirectory(_path))
        {
            string name = entry.Name;
            if (name is "." or "..")
                continue;
            if (!Shell.Settings.ShowHiddenFiles && name.Length > 0 && name[0] == '.')
                continue;

            string full = PathUtil.Combine(_path, name);

            // The listing names the kind for most entries, but it can leave it out, and a link names
            // something other than itself. A single status call settles the kind and, for a regular
            // file, the size, and it reads the entry's record rather than opening it - so a pipe, a
            // socket or a device is passed over without the wait that opening one could bring. The
            // size is taken only when the listing or the order actually shows it.
            FileEntryType type = entry.Type;
            long size = -1;
            if (type is FileEntryType.Unknown or FileEntryType.SymbolicLink)
            {
                (FileEntryType resolved, long resolvedSize) = FileOperations.Stat(full);
                type = resolved;
                if (wantSizes && resolved == FileEntryType.File)
                    size = resolvedSize;
            }
            else if (wantSizes && type == FileEntryType.File)
            {
                size = FileOperations.Stat(full).Size;
            }

            bool isDirectory = type == FileEntryType.Directory;
            listed.Add(new Entry(
                name,
                full,
                isDirectory,
                size,
                FileKinds.Classify(full, isDirectory),
                PathUtil.GetExtension(name).ToLowerInvariant(),
                false));
        }

        listed.Sort(Compare);
        _all.AddRange(listed);
    }

    private int Compare(Entry a, Entry b)
    {
        if (Shell.Settings.FoldersFirst && a.IsDirectory != b.IsDirectory)
            return a.IsDirectory ? -1 : 1;

        switch (Shell.Settings.Sort)
        {
            case SortOrder.Size:
                int bySize = b.Size.CompareTo(a.Size);
                if (bySize != 0)
                    return bySize;
                break;
            case SortOrder.Kind:
                int byKind = string.CompareOrdinal(a.Extension, b.Extension);
                if (byKind != 0)
                    return byKind;
                break;
            default:
                break;
        }

        return TextFormat.CompareNatural(a.Name, b.Name);
    }

    private string FormatRow(Entry entry)
    {
        string mark = _marked.Contains(entry.Path) && !entry.IsUp ? "* " : "  ";
        string marker = FileKinds.Marker(entry.Kind);
        string name = entry.Name.Length > NameColumn ? entry.Name[..(NameColumn - 3)] + "..." : entry.Name;

        if (entry.IsUp || entry.IsDirectory || entry.Size < 0)
            return $"{mark}{marker} {name}";

        return $"{mark}{marker} {name.PadRight(NameColumn)}  {TextFormat.ByteSize(entry.Size),12}";
    }

    private void OpenRow(int index)
    {
        if (index < 0 || index >= _entries.Count)
            return;

        Entry entry = _entries[index];
        if (entry.IsUp)
        {
            GoUp(entry.Path);
            return;
        }

        if (entry.IsDirectory)
        {
            Shell.Push(new FileBrowserScreen(Shell, entry.Path) { OpenedFromParent = true });
            return;
        }

        OpenFile(entry);
    }

    private void GoUp(string parent)
    {
        if (OpenedFromParent)
            Shell.Pop();
        else
            Shell.Push(new FileBrowserScreen(Shell, parent));
    }

    private void OpenFile(Entry entry)
    {
        switch (entry.Kind)
        {
            case FileKind.Text:
                Shell.Push(new TextScreen(Shell, entry.Path));
                break;
            case FileKind.Image:
                Shell.Push(new ImageScreen(Shell, entry.Path));
                break;
            case FileKind.Audio:
            case FileKind.Video:
                Shell.Push(new MediaScreen(Shell, entry.Path));
                break;
            case FileKind.Archive:
                // A container is browsed; a lone compressed stream holds one file and no listing, so
                // there is nothing to browse and unpacking it is the only thing to do with it. It still
                // writes a file into this folder, so opening the row asks the same question the action
                // does rather than doing it on the press.
                if (FileKinds.IsBrowsableArchive(entry.Path))
                    Shell.Push(new ArchiveScreen(Shell, entry.Path));
                else
                    StartExtract(entry.Path, entry.Name);
                break;
            case FileKind.Package:
                Shell.Push(new PackageScreen(Shell));
                break;
            default:
                OfferAsText(entry);
                break;
        }
    }

    private void OfferAsText(Entry entry)
        => Shell.Dialogs.Confirm(
            $"{entry.Name} is not a kind this application reads. Open it as text?",
            yes =>
            {
                if (yes)
                    Shell.Push(new TextScreen(Shell, entry.Path));
            });

    private void ToggleMark()
    {
        Entry? current = Current();
        if (current is not { IsUp: false } row)
            return;

        if (!_marked.Remove(row.Path))
            _marked.Add(row.Path);

        // Only the toggled row's text changes, so it alone is formatted again and the rest are the
        // strings already held. On a folder of thousands this is one small allocation for a key press
        // rather than a page of them.
        int index = _list.SelectedIndex;
        if (index >= 0 && index < _rows.Count)
            _rows[index] = FormatRow(row);
        ShowRows(index);
        UpdateStatus();
    }

    // Formats every shown row and keeps the text, so a later mark toggle can rewrite one row rather
    // than formatting the whole listing again.
    private void BuildRows()
    {
        _rows.Clear();
        foreach (Entry row in _entries)
            _rows.Add(FormatRow(row));
    }

    // Puts the kept row text into the list and the cursor at index. Nothing is read from the file
    // system; the list holds plain text.
    private void ShowRows(int index)
    {
        _list.Clear();
        foreach (string row in _rows)
            _list.Add(row);
        _list.SelectedIndex = index;
    }

    // Narrows the held listing to what the find matches, best match first, with the way up kept at the
    // top. With no find the whole folder is shown in the order it was read.
    private void ApplyFilter()
    {
        _entries.Clear();

        if (_filter.Length == 0)
        {
            _entries.AddRange(_all);
            return;
        }

        if (_all.Count > 0 && _all[0].IsUp)
            _entries.Add(_all[0]);

        var pool = new List<Entry>(_all.Count);
        foreach (Entry entry in _all)
        {
            if (!entry.IsUp)
                pool.Add(entry);
        }

        foreach (var ranked in FuzzyMatcher.Rank(_filter, pool, static entry => entry.Name))
            _entries.Add(ranked.Item);
    }

    // A mark names a path. A rename or a change made outside this listing can leave a mark on a path
    // the folder no longer holds, which would be counted but never shown; such a mark is dropped. A
    // mark on an entry a find has only hidden is kept, because the whole listing is what is checked.
    private void PruneMarks()
    {
        if (_marked.Count == 0)
            return;

        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (Entry entry in _all)
            present.Add(entry.Path);
        _marked.RemoveWhere(path => !present.Contains(path));
    }

    private void StartPaste(IReadOnlyList<string> sources, bool moving)
    {
        IFileTask task = moving
            ? FileOperations.Move(sources, _path)
            : FileOperations.Copy(sources, _path);

        string what = sources.Count == 1 ? PathUtil.GetFileName(sources[0]) : $"{sources.Count} items";
        string doneMessage = moving ? $"Moved {what}." : $"Copied {what}.";

        Drive(task, () =>
        {
            Report(task, doneMessage);

            // A cut is forgotten once the move has run, because the paths it holds have stopped
            // existing. It is kept when the move never ran at all - something else was already going,
            // or the bar never came up - so the user can paste it afterwards instead of marking
            // everything again.
            if (moving && task.IsFinished)
                Clipboard.Clear();

            _marked.Clear();
            Refresh();
        });
    }

    // What an archive holds is only known once it has been read, and reading it is work spread over
    // frames, so the question comes before any of it starts. Nothing is written until it is answered.
    private void StartExtract(string archivePath, string name)
    {
        if (!Shell.Settings.ConfirmDestructive)
        {
            RunExtract(archivePath, name);
            return;
        }

        Shell.Dialogs.Confirm(
            $"Unpack {name} here? Anything already here under a name it uses is replaced.",
            yes =>
            {
                if (yes)
                    RunExtract(archivePath, name);
            });
    }

    private void RunExtract(string archivePath, string name)
        => Run(FileOperations.ExtractArchive(archivePath, _path), $"Unpacked {name}.");

    private void StartPack(IReadOnlyList<string> targets, string archivePath, string name)
        => Run(
            FileOperations.AddToArchive(targets, archivePath, Shell.Settings.ArchiveCompress, Shell.Settings.ArchiveFormat),
            $"Packed {name}.");

    private void Run(IFileTask task, string doneMessage)
        => Drive(task, () =>
        {
            Report(task, doneMessage);
            _marked.Clear();
            Refresh();
        });

    // How a finished task is put to the user. A task that never ran to its end reports neither success
    // nor a failure of its own, so it is called out on its own terms.
    private void Report(IFileTask task, string doneMessage)
    {
        if (task.FailureCount > 0)
            Shell.Dialogs.Alert($"{doneMessage}\n\n{task.FailureCount} could not be handled.\n{task.Failures[0]}");
        else if (task.IsFinished)
            Shell.Notify(doneMessage);
        else
            Shell.Status("The work stopped before it finished.");
    }

    // Drives a task from the frame loop behind the progress bar. The task does a bounded amount of work
    // per frame, so the page underneath keeps drawing however large the work is.
    private void Drive(IFileTask task, Action finished)
    {
        // Nothing that was asked for has happened, so the caller's completion does not run: it would
        // report work that was never done and undo the marks the user would need to ask again.
        if (_running is not null)
        {
            Shell.Status("Something else is still running.");
            task.Dispose();
            return;
        }

        _running = task;
        _lastProgressMessage = string.Empty;

        Shell.Dialogs.RunWithProgress(
            task.Caption,
            dialog =>
            {
                bool done = task.Step();
                dialog.SetProgress((int)(task.Progress * 100f));
                string message = task.CurrentItem;
                if (!string.Equals(message, _lastProgressMessage, StringComparison.Ordinal))
                {
                    _lastProgressMessage = message;
                    dialog.SetProgressMessage(message);
                }
                return done;
            },
            () =>
            {
                try
                {
                    finished();
                }
                finally
                {
                    task.Dispose();
                    _running = null;
                }
            });
    }

    private void UpdateStatus()
    {
        int total = _all.Count;
        if (total > 0 && _all[0].IsUp)
            total--;

        string marked = _marked.Count == 0 ? "none marked" : $"{_marked.Count} marked";

        string items;
        if (_filter.Length > 0)
        {
            int shown = _entries.Count;
            if (shown > 0 && _entries[0].IsUp)
                shown--;
            items = $"{shown} of {total} match '{_filter}'";
        }
        else
        {
            items = total == 1 ? "1 item" : $"{total} items";
        }

        _statusLabel.Text = $"{items}, {marked}, {Clipboard.Describe()}";
    }

    // The path line carries the current find so it is plain that the listing is narrowed and by what.
    private void UpdatePathLabel()
        => _pathLabel.Text = _filter.Length == 0 ? _path : $"{_path}    find: {_filter}";

    // Where the cursor lands after a find: the first match rather than the way up that sits above it,
    // so the closest match is under the cursor straight away.
    private int FirstRealRow()
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (!_entries[i].IsUp)
                return i;
        }
        return 0;
    }

    private Entry? Current()
    {
        int index = _list.SelectedIndex;
        return index >= 0 && index < _entries.Count ? _entries[index] : null;
    }

    private int IndexOf(string path)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (string.Equals(_entries[i].Path, path, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    // A path that cannot be looked at counts as absent, which sends the caller down the path that asks
    // the file system to write and reports whatever it says.
    private static bool SafeExists(string path)
    {
        try
        {
            return FileSystem.Exists(path);
        }
        catch (ProsperoException)
        {
            return false;
        }
    }

    private readonly record struct Entry(
        string Name,
        string Path,
        bool IsDirectory,
        long Size,
        FileKind Kind,
        string Extension,
        bool IsUp);
}

/// <summary>
/// What can be done with what the browser has in play. This is a page of its own rather than a panel in
/// the listing, so the D-pad moves through it without also moving the row underneath.
/// </summary>
internal sealed class BrowserActionsScreen : ExplorerScreen
{
    private readonly FileBrowserScreen _browser;
    private readonly string _summary;

    /// <summary>Opens the actions for <paramref name="browser"/>.</summary>
    public BrowserActionsScreen(ExplorerShell shell, FileBrowserScreen browser) : base(shell)
    {
        _browser = browser;
        _summary = browser.TargetSummary;
    }

    /// <inheritdoc />
    public override string Title => "Actions";

    /// <inheritdoc />
    public override string Hint => "Cross runs, Circle goes back.";

    /// <inheritdoc />
    protected override UiElement BuildRoot()
    {
        var menu = new ScrollMenu { ViewHeight = 640 };
        menu.Add(new Button("Open", () => Then(_browser.OpenCurrent)));
        menu.Add(new Button("Copy", () => Then(_browser.HoldForCopy)));
        menu.Add(new Button("Cut", () => Then(_browser.HoldForMove)));

        if (Clipboard.HasContent)
            menu.Add(new Button($"Paste ({Clipboard.Describe()})", () => Then(_browser.Paste)));

        menu.Add(new Separator());
        menu.Add(new Button("Rename", () => Then(_browser.RenameCurrent)));
        menu.Add(new Button("Delete", () => Then(_browser.DeleteTargets)));
        menu.Add(new Separator());
        menu.Add(new Button("New folder", () => Then(_browser.NewFolder)));
        menu.Add(new Button("New file", () => Then(_browser.NewFile)));
        menu.Add(new Separator());
        menu.Add(new Button("Properties", () => Then(_browser.ShowProperties)));
        menu.Add(new Button("Add to archive", () => Then(_browser.AddToArchive)));

        if (_browser.CanExtract)
            menu.Add(new Button("Extract here", () => Then(_browser.ExtractHere)));
        if (_browser.CanChecksum)
            menu.Add(new Button("Compute checksum", () => Then(_browser.ComputeChecksum)));

        menu.Add(new Separator());
        menu.Add(new Button("Find", () => Then(_browser.Find)));
        if (_browser.IsFiltered)
            menu.Add(new Button("Show all", () => Then(_browser.ClearFilter)));
        menu.Add(new Button("Refresh", () => Then(_browser.Refresh)));

        return new StackPanel()
            .Add(new Label($"Acting on {_summary}.") { TextColor = Shell.Theme.TextMuted })
            .Add(new Separator())
            .Add(menu);
    }

    // Every action closes this page first, so the listing is back in front before the action asks the
    // user anything or puts a progress bar up.
    private void Then(Action action)
    {
        Shell.Pop();
        action();
    }
}

/// <summary>What the file system knows about one entry.</summary>
internal sealed class PropertiesScreen : ExplorerScreen
{
    private readonly string _path;
    private readonly List<KeyValueRow> _rows = [];

    /// <summary>Shows the details of <paramref name="path"/>.</summary>
    public PropertiesScreen(ExplorerShell shell, string path) : base(shell)
    {
        _path = path;
        Build();
    }

    /// <inheritdoc />
    public override string Title => PathUtil.GetFileName(_path);

    /// <inheritdoc />
    public override string Hint => "Circle goes back.";

    /// <inheritdoc />
    protected override UiElement BuildRoot()
    {
        var panel = new StackPanel();
        foreach (KeyValueRow row in _rows)
            panel.Add(row);
        return panel;
    }

    private void Build()
    {
        FileEntryType type;
        try
        {
            type = FileSystem.GetEntryType(_path);
        }
        catch (ProsperoException)
        {
            type = FileEntryType.Unknown;
        }

        bool isDirectory = type == FileEntryType.Directory;
        _rows.Add(new KeyValueRow("Path", _path));
        _rows.Add(new KeyValueRow("Name", PathUtil.GetFileName(_path)));
        _rows.Add(new KeyValueRow("Kind", FileKinds.Describe(FileKinds.Classify(_path, isDirectory))));
        _rows.Add(new KeyValueRow("Entry type", Describe(type)));
        _rows.Add(new KeyValueRow("Size", DescribeSize(isDirectory)));
    }

    private string DescribeSize(bool isDirectory)
    {
        try
        {
            if (!isDirectory)
                return TextFormat.ByteSize(FileSystem.GetFileSize(_path));

            int count = 0;
            foreach (DirectoryEntry entry in FileSystem.EnumerateDirectory(_path))
            {
                if (entry.Name is not ("." or ".."))
                    count++;
            }
            return count == 1 ? "1 entry" : $"{count} entries";
        }
        catch (ProsperoException error)
        {
            return ExplorerShell.Describe(error);
        }
    }

    private static string Describe(FileEntryType type) => type switch
    {
        FileEntryType.Directory => "Directory",
        FileEntryType.File => "Regular file",
        FileEntryType.SymbolicLink => "Link",
        FileEntryType.Fifo => "Named pipe",
        FileEntryType.Character => "Character device",
        FileEntryType.Block => "Block device",
        FileEntryType.Socket => "Socket",
        _ => "Not reported",
    };
}

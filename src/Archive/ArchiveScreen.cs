// Prospero Explorer
// Copyright (C) 2026 SvenGDK

using ProsperoExplorer.Shell;
using SharpProspero.Platform;
using SharpProspero.Storage;
using SharpProspero.Text;
using SharpProspero.Ui;
using System;
using System.Text;

namespace ProsperoExplorer.Archive;

/// <summary>
/// What is inside one archive: every member with its size and how much of it the archive kept, and the
/// actions that get a member back out. A member holding text is shown here rather than opened
/// elsewhere, so reading a note or a manifest costs nothing on disk.
/// </summary>
internal sealed class ArchiveScreen : ExplorerScreen
{
    // How many rows are added per frame while the listing is filled. An archive of tens of thousands of
    // members would otherwise hold the frame for as long as it took to write them all out.
    private const int RowsPerSlice = 512;

    // How wide the name column is, in characters. The rest of the row carries the sizes.
    private const int NameColumn = 64;

    // How much of a name the line under the listing shows, leaving the rest of that line for the sizes.
    private const int DetailNameWidth = 70;

    // How much of a member the preview shows. The text is laid out and wrapped every frame, so a whole
    // large file would cost too much to keep on screen; the text viewer opens one in full.
    private const int PreviewBytes = 32 * 1024;

    // The most of a member a preview will unpack. A preview shows only the first PreviewBytes, but
    // reading a member unpacks the whole of it, and a zip member can unpack to tens of megabytes; a
    // member larger than this is extracted to be read rather than unpacked in full here to show a little.
    private const long PreviewMemberCeiling = 1L * 1024 * 1024;

    private readonly string _archivePath;
    private readonly Label _summary = new();
    private readonly Label _totals = new();
    private readonly ListView _list = new();
    private readonly Label _detail = new();
    private readonly StackPanel _listPanel = new();
    private readonly StackPanel _textPanel = new();
    private readonly Label _textTitle = new();
    private readonly TextBlock _textBody = new();
    private readonly ScrollView _textView;
    private readonly Button _extractEntry;
    private readonly Button _extractEverything;
    private readonly Button _viewAsText;

    private ArchiveListing? _listing;
    private ArchiveListing? _opened;
    private byte[]? _raw;
    private Exception? _loadError;
    private int _loadPhase;
    private int _filled;
    private bool _loadStarted;

    /// <summary>Opens the archive at <paramref name="archivePath"/>.</summary>
    public ArchiveScreen(ExplorerShell shell, string archivePath) : base(shell)
    {
        _archivePath = archivePath;
        _textView = new ScrollView(_textBody) { ViewHeight = 620 };
        _extractEntry = new Button("Extract entry", ExtractSelected) { Enabled = false };
        _extractEverything = new Button("Extract everything", ExtractEverything) { Enabled = false };
        _viewAsText = new Button("View as text", ViewSelected) { Enabled = false };

        _list.VisibleRows = Math.Clamp(shell.Settings.ListRows, 6, 18);
        _list.SelectionChanged = ShowDetail;
        _list.Activated = OpenEntry;
    }

    /// <inheritdoc/>
    public override string Title => PathUtil.GetFileName(_archivePath);

    /// <inheritdoc/>
    public override string Hint => _textPanel.Visible
        ? "Up and down scroll, Circle returns to the list."
        : "Cross opens an entry, Circle goes back.";

    /// <inheritdoc/>
    protected override UiElement BuildRoot()
    {
        _summary.TextColor = Shell.Theme.TextMuted;
        _totals.TextColor = Shell.Theme.TextMuted;
        _detail.TextColor = Shell.Theme.TextMuted;
        _textTitle.TextColor = Shell.Theme.Accent;

        _listPanel
            .Add(_summary)
            .Add(_totals)
            .Add(new Separator())
            .Add(_list)
            .Add(_detail)
            .Add(new Separator())
            .Add(new Row()
                .Add(_extractEntry)
                .Add(_extractEverything)
                .Add(_viewAsText));

        // The preview shares the page rather than opening above it, so returning to the listing keeps the
        // position in it and the archive stays read once.
        _textPanel.Visible = false;
        _textPanel
            .Add(_textTitle)
            .Add(new Separator())
            .Add(_textView)
            .Add(new Button("Back to the listing", CloseTextView));

        return new StackPanel()
            .Add(_listPanel)
            .Add(_textPanel);
    }

    /// <inheritdoc/>
    public override void OnShown()
    {
        if (_loadStarted)
            return;
        _loadStarted = true;
        _summary.Text = $"Reading {Title}...";
        Shell.Dialogs.RunWithProgress($"Reading {Title}", LoadStep, LoadFinished);
    }

    /// <inheritdoc/>
    protected override bool OnCancel()
    {
        if (!_textPanel.Visible)
            return false;
        CloseTextView();
        return true;
    }

    /// <inheritdoc/>
    protected override void OnDispose()
    {
        _listing?.Dispose();
        _listing = null;
        _opened?.Dispose();
        _opened = null;
        _raw = null;
        _textBody.Text = "";
    }

    // Reading the file, making sense of it and writing out the rows are three different costs, so each
    // one takes its own frame and the listing fills a slice at a time after that.
    private bool LoadStep(MessageDialog dialog)
    {
        switch (_loadPhase)
        {
            case 0:
                dialog.SetProgressMessage("Reading the file");
                dialog.SetProgress(5);
                try
                {
                    _raw = ArchiveService.ReadArchiveFile(_archivePath);
                }
                catch (Exception error)
                {
                    _loadError = error;
                    return true;
                }

                _loadPhase = 1;
                return false;

            case 1:
                dialog.SetProgressMessage("Reading what is inside");
                dialog.SetProgress(30);
                try
                {
                    _opened = ArchiveService.OpenBytes(_archivePath, _raw!);
                }
                catch (Exception error)
                {
                    _loadError = error;
                    return true;
                }

                // The listing keeps whatever copy of the file it needs, so this one is let go here rather
                // than held until the page closes.
                _raw = null;
                _loadPhase = 2;
                return false;

            default:
                return FillStep(dialog);
        }
    }

    private bool FillStep(MessageDialog dialog)
    {
        ArchiveListing listing = _opened!;
        int count = listing.Items.Count;
        int end = Math.Min(count, _filled + RowsPerSlice);
        for (; _filled < end; _filled++)
            _list.Add(RowText(listing.Items[_filled]));

        dialog.SetProgressMessage($"Listing {_filled} of {count}");
        dialog.SetProgress(count == 0 ? 100 : 30 + (70 * _filled / count));
        return _filled >= count;
    }

    private void LoadFinished()
    {
        _raw = null;

        if (_loadError is not null || _opened is null)
        {
            _opened?.Dispose();
            _opened = null;
            _summary.Text = $"{Title} could not be opened.";
            _totals.Text = _loadError is null ? "The archive is empty of anything readable." : ExplorerShell.Describe(_loadError);
            _detail.Text = "";
            SetActionsEnabled(false);
            if (_loadError is not null)
                Shell.ReportFailure($"{Title} could not be opened", _loadError, important: true);
            return;
        }

        _listing = _opened;
        _opened = null;

        _summary.Text = $"{_listing.KindName} - {_listing.Items.Count} entries";

        // A container that pads its records can come out larger than what it holds, which a percentage
        // of the original reads as a nonsense figure, so that case is said in words instead.
        string comparison = _listing.Ratio <= 100
            ? $"{_listing.Ratio}% of the original"
            : "larger than what it holds";
        _totals.Text =
            $"{TextFormat.ByteSize(_listing.FileSize)} packed, {TextFormat.ByteSize(_listing.UnpackedTotal)} unpacked "
            + $"({comparison})";
        SetActionsEnabled(true);
        ShowDetail(_list.SelectedIndex);
    }

    private void SetActionsEnabled(bool enabled)
    {
        _extractEverything.Enabled = enabled;

        // The two entry actions need something selected as well as something open.
        bool hasEntries = enabled && _list.Items.Count > 0;
        _extractEntry.Enabled = hasEntries;
        _viewAsText.Enabled = hasEntries;
    }

    private void ShowDetail(int index)
    {
        if (_listing is null || index < 0 || index >= _listing.Items.Count)
        {
            _detail.Text = "";
            return;
        }

        ArchiveItem item = _listing.Items[index];
        var text = new StringBuilder(Shorten(item.Name, DetailNameWidth));
        if (item.IsDirectory)
        {
            text.Append(" - folder");
        }
        else
        {
            text.Append(" - ").Append(TextFormat.ByteSize(item.Size));
            text.Append(item.PackedSize == item.Size
                ? ", stored"
                : $", {TextFormat.ByteSize(item.PackedSize)} packed ({item.Ratio}% of the original)");
        }

        if (item.Modified is DateTime when)
            text.Append(" - ").Append(Formatting.Timestamp(when));
        _detail.Text = text.ToString();
    }

    private void OpenEntry(int index)
    {
        if (_listing is null || index < 0 || index >= _listing.Items.Count)
            return;

        ArchiveItem item = _listing.Items[index];
        if (item.IsDirectory)
        {
            Shell.Status($"{item.Name} is a folder inside the archive.");
            return;
        }

        if (FileKinds.Classify(item.Name, isDirectory: false) == FileKind.Text)
        {
            ViewAsText(item);
            return;
        }

        Shell.Status($"{PathUtil.GetFileName(item.Name)} - {TextFormat.ByteSize(item.Size)}. Extract it to open it.");
    }

    private void ViewSelected()
    {
        ArchiveItem? item = SelectedItem();
        if (item is not null)
            ViewAsText(item);
    }

    private void ViewAsText(ArchiveItem item)
    {
        if (_listing is null)
            return;

        if (item.IsDirectory)
        {
            Shell.Status($"{item.Name} is a folder.");
            return;
        }

        // A preview unpacks the whole member to show only its start, so it is bounded well below the
        // text ceiling: a larger member is extracted to be read rather than unpacked in full for a
        // glimpse of it.
        long ceiling = Math.Min(Shell.Settings.TextMaxKilobytes * 1024L, PreviewMemberCeiling);
        if (item.Size > ceiling)
        {
            Shell.Status(
                $"{PathUtil.GetFileName(item.Name)} is larger than the {TextFormat.ByteSize(ceiling)} a preview reads. "
                + "Extract it to read it in full.");
            return;
        }

        byte[] bytes;
        try
        {
            bytes = _listing.Read(item);
        }
        catch (Exception error)
        {
            Shell.ReportFailure($"{PathUtil.GetFileName(item.Name)} could not be read", error);
            return;
        }

        if (!LooksLikeText(bytes))
        {
            Shell.Status($"{PathUtil.GetFileName(item.Name)} does not hold text.");
            return;
        }

        string text = Decode(bytes, out bool truncated);
        string name = Shorten(item.Name, DetailNameWidth);
        _textTitle.Text = truncated
            ? $"{name} - first {TextFormat.ByteSize(PreviewBytes)} of {TextFormat.ByteSize(item.Size)}"
            : $"{name} - {TextFormat.ByteSize(item.Size)}";
        _textBody.Text = text;
        _textView.ScrollToTop();
        _listPanel.Visible = false;
        _textPanel.Visible = true;
    }

    private void CloseTextView()
    {
        _textPanel.Visible = false;
        _listPanel.Visible = true;

        // The preview can hold tens of thousands of characters, and nothing needs them once it is closed.
        _textBody.Text = "";
        _textTitle.Text = "";
    }

    private void ExtractSelected()
    {
        ArchiveItem? selected = SelectedItem();
        if (selected is null || _listing is null)
            return;

        ArchiveListing listing = _listing;
        ArchiveItem item = selected;
        string what = $"Extracting {PathUtil.GetFileName(item.Name)}";
        Shell.Dialogs.AskText("Extract this entry to", ExtractRoot(), entered =>
        {
            if (!TryDestination(entered, out string destination))
                return;
            ConfirmThenExtract(destination, () => ArchiveService.ExtractEntry(listing, item, destination), what);
        });
    }

    // Where an unpack is offered to go. The setting when it names somewhere, and otherwise the folder
    // the archive itself is in - which is where the user is looking, and is somewhere that is known to
    // exist. A folder written into the code would not be.
    private string ExtractRoot()
    {
        string configured = Shell.Settings.ExtractPath;
        if (!string.IsNullOrEmpty(configured))
            return configured;
        string folder = PathUtil.GetDirectoryName(_archivePath);
        return string.IsNullOrEmpty(folder) ? "/" : folder;
    }

    private void ExtractEverything()
    {
        if (_listing is null)
        {
            Shell.Status("Nothing is open.");
            return;
        }

        ArchiveListing listing = _listing;
        string suggestion = PathUtil.Combine(ExtractRoot(), SuggestedFolderName());
        string what = $"Unpacking {Title}";
        Shell.Dialogs.AskText("Extract everything to", suggestion, entered =>
        {
            if (!TryDestination(entered, out string destination))
                return;
            ConfirmThenExtract(destination, () => ArchiveService.ExtractAll(listing, destination), what);
        });
    }

    private bool TryDestination(string? entered, out string destination)
    {
        destination = entered?.Trim() ?? "";
        if (entered is null)
        {
            Shell.Status("Extraction cancelled.");
            return false;
        }

        if (destination.Length == 0)
        {
            Shell.Status("No folder was given.");
            return false;
        }

        if (!PathUtil.IsAbsolute(destination))
        {
            Shell.Status($"{destination} is not a full path. Give one starting with a slash.");
            return false;
        }

        return true;
    }

    private void ConfirmThenExtract(string destination, Func<ExtractOperation> start, string what)
    {
        if (Shell.Settings.ConfirmDestructive && FolderHoldsFiles(destination))
        {
            Shell.Dialogs.Confirm(
                $"{destination} already holds files. Anything with the same name is overwritten. Continue?",
                answer =>
                {
                    if (answer)
                        RunExtract(start, what);
                    else
                        Shell.Status("Extraction cancelled.");
                });
            return;
        }

        RunExtract(start, what);
    }

    private void RunExtract(Func<ExtractOperation> start, string what)
    {
        ExtractOperation operation;
        try
        {
            operation = start();
        }
        catch (Exception error)
        {
            Shell.ReportFailure($"{what} could not start", error, important: true);
            return;
        }

        Shell.Dialogs.RunWithProgress(
            $"Extracting to {operation.Destination}",
            dialog =>
            {
                bool done = operation.Step();
                dialog.SetProgress((int)Math.Clamp(operation.Progress * 100f, 0f, 100f));
                dialog.SetProgressMessage(operation.CurrentItem);
                return done;
            },
            () =>
            {
                // A member whose stored path climbs out of the chosen folder is passed over rather than
                // written, and one that will not decode or write is passed over with the rest still
                // unpacked; the user is told how many of each, since files they expected will be absent.
                string skipped = operation.Skipped > 0 ? $", {operation.Skipped} passed over" : "";
                string failed = operation.Failed > 0 ? $", {operation.Failed} could not be unpacked" : "";
                string summary =
                    $"{what} finished: {operation.Extracted} files, {TextFormat.ByteSize(operation.BytesWritten)}, "
                    + $"into {operation.Destination}{skipped}{failed}.";

                // The first member that failed says why, which is more use than the count alone when an
                // unpack comes up short.
                if (operation.FirstFailure is string reason)
                    summary += $" First problem: {reason}";
                Shell.Notify(summary);
            });
    }

    private ArchiveItem? SelectedItem()
    {
        if (_listing is null)
        {
            Shell.Status("Nothing is open.");
            return null;
        }

        int index = _list.SelectedIndex;
        if (index < 0 || index >= _listing.Items.Count)
        {
            Shell.Status("Nothing is selected.");
            return null;
        }

        return _listing.Items[index];
    }

    private string SuggestedFolderName()
    {
        string name = PathUtil.GetFileNameWithoutExtension(_archivePath);
        return name.Length == 0 ? "extracted" : name;
    }

    private static bool FolderHoldsFiles(string folder)
    {
        try
        {
            if (!FileSystem.Exists(folder) || !FileSystem.IsDirectory(folder))
                return false;
            foreach (DirectoryEntry entry in FileSystem.EnumerateDirectory(folder))
            {
                if (entry.Name is not ("." or ".."))
                    return true;
            }
        }
        catch (Exception)
        {
            // The folder cannot be looked at, so there is nothing to warn about. Extracting reports its
            // own failure if the path is unusable.
        }

        return false;
    }

    private static string RowText(ArchiveItem item)
    {
        string marker = FileKinds.Marker(FileKinds.Classify(item.Name, item.IsDirectory));
        string name = Fit(item.Name, NameColumn);
        if (item.IsDirectory)
            return $"{marker} {name}";

        string size = TextFormat.ByteSize(item.Size).PadLeft(10);
        string packed = item.PackedSize == item.Size ? "stored" : $"{item.Ratio}%";
        return $"{marker} {name} {size}  {packed.PadLeft(6)}";
    }

    private static string Fit(string text, int width)
        => text.Length <= width ? text.PadRight(width) : Shorten(text, width);

    // The end of a stored path is what identifies it, so a long one loses its front rather than its name.
    private static string Shorten(string text, int width)
        => text.Length <= width ? text : ".." + text[(text.Length - width + 2)..];

    private static bool LooksLikeText(byte[] bytes)
    {
        int end = Math.Min(bytes.Length, 4096);
        for (int i = 0; i < end; i++)
        {
            if (bytes[i] == 0)
                return false;
        }

        return true;
    }

    private string Decode(byte[] bytes, out bool truncated)
    {
        int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        int length = Math.Min(bytes.Length, PreviewBytes);
        truncated = length < bytes.Length;
        if (truncated)
        {
            // Cutting at a fixed count can land inside a character, which would read as a replacement
            // mark at the end of the preview, so the cut moves back to a character boundary.
            while (length > start && (bytes[length - 1] & 0xC0) == 0x80)
                length--;
            if (length > start && (bytes[length - 1] & 0x80) != 0)
                length--;
        }

        string text = Encoding.UTF8.GetString(bytes, start, Math.Max(0, length - start));
        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\t", new string(' ', Shell.Settings.TextTabWidth), StringComparison.Ordinal);
    }
}

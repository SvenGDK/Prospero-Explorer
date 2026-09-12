// Prospero Explorer
// Copyright (C) 2026 SvenGDK

using ProsperoExplorer.Shell;
using SharpProspero.Application;
using SharpProspero.Graphics;
using SharpProspero.Interop.Dialog;
using SharpProspero.Interop.Pad;
using SharpProspero.Storage;
using SharpProspero.Text;
using SharpProspero.Ui;
using System;
using System.Collections.Generic;
using System.Text;

namespace ProsperoExplorer.TextEditor;

/// <summary>
/// Draws a buffer of lines with a number down the left, expanding tabs and breaking long lines to the
/// width it is given. It is a leaf control meant to sit inside a <see cref="ScrollView"/>, which handles
/// the scrolling and clips it.
/// </summary>
/// <remarks>
/// A file worth opening in a viewer can run to hundreds of thousands of lines, so the text of every
/// visible line is built as it is drawn rather than held. What is kept is one integer per line saying
/// which row that line starts at, which is enough to measure the whole buffer, to find the line under a
/// row, and to scroll to a line. The built-in text is fixed width, so a column is a whole number of
/// pixels and the break points are arithmetic rather than measurement.
/// </remarks>
internal sealed class TextCanvas : UiElement
{
    // A little air between rows, so a highlighted line reads as a band rather than a solid block.
    private const int RowGap = 2;

    // The built-in text has no glyph for the delete character, which is grouped with the control
    // characters here rather than left as a gap in the line.
    private const char Delete = (char)0x7F;

    private readonly List<string> _segments = [];
    private IReadOnlyList<string> _lines = [];
    private int[] _lineStartRow = [0];
    private char[] _scratch = new char[256];
    private int _scratchLength;
    private int _segmentLine = -1;
    private int _revision;
    private int _builtRevision = -1;
    private int _builtWidth = -1;
    private int _columns = 1;
    private int _gutterWidth;
    private bool _wordWrap = true;
    private int _scale = 2;
    private int _tabWidth = 4;
    private bool _showLineNumbers = true;

    /// <summary>The buffer to show, one entry per line.</summary>
    public IReadOnlyList<string> Lines
    {
        get => _lines;
        set
        {
            _lines = value ?? [];
            Invalidate();
        }
    }

    /// <summary>Whether a line longer than the width is broken to fit instead of running off the edge.</summary>
    public bool WordWrap
    {
        get => _wordWrap;
        set
        {
            if (_wordWrap == value)
                return;
            _wordWrap = value;
            Invalidate();
        }
    }

    /// <summary>How large the text is drawn, 1 to 4.</summary>
    public int Scale
    {
        get => _scale;
        set
        {
            int clamped = Math.Clamp(value, 1, 4);
            if (_scale == clamped)
                return;
            _scale = clamped;
            Invalidate();
        }
    }

    /// <summary>How many columns a tab advances to the next stop.</summary>
    public int TabWidth
    {
        get => _tabWidth;
        set
        {
            int clamped = Math.Clamp(value, 1, 8);
            if (_tabWidth == clamped)
                return;
            _tabWidth = clamped;
            Invalidate();
        }
    }

    /// <summary>Whether the line number is drawn down the left.</summary>
    public bool ShowLineNumbers
    {
        get => _showLineNumbers;
        set
        {
            if (_showLineNumbers == value)
                return;
            _showLineNumbers = value;
            Invalidate();
        }
    }

    /// <summary>The line drawn with a band behind it, or -1 for none.</summary>
    public int HighlightLine { get; set; } = -1;

    /// <summary>The band behind the highlighted line, or null to use the theme's focused panel.</summary>
    public Color? HighlightColor { get; set; }

    /// <summary>The colour of the line numbers, or null to use the theme's muted text.</summary>
    public Color? GutterColor { get; set; }

    /// <summary>How tall one row is, in pixels.</summary>
    public int RowHeight => (BitmapFont.GlyphSize * _scale) + RowGap;

    /// <summary>How many rows the whole buffer takes at the width it was last laid out to.</summary>
    public int RowCount => _lineStartRow[^1];

    /// <summary>Rebuilds the row map on the next layout. Call after the buffer changes.</summary>
    public void Invalidate() => _revision++;

    /// <summary>The row <paramref name="line"/> starts at.</summary>
    public int RowOfLine(int line)
        => _lineStartRow.Length < 2 ? 0 : _lineStartRow[Math.Clamp(line, 0, _lineStartRow.Length - 2)];

    /// <summary>The line that <paramref name="row"/> belongs to.</summary>
    public int LineOfRow(int row)
    {
        if (_lineStartRow.Length < 2)
            return 0;

        int low = 0;
        int high = _lineStartRow.Length - 2;
        int best = 0;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (_lineStartRow[middle] <= row)
            {
                best = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return best;
    }

    /// <inheritdoc/>
    public override int Measure(int width, UiTheme theme)
    {
        Build(width);
        return Math.Max(RowHeight, RowCount * RowHeight);
    }

    /// <inheritdoc/>
    public override void Draw(Surface surface, UiTheme theme, UiElement? focused)
    {
        if (!Visible)
            return;

        // The row map belongs to the buffer that was there when the tree was laid out. A frame lays the
        // tree out, hands the buttons to it, and only then draws it, so an action that replaces the
        // buffer runs in between - and the map would then name lines the new buffer does not have.
        // Nothing is drawn until the next layout has rebuilt it, which is the very next frame.
        if (_revision != _builtRevision || _lineStartRow.Length != _lines.Count + 1)
            return;

        if (RowCount == 0)
            return;

        int rowHeight = RowHeight;
        int charWidth = BitmapFont.GlyphSize * _scale;

        // Only the rows inside the surface are drawn. Inside a scrolling window the surface is the
        // window and this control's top sits above it, which is what makes a large file affordable.
        int top = Math.Max(0, Bounds.Y);
        int bottom = Math.Min(surface.Height, Bounds.Y + Bounds.Height);
        if (bottom <= top)
            return;

        int firstRow = Math.Max(0, (top - Bounds.Y) / rowHeight);
        int lastRow = Math.Min(RowCount - 1, (bottom - 1 - Bounds.Y) / rowHeight);
        Color text = theme.Text;
        Color gutter = GutterColor ?? theme.TextMuted;
        Color band = HighlightColor ?? theme.PanelFocused;

        if (_gutterWidth > 0)
            surface.FillRect(Bounds.X + _gutterWidth - (charWidth / 2), top, 1, bottom - top, theme.Border);

        for (int row = firstRow; row <= lastRow; row++)
        {
            int y = Bounds.Y + (row * rowHeight);
            int line = LineOfRow(row);
            if (line == HighlightLine)
                surface.FillRect(Bounds.X, y, Bounds.Width, rowHeight, band);

            if (_gutterWidth > 0 && _lineStartRow[line] == row)
            {
                string number = (line + 1).ToString();
                int x = Bounds.X + _gutterWidth - (charWidth * 2) - Surface.MeasureText(number, _scale);
                surface.DrawText(number, x, y, _scale, gutter);
            }

            surface.DrawText(SegmentOf(row, line), Bounds.X + _gutterWidth, y, _scale, text);
        }
    }

    // Maps every line to its first row. Only the map is kept; the text of a row is built when drawn.
    private void Build(int width)
    {
        if (width == _builtWidth && _revision == _builtRevision)
            return;

        _builtWidth = width;
        _builtRevision = _revision;
        _segmentLine = -1;
        _segments.Clear();

        int charWidth = BitmapFont.GlyphSize * _scale;
        _gutterWidth = _showLineNumbers ? (DigitCount(_lines.Count) + 2) * charWidth : 0;
        _columns = Math.Max(1, (width - _gutterWidth) / charWidth);

        _lineStartRow = new int[_lines.Count + 1];
        int row = 0;
        for (int i = 0; i < _lines.Count; i++)
        {
            _lineStartRow[i] = row;
            row += CountRows(_lines[i]);
        }
        _lineStartRow[_lines.Count] = row;
    }

    // The text of one row. Rows of the same line are drawn one after another, so the segments of the
    // line last asked for are kept and the next row usually costs nothing.
    private string SegmentOf(int row, int line)
    {
        if (line != _segmentLine)
        {
            _segmentLine = line;
            _segments.Clear();
            Expand(_lines[line]);
            if (_wordWrap)
                WrapSegments(_segments);
            else
                _segments.Add(new string(_scratch, 0, _scratchLength));
        }

        int index = row - _lineStartRow[line];
        return index >= 0 && index < _segments.Count ? _segments[index] : string.Empty;
    }

    private int CountRows(string line)
    {
        if (!_wordWrap)
            return 1;
        Expand(line);
        return WrapSegments(null);
    }

    // Lays the line out into the scratch buffer: tabs become spaces up to the next stop, and a control
    // character becomes a full stop because the built-in text has no glyph for one and would leave a
    // gap that reads as an empty line.
    private void Expand(string line)
    {
        _scratchLength = 0;
        foreach (char c in line)
        {
            if (c == '\t')
            {
                int spaces = _tabWidth - (_scratchLength % _tabWidth);
                for (int i = 0; i < spaces; i++)
                    Append(' ');
                continue;
            }
            Append(c < ' ' || c == Delete ? '.' : c);
        }
    }

    private void Append(char c)
    {
        if (_scratchLength == _scratch.Length)
            Array.Resize(ref _scratch, _scratch.Length * 2);
        _scratch[_scratchLength++] = c;
    }

    // Breaks the scratch buffer into rows no wider than the column count, at the last space that fits so
    // a word is kept whole, and mid-word when a word alone is wider than the view. Returns how many rows
    // that comes to, and writes them out only when a list is given - which is what lets the row map be
    // built for the whole buffer without holding the text of every row.
    private int WrapSegments(List<string>? into)
    {
        if (_scratchLength == 0)
        {
            into?.Add(string.Empty);
            return 1;
        }

        int count = 0;
        int start = 0;
        while (start < _scratchLength)
        {
            if (_scratchLength - start <= _columns)
            {
                into?.Add(new string(_scratch, start, _scratchLength - start));
                return count + 1;
            }

            int end = start + _columns;
            int split = end;
            for (int i = end; i > start; i--)
            {
                if (_scratch[i - 1] == ' ')
                {
                    split = i;
                    break;
                }
            }

            into?.Add(new string(_scratch, start, split - start));
            count++;
            start = split;
        }
        return count;
    }

    private static int DigitCount(int value)
    {
        int digits = 1;
        while (value >= 10)
        {
            value /= 10;
            digits++;
        }
        return digits;
    }
}

/// <summary>
/// Shows a text file and lets it be changed a line at a time. Triangle opens the actions; the line the
/// actions apply to is the highlighted one, moved with the shoulder buttons and dragged along by the
/// view when the file is scrolled.
/// </summary>
/// <remarks>
/// The keyboard is the only way to type on this machine and it returns one field at a time, so editing
/// is by whole lines rather than by character. That is also why every action that changes the buffer
/// names the line it changed.
/// </remarks>
internal sealed class TextScreen : ExplorerScreen
{
    // A jump is applied for a few frames because the row map is only correct once the tree has been
    // laid out, which happens when the frame is drawn - after the action that asked for the jump ran.
    private const int JumpFrames = 3;

    // The keyboard's own ceiling.
    private const int MaxEntryLength = 2048;

    // A hex dump is about four times the size of what it shows, so only the first part of a large file
    // is dumped. The footer says how much is shown.
    private const int MaxHexBytes = 128 * 1024;

    // How much of a file is decoded to decide whether it is text at all, before the whole of it is
    // turned into a string, so a large file that is not text is never fully decoded to be thrown away.
    private const int BinaryProbeBytes = 16 * 1024;

    // A table preview lays a value out in fixed columns; these bound how wide one column grows and how
    // many columns are shown, so a wide file stays readable and a row does not run far off the view.
    private const int MaxTableColumnWidth = 20;
    private const int MaxTableColumns = 16;

    // Decoding stands this in for a byte that is not valid text, which is what gives a file that is not
    // text away.
    private const char Replacement = (char)0xFFFD;

    // The delete character has no glyph and is grouped with the control characters wherever one is
    // flattened out of a value.
    private const char Delete = (char)0x7F;

    // What a save is written under until all of it is down. It lives beside the file it replaces and
    // only for as long as the save takes.
    private const string PartSuffix = ".part";

    private readonly List<string> _lines = [];
    private readonly TextCanvas _canvas = new();
    private readonly ScrollView _view;
    private readonly ModalHost _host;
    private readonly Label _heading = new();
    private readonly Label _footer = new();
    private readonly Separator _aboveText = new();
    private readonly Separator _belowText = new();

    private string _path;
    private string _find = "";
    private long _fileSize;
    private long _shownBytes;
    private long _characters;
    private int _cursor;
    private int _jumpFrames;
    private bool _jumpToMiddle;
    private bool _dirty;
    private bool _hex;
    private bool _table;
    private bool _partial;
    private bool _tooLarge;
    private bool _askedAboutSize;
    private bool _leaving;

    // The file's own line ending and encoding, remembered on load so a save writes the file back the
    // same way rather than silently changing every line ending or the encoding.
    private string _newline = "\n";
    private SourceEncoding _encoding = SourceEncoding.Utf8;

    // The editable text set aside while the read-only table view is showing, with the state to put back
    // when it closes, so previewing a file as a table loses none of the work in progress.
    private List<string>? _editBackup;
    private bool _editBackupDirty;
    private int _editBackupCursor;

    // How the bytes are read as text. Kept so a save can reproduce the file's encoding and its mark.
    private enum SourceEncoding
    {
        Utf8,
        Utf8Bom,
        Utf16Le,
        Utf16Be,
    }

    /// <summary>Opens <paramref name="filePath"/> in the viewer.</summary>
    public TextScreen(ExplorerShell shell, string filePath) : base(shell)
    {
        _path = filePath;

        _canvas.Lines = _lines;
        _canvas.WordWrap = Shell.Settings.TextWordWrap;
        _canvas.Scale = Shell.Settings.TextScale;
        _canvas.TabWidth = Shell.Settings.TextTabWidth;
        _canvas.HighlightColor = Shell.Theme.PanelFocused;
        _canvas.GutterColor = Shell.Theme.TextMuted;

        _view = new ScrollView(_canvas) { ViewHeight = 560, ScrollStep = _canvas.RowHeight };
        _host = new ModalHost(BuildBody()) { ModalWidthFraction = 0.6f };

        _heading.TextColor = Shell.Theme.TextMuted;
        _footer.TextColor = Shell.Theme.TextMuted;

        Load();
    }

    /// <inheritdoc/>
    public override string Title
    {
        get
        {
            string name = PathUtil.GetFileName(_path);
            if (name.Length == 0)
                name = _path;
            return _dirty ? name + " (modified)" : name;
        }
    }

    /// <inheritdoc/>
    public override string Hint => "Triangle for actions, L1/R1 line, L2/R2 page, Circle goes back.";

    /// <inheritdoc/>
    protected override UiElement BuildRoot() => _host;

    // The hex and table views show the file without letting it be changed, so the actions that edit or
    // save the buffer are held back while either is on screen.
    private bool ReadOnly => _hex || _table;

    /// <inheritdoc/>
    public override void OnShown()
    {
        if (_askedAboutSize || !_tooLarge)
            return;

        // Asked once, and only when the page is actually in front, so the question cannot arrive while
        // another page is still showing.
        _askedAboutSize = true;
        long limit = TextLimitBytes();
        Shell.Dialogs.Confirm(
            $"{PathUtil.GetFileName(_path)} is {TextFormat.ByteSize(_fileSize)}, larger than the "
            + $"{TextFormat.ByteSize(limit)} the viewer opens.\n\nShow the first {TextFormat.ByteSize(limit)}?",
            yes =>
            {
                if (yes)
                    LoadFirstPart(limit);
                else
                    Shell.Pop();
            });
    }

    /// <inheritdoc/>
    public override void Tick(FrameContext context)
    {
        FitView();

        if (_jumpFrames > 0)
        {
            _jumpFrames--;
            ApplyJump();
        }
        else
        {
            FollowView();
        }

        _canvas.HighlightLine = _cursor;
        UpdateCaptions();

        // The shell keeps giving this page the frame while an overlay is up so it can carry on with its
        // own work, but the buttons belong to the overlay while it is showing.
        if (Shell.Dialogs.IsBusy)
            return;

        HandleButtons(context);
    }

    /// <inheritdoc/>
    protected override bool OnCancel()
    {
        if (_host.IsOpen)
        {
            _host.Close();
            return true;
        }

        // The table view is a way of reading the file, not a page of its own, so going back from it
        // returns to the text it was made from rather than leaving the file.
        if (_table)
        {
            HideTable();
            return true;
        }

        if (!_dirty || _leaving)
            return false;

        _leaving = true;
        Shell.Dialogs.Confirm(
            $"{PathUtil.GetFileName(_path)} has changes that are not saved.\n\nLeave without saving?",
            yes =>
            {
                if (yes)
                    Shell.Pop();
                else
                    _leaving = false;
            });
        return true;
    }

    /// <inheritdoc/>
    protected override void OnDispose()
    {
        // The buffer can be tens of megabytes and the page may be closed long before the next
        // collection, so it is dropped here rather than left to the object.
        _lines.Clear();
        _lines.TrimExcess();
        _canvas.Lines = [];
    }

    private UiElement BuildBody()
        => new StackPanel()
            .Add(_heading)
            .Add(_aboveText)
            .Add(_view)
            .Add(_belowText)
            .Add(_footer);

    // Loading.

    private long TextLimitBytes() => (long)Shell.Settings.TextMaxKilobytes * 1024;

    private void Load()
    {
        _lines.Clear();
        _dirty = false;
        _partial = false;
        _tooLarge = false;
        _hex = false;
        _table = false;
        _cursor = 0;
        _shownBytes = 0;
        _newline = "\n";
        _encoding = SourceEncoding.Utf8;
        _editBackup = null;

        // A fresh load is the text at the view's own size and wrap; the hex and table views change these,
        // and this is where they are put back.
        _canvas.ShowLineNumbers = true;
        _canvas.WordWrap = Shell.Settings.TextWordWrap;

        try
        {
            _fileSize = FileSystem.GetFileSize(_path);
        }
        catch (Exception error)
        {
            _fileSize = 0;
            Shell.ReportFailure($"{PathUtil.GetFileName(_path)} could not be opened", error, important: true);
            Adopt([""]);
            return;
        }

        if (_fileSize > TextLimitBytes())
        {
            // The question is put when the page comes to the front; until then there is nothing to show.
            _tooLarge = true;
            Adopt([$"{PathUtil.GetFileName(_path)} is {TextFormat.ByteSize(_fileSize)}."]);
            return;
        }

        byte[] data;
        try
        {
            // The size check above has already turned away anything past the limit, so reading the whole
            // of what is left never pulls more than the cap into memory.
            data = ReadPrefix(_path, _fileSize);
        }
        catch (Exception error)
        {
            Shell.ReportFailure($"{PathUtil.GetFileName(_path)} could not be read", error, important: true);
            Adopt([""]);
            return;
        }

        LoadFromBytes(data, partial: false);
    }

    private void LoadFirstPart(long limit)
    {
        byte[] data;
        try
        {
            // Only the part that is shown is read off the device, so a file far larger than the heap can
            // still be opened at its front rather than being pulled in whole and then cut down.
            data = ReadPrefix(_path, limit);
        }
        catch (Exception error)
        {
            Shell.ReportFailure($"{PathUtil.GetFileName(_path)} could not be read", error, important: true);
            Adopt([""]);
            return;
        }

        // The read stops part way through the file, so step the end back to a whole-character boundary
        // for the encoding the front of the file declares and keep only the bytes up to it.
        SourceEncoding encoding = DetectEncoding(data, out int bom);
        int take = StepBackToBoundary(data, data.Length, encoding, bom);
        if (take < data.Length)
            Array.Resize(ref data, take);

        LoadFromBytes(data, partial: true);
        if (!_hex)
            Shell.Notify($"Showing the first {TextFormat.ByteSize(data.Length)} of {PathUtil.GetFileName(_path)}.");
    }

    // Turns the bytes that were read into either text or, when they are not text, a hex view. The text
    // check reads only a bounded window, so a large file that turns out not to be text is not fully
    // decoded to a string only to be thrown away.
    private void LoadFromBytes(byte[] data, bool partial)
    {
        _encoding = DetectEncoding(data, out int bom);
        int contentLength = data.Length - bom;

        int probeBytes = Math.Min(contentLength, BinaryProbeBytes);
        if ((_encoding is SourceEncoding.Utf16Le or SourceEncoding.Utf16Be) && (probeBytes & 1) != 0)
            probeBytes--;

        if (LooksBinary(Decode(data, bom, probeBytes)))
        {
            // The very bytes just read are shown as hex, so the read that found the file was not text is
            // not made a second time to show it.
            ShowHexFrom(data, partial);
            Shell.Status("This file is not text. Showing it as hex.");
            return;
        }

        string text = Decode(data, bom, contentLength);
        _newline = DetectNewline(text);
        _hex = false;
        _table = false;
        _tooLarge = false;
        _partial = partial;
        _shownBytes = data.Length;
        _canvas.ShowLineNumbers = true;
        Adopt(Split(text));
    }

    private void ShowHex()
    {
        byte[] data;
        try
        {
            // A hex view holds only its own first bytes, so only those are read rather than the whole
            // file behind them.
            data = ReadPrefix(_path, Math.Min(_fileSize, MaxHexBytes));
        }
        catch (Exception error)
        {
            Shell.ReportFailure($"{PathUtil.GetFileName(_path)} could not be read", error, important: true);
            return;
        }

        ShowHexFrom(data, sourcePartial: false);
    }

    private void ShowHexFrom(byte[] data, bool sourcePartial)
    {
        int take = Math.Min(data.Length, MaxHexBytes);
        string dump;
        try
        {
            dump = TextFormat.HexDump(data.AsSpan(0, take));
        }
        catch (Exception error)
        {
            Shell.ReportFailure($"{PathUtil.GetFileName(_path)} could not be shown as hex", error);
            return;
        }

        _hex = true;
        _table = false;
        _dirty = false;
        _tooLarge = false;
        // The dump falls short of the file when the file itself was only read in part, or when the dump
        // was capped at what the hex view holds.
        _partial = sourcePartial || take < _fileSize;
        _shownBytes = take;

        // The offset column already carries the position, so a line number beside it says nothing.
        _canvas.ShowLineNumbers = false;
        Adopt(Split(dump));
    }

    private void ShowText()
    {
        _canvas.ShowLineNumbers = true;
        _canvas.WordWrap = Shell.Settings.TextWordWrap;
        Load();
    }

    // Reads up to <paramref name="take"/> bytes from the front of the file into one array, in pieces, so
    // nothing larger than that is held. A file shorter than asked for returns what there was.
    private static byte[] ReadPrefix(string path, long take)
    {
        if (take <= 0)
            return [];

        int want = (int)Math.Min(take, int.MaxValue);
        byte[] buffer = new byte[want];
        int filled = 0;
        using DeviceFileStream stream = FileSystem.OpenRead(path);
        while (filled < want)
        {
            int read = stream.Read(buffer, filled, want - filled);
            if (read <= 0)
                break;
            filled += read;
        }

        if (filled != buffer.Length)
            Array.Resize(ref buffer, filled);
        return buffer;
    }

    // The byte-order mark at the front tells the encoding apart; without one the bytes are read as UTF-8,
    // which also covers plain 7-bit text.
    private static SourceEncoding DetectEncoding(byte[] data, out int bomLength)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            bomLength = 3;
            return SourceEncoding.Utf8Bom;
        }
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            bomLength = 2;
            return SourceEncoding.Utf16Le;
        }
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            bomLength = 2;
            return SourceEncoding.Utf16Be;
        }
        bomLength = 0;
        return SourceEncoding.Utf8;
    }

    private string Decode(byte[] data, int start, int count)
        => _encoding switch
        {
            SourceEncoding.Utf16Le => Encoding.Unicode.GetString(data, start, count),
            SourceEncoding.Utf16Be => Encoding.BigEndianUnicode.GetString(data, start, count),
            _ => Encoding.UTF8.GetString(data, start, count),
        };

    private byte[] Encode(string text)
    {
        byte[] mark;
        byte[] body;
        switch (_encoding)
        {
            case SourceEncoding.Utf16Le:
                mark = new byte[] { 0xFF, 0xFE };
                body = Encoding.Unicode.GetBytes(text);
                break;
            case SourceEncoding.Utf16Be:
                mark = new byte[] { 0xFE, 0xFF };
                body = Encoding.BigEndianUnicode.GetBytes(text);
                break;
            case SourceEncoding.Utf8Bom:
                mark = new byte[] { 0xEF, 0xBB, 0xBF };
                body = Encoding.UTF8.GetBytes(text);
                break;
            default:
                return Encoding.UTF8.GetBytes(text);
        }

        byte[] result = new byte[mark.Length + body.Length];
        mark.CopyTo(result, 0);
        body.CopyTo(result, mark.Length);
        return result;
    }

    // Finds the last whole-character boundary at or before <paramref name="take"/>, so a read that
    // stopped part way through the file does not end in half a character.
    private static int StepBackToBoundary(byte[] data, int take, SourceEncoding encoding, int bom)
    {
        if (take > data.Length)
            take = data.Length;
        if (take <= bom)
            return Math.Max(bom, 0);

        if (encoding is SourceEncoding.Utf16Le or SourceEncoding.Utf16Be)
        {
            // A code unit is two bytes; keep an even number of them after the mark.
            if (((take - bom) & 1) != 0)
                take--;
            return take;
        }

        // UTF-8: a byte of the form 10xxxxxx continues the character before it. Step back to the last
        // lead byte, then keep its character only when all of its bytes are present.
        int i = take - 1;
        while (i >= bom && (data[i] & 0xC0) == 0x80)
            i--;
        if (i < bom)
            return take;

        int lead = data[i];
        int length =
            (lead & 0x80) == 0x00 ? 1 :
            (lead & 0xE0) == 0xC0 ? 2 :
            (lead & 0xF0) == 0xE0 ? 3 :
            (lead & 0xF8) == 0xF0 ? 4 : 1;
        return take - i >= length ? take : i;
    }

    // Remembers the file's own line ending from the first one seen, so a save writes them all back the
    // same way rather than turning every ending in the file into a bare newline.
    private static string DetectNewline(string text)
    {
        int at = text.IndexOf('\n');
        if (at < 0)
            return "\n";
        return at > 0 && text[at - 1] == '\r' ? "\r\n" : "\n";
    }

    private void Adopt(IReadOnlyList<string> lines)
    {
        _lines.Clear();
        for (int i = 0; i < lines.Count; i++)
            _lines.Add(lines[i]);
        if (_lines.Count == 0)
            _lines.Add("");

        _cursor = Math.Clamp(_cursor, 0, _lines.Count - 1);
        _view.ScrollToTop();
        MarkBufferChanged(dirty: false);
    }

    // Splits into lines on the newline and drops a trailing carriage return, so the line ending, however
    // it is written, does not show up as a stray character. The kind that was there is remembered
    // separately and put back on save.
    private static List<string> Split(string text)
    {
        var lines = new List<string>();
        foreach (string line in text.Split('\n'))
            lines.Add(line.TrimEnd('\r'));
        return lines;
    }

    // Decoding turns a byte that is not valid text into the replacement character, so a file that is not
    // text shows up as a run of them or as embedded zeroes.
    private static bool LooksBinary(string text)
    {
        int limit = Math.Min(text.Length, 4096);
        if (limit == 0)
            return false;

        int suspicious = 0;
        for (int i = 0; i < limit; i++)
        {
            char c = text[i];
            if (c == '\0')
                return true;
            if (c == Replacement || (c < ' ' && c != '\t' && c != '\n' && c != '\r'))
                suspicious++;
        }
        return suspicious * 100 / limit > 5;
    }

    // The table preview.

    // The comma- and tab-separated files that the table view can lay out, told apart by their suffix.
    private bool IsTabular
    {
        get
        {
            string extension = PathUtil.GetExtension(_path).ToLowerInvariant();
            return extension is ".csv" or ".tsv";
        }
    }

    private void EnterTable()
    {
        _host.Close();
        ShowTable();
    }

    private void LeaveTable()
    {
        _host.Close();
        HideTable();
    }

    // Lays the values out in aligned columns for reading. The editable text is set aside so returning to
    // it loses nothing, and the view itself does not change the file.
    private void ShowTable()
    {
        if (_table || _hex)
            return;

        List<string>? built = BuildTablePreview(out string note);
        if (built is null)
        {
            Shell.Status(note);
            return;
        }

        _editBackup = new List<string>(_lines);
        _editBackupDirty = _dirty;
        _editBackupCursor = _cursor;

        _table = true;
        _canvas.ShowLineNumbers = false;
        _canvas.WordWrap = false;
        Adopt(built);
        Shell.Status("Showing this file as a table. Show as text to edit it again.");
    }

    private void HideTable()
    {
        if (!_table)
            return;

        List<string> restore = _editBackup ?? [""];
        _editBackup = null;
        _table = false;
        _canvas.ShowLineNumbers = true;
        _canvas.WordWrap = Shell.Settings.TextWordWrap;

        _lines.Clear();
        _lines.AddRange(restore);
        if (_lines.Count == 0)
            _lines.Add("");

        _cursor = Math.Clamp(_editBackupCursor, 0, _lines.Count - 1);
        _view.ScrollToTop();
        MarkBufferChanged(_editBackupDirty);
        RequestJump(toMiddle: false);
    }

    // Builds the rows of the table view from what is loaded, with the separator the file's suffix names.
    // Returns null with a reason when there is nothing to lay out.
    private List<string>? BuildTablePreview(out string note)
    {
        note = "";
        char separator = PathUtil.GetExtension(_path).Equals(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : ',';

        DataTable table;
        try
        {
            table = DataTable.FromCsv(string.Join("\n", _lines), hasHeader: true, separator: separator);
        }
        catch (ArgumentException)
        {
            note = "There are no rows to show as a table.";
            return null;
        }

        int columns = Math.Min(table.ColumnCount, MaxTableColumns);
        int[] widths = new int[columns];
        for (int c = 0; c < columns; c++)
            widths[c] = ColumnWidth(CellText(table.Columns[c]).Length);
        for (int r = 0; r < table.RowCount; r++)
        {
            for (int c = 0; c < columns; c++)
                widths[c] = Math.Max(widths[c], ColumnWidth(CellText(table[r, c]).Length));
        }

        var lines = new List<string>(table.RowCount + 2)
        {
            ComposeRow(c => table.Columns[c], columns, widths, table.ColumnCount),
            ComposeRule(columns, widths, table.ColumnCount),
        };
        for (int r = 0; r < table.RowCount; r++)
        {
            int row = r;
            lines.Add(ComposeRow(c => table[row, c], columns, widths, table.ColumnCount));
        }
        return lines;

        static int ColumnWidth(int length) => Math.Clamp(length, 1, MaxTableColumnWidth);
    }

    private static string ComposeRow(Func<int, string> cell, int columns, int[] widths, int totalColumns)
    {
        var builder = new StringBuilder();
        for (int c = 0; c < columns; c++)
        {
            if (c > 0)
                builder.Append(" | ");
            builder.Append(Fit(CellText(cell(c)), widths[c]));
        }
        if (totalColumns > columns)
            builder.Append(" | ...");
        return builder.ToString();
    }

    private static string ComposeRule(int columns, int[] widths, int totalColumns)
    {
        var builder = new StringBuilder();
        for (int c = 0; c < columns; c++)
        {
            if (c > 0)
                builder.Append("-+-");
            builder.Append('-', widths[c]);
        }
        if (totalColumns > columns)
            builder.Append("-+----");
        return builder.ToString();
    }

    // Pads a value to the column width, or cuts it and marks the cut so a shortened value is not taken
    // for a whole one.
    private static string Fit(string text, int width)
    {
        if (text.Length == width)
            return text;
        if (text.Length < width)
            return text.PadRight(width);
        return width <= 3 ? text[..width] : text[..(width - 3)] + "...";
    }

    // Flattens a cell to one line by turning any control character into a space, so a value that held a
    // line break or a tab keeps the columns in their places rather than breaking the row.
    private static string CellText(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "";

        char[]? cleaned = null;
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c >= ' ' && c != Delete)
                continue;
            cleaned ??= raw.ToCharArray();
            cleaned[i] = ' ';
        }
        return cleaned is null ? raw : new string(cleaned);
    }

    // Layout, view and cursor.

    // The window is as tall as what is left after the heading, the rules and the footer, worked out from
    // the area the shell actually gave the page rather than from a fixed figure.
    private void FitView()
    {
        int width = _host.Bounds.Width;
        int height = _host.Bounds.Height;
        if (width <= 0 || height <= 0)
            return;

        UiTheme theme = Shell.Theme;
        int chrome = _heading.Measure(width, theme)
            + _aboveText.Measure(width, theme)
            + _belowText.Measure(width, theme)
            + _footer.Measure(width, theme)
            + (4 * theme.Spacing);

        int view = Math.Max(theme.RowHeight, height - chrome);
        if (_view.ViewHeight != view)
            _view.ViewHeight = view;

        int step = _canvas.RowHeight;
        if (_view.ScrollStep != step)
            _view.ScrollStep = step;
    }

    private void RequestJump(bool toMiddle)
    {
        _jumpToMiddle = toMiddle;
        _jumpFrames = JumpFrames;
    }

    private void ApplyJump()
    {
        int rowHeight = _canvas.RowHeight;
        int top = _canvas.RowOfLine(_cursor) * rowHeight;

        if (_jumpToMiddle)
        {
            _view.ScrollBy(top - (_view.ViewHeight / 3) - _view.ScrollOffset);
            return;
        }

        int bottom = top + rowHeight;
        if (top < _view.ScrollOffset)
            _view.ScrollBy(top - _view.ScrollOffset);
        else if (bottom > _view.ScrollOffset + _view.ViewHeight)
            _view.ScrollBy(bottom - _view.ScrollOffset - _view.ViewHeight);
    }

    // Scrolling with the pad drags the highlighted line along, so the line the actions apply to is
    // always one the user can see.
    private void FollowView()
    {
        if (_lines.Count == 0)
            return;

        int rowHeight = _canvas.RowHeight;
        int firstRow = _view.ScrollOffset / rowHeight;
        int lastRow = Math.Max(firstRow, (_view.ScrollOffset + _view.ViewHeight - 1) / rowHeight);
        _cursor = Math.Clamp(_cursor, _canvas.LineOfRow(firstRow), _canvas.LineOfRow(lastRow));
    }

    private void MoveCursor(int delta)
    {
        int target = Math.Clamp(_cursor + delta, 0, Math.Max(0, _lines.Count - 1));
        if (target == _cursor)
            return;
        _cursor = target;
        RequestJump(toMiddle: false);
    }

    private void HandleButtons(FrameContext context)
    {
        if (context.Pressed(ScePadButton.Triangle))
        {
            if (_host.IsOpen)
                _host.Close();
            else
                OpenMenu();
            return;
        }

        if (_host.IsOpen)
            return;

        int page = Math.Max(1, _view.ViewHeight / Math.Max(1, _canvas.RowHeight));
        if (context.Pressed(ScePadButton.L1))
            MoveCursor(-1);
        else if (context.Pressed(ScePadButton.R1))
            MoveCursor(+1);
        else if (context.Pressed(ScePadButton.L2))
            MoveCursor(-page);
        else if (context.Pressed(ScePadButton.R2))
            MoveCursor(+page);
    }

    private void UpdateCaptions()
    {
        if (_hex)
        {
            _heading.Text = _partial
                ? $"{_path} - hex, first {TextFormat.ByteSize(_shownBytes)} of {TextFormat.ByteSize(_fileSize)}"
                : $"{_path} - hex";
        }
        else if (_table)
        {
            _heading.Text = _partial
                ? $"{_path} - table, first {TextFormat.ByteSize(_shownBytes)} of {TextFormat.ByteSize(_fileSize)}"
                : $"{_path} - table";
        }
        else
        {
            _heading.Text = _partial
                ? $"{_path} - first {TextFormat.ByteSize(_shownBytes)} of {TextFormat.ByteSize(_fileSize)} shown"
                : _path;
        }

        string state = _dirty ? "  -  not saved" : "";
        _footer.Text = $"Line {_cursor + 1} of {_lines.Count}  -  {_characters} characters  -  "
            + $"{TextFormat.ByteSize(_fileSize)}{state}";
    }

    private void MarkBufferChanged(bool dirty)
    {
        _dirty = dirty;
        long characters = 0;
        foreach (string line in _lines)
            characters += line.Length;
        _characters = characters + Math.Max(0, _lines.Count - 1);

        _canvas.Invalidate();
        _cursor = Math.Clamp(_cursor, 0, Math.Max(0, _lines.Count - 1));
    }

    // The actions.

    private void OpenMenu()
    {
        UiTheme theme = Shell.Theme;
        var menu = new ScrollMenu();

        if (!ReadOnly)
        {
            menu.Add(new Button("Edit line", EditLine));
            menu.Add(new Button("Insert line above", InsertLine));
            menu.Add(new Button("Delete line", DeleteLine));
            menu.Add(new Button("Append line at the end", AppendLine));
            menu.Add(new Separator());
        }

        menu.Add(new Button("Find", Find));
        menu.Add(new Button("Find again", FindAgain) { Enabled = _find.Length > 0 });
        menu.Add(new Button("Go to line", GoToLine));
        menu.Add(new Separator());

        if (!ReadOnly)
        {
            menu.Add(new Button("Save", Save));
            menu.Add(new Button("Save as", SaveAs));
        }

        menu.Add(new Button("Reload", Reload));
        if (_table)
            menu.Add(new Button("Show as text", LeaveTable));
        else
            menu.Add(new Button(_hex ? "Show as text" : "Show as hex", ToggleHex));
        if (!_hex && !_table && !_tooLarge && IsTabular)
            menu.Add(new Button("Show as table", EnterTable));
        menu.Add(new Separator());
        menu.Add(new Button(Shell.Settings.TextWordWrap ? "Word wrap off" : "Word wrap on", ToggleWordWrap));
        menu.Add(new Button("Text size", TextSize));

        // Eight rows at a time, which leaves the panel well inside the screen at every text size.
        const int rows = 8;
        menu.ViewHeight = (rows * theme.RowHeight) + ((rows - 1) * theme.Spacing);

        _host.Show(new StackPanel()
            .Add(new Label("Actions"))
            .Add(new Separator())
            .Add(menu));
    }

    private void EditLine()
    {
        _host.Close();
        int line = _cursor;
        if (line < 0 || line >= _lines.Count)
            return;

        string current = _lines[line];
        if (current.Length > MaxEntryLength)
        {
            // The keyboard fills a buffer of a fixed size and cuts anything past it, so a line it
            // cannot hold would come back shortened without a word.
            Shell.Dialogs.Alert(
                $"Line {line + 1} is {current.Length} characters, longer than the "
                + $"{MaxEntryLength} the keyboard holds, so it cannot be edited here.");
            return;
        }

        Shell.Dialogs.AskText(
            $"Line {line + 1}",
            current,
            text =>
            {
                if (text is null || text == current)
                {
                    Shell.Status("Line left as it was.");
                    return;
                }
                _lines[line] = text;
                MarkBufferChanged(dirty: true);
                Shell.Notify($"Line {line + 1} changed.");
            },
            EntryLength(current));
    }

    private void InsertLine()
    {
        _host.Close();
        int line = Math.Clamp(_cursor, 0, _lines.Count);
        Shell.Dialogs.AskText(
            $"New line above line {line + 1}",
            "",
            text =>
            {
                if (text is null)
                {
                    Shell.Status("Nothing inserted.");
                    return;
                }
                _lines.Insert(line, text);
                _cursor = line;
                MarkBufferChanged(dirty: true);
                RequestJump(toMiddle: false);
                Shell.Notify($"Line inserted at line {line + 1}.");
            },
            MaxEntryLength);
    }

    private void DeleteLine()
    {
        _host.Close();
        int line = _cursor;
        if (line < 0 || line >= _lines.Count)
            return;

        if (_lines.Count == 1)
        {
            Shell.Status("The last line cannot be deleted.");
            return;
        }

        if (!Shell.Settings.ConfirmDestructive)
        {
            RemoveLine(line);
            return;
        }

        Shell.Dialogs.Confirm($"Delete line {line + 1}?", yes =>
        {
            if (yes)
                RemoveLine(line);
            else
                Shell.Status("Nothing deleted.");
        });
    }

    private void RemoveLine(int line)
    {
        if (line < 0 || line >= _lines.Count)
            return;
        _lines.RemoveAt(line);
        _cursor = Math.Clamp(line, 0, _lines.Count - 1);
        MarkBufferChanged(dirty: true);
        RequestJump(toMiddle: false);
        Shell.Notify($"Line {line + 1} deleted.");
    }

    private void AppendLine()
    {
        _host.Close();
        Shell.Dialogs.AskText(
            "New line at the end",
            "",
            text =>
            {
                if (text is null)
                {
                    Shell.Status("Nothing appended.");
                    return;
                }
                _lines.Add(text);
                _cursor = _lines.Count - 1;
                MarkBufferChanged(dirty: true);
                RequestJump(toMiddle: true);
                Shell.Notify($"Line {_lines.Count} appended.");
            },
            MaxEntryLength);
    }

    private void Find()
    {
        _host.Close();
        Shell.Dialogs.AskText(
            "Find",
            _find,
            text =>
            {
                if (string.IsNullOrEmpty(text))
                {
                    Shell.Status("Nothing to find.");
                    return;
                }
                _find = text;
                JumpToMatch(_cursor);
            },
            128);
    }

    private void FindAgain()
    {
        _host.Close();
        if (_find.Length == 0)
        {
            Shell.Status("Nothing to find.");
            return;
        }
        JumpToMatch(_cursor + 1);
    }

    // Searches on from the given line and wraps round, so "find again" walks every match and comes back
    // to the first.
    private void JumpToMatch(int from)
    {
        if (_lines.Count == 0)
            return;

        int start = ((from % _lines.Count) + _lines.Count) % _lines.Count;
        for (int step = 0; step < _lines.Count; step++)
        {
            int line = (start + step) % _lines.Count;
            if (_lines[line].Contains(_find, StringComparison.OrdinalIgnoreCase))
            {
                _cursor = line;
                RequestJump(toMiddle: true);
                Shell.Status($"Found on line {line + 1}.");
                return;
            }
        }
        Shell.Status($"{_find} is not in this file.");
    }

    private void GoToLine()
    {
        _host.Close();
        Shell.Dialogs.AskText(
            $"Go to line, 1 to {_lines.Count}",
            (_cursor + 1).ToString(),
            text =>
            {
                if (text is null)
                    return;
                if (!int.TryParse(text.Trim(), out int line) || line < 1 || line > _lines.Count)
                {
                    Shell.Status($"There is no line {text.Trim()}.");
                    return;
                }
                _cursor = line - 1;
                RequestJump(toMiddle: true);
                Shell.Status($"Line {line}.");
            },
            16,
            ImeType.Number);
    }

    private void Save()
    {
        _host.Close();
        if (_hex)
        {
            Shell.Status("The hex view cannot be saved.");
            return;
        }

        if (_partial)
        {
            // Writing the buffer back would drop everything past the part that was loaded.
            Shell.Dialogs.Alert(
                "Only the first part of this file is loaded, so saving would shorten it.\n\n"
                + "Use Save as to write what is shown to another file.");
            return;
        }

        if (!Shell.Settings.ConfirmDestructive)
        {
            WriteTo(_path);
            return;
        }

        Shell.Dialogs.Confirm($"Overwrite {PathUtil.GetFileName(_path)}?", yes =>
        {
            if (yes)
                WriteTo(_path);
            else
                Shell.Status("Not saved.");
        });
    }

    private void SaveAs()
    {
        _host.Close();
        if (_hex)
        {
            Shell.Status("The hex view cannot be saved.");
            return;
        }

        Shell.Dialogs.AskText(
            "Save as",
            _path,
            text =>
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    Shell.Status("Not saved.");
                    return;
                }

                // A bare name is taken as being beside the file that is open, which is where the user
                // is looking. Falling back to the application's own folder would put it somewhere they
                // would have to go and find, and that folder may not exist at all.
                string target = text.Trim();
                if (!PathUtil.IsAbsolute(target))
                {
                    string folder = PathUtil.GetDirectoryName(_path);
                    target = PathUtil.Combine(string.IsNullOrEmpty(folder) ? "/" : folder, target);
                }
                ConfirmThenWrite(target);
            },
            512);
    }

    private void ConfirmThenWrite(string target)
    {
        bool exists;
        try
        {
            exists = FileSystem.Exists(target);
        }
        catch (Exception)
        {
            // Whether it is there cannot be told, so treat it as present and ask.
            exists = true;
        }

        if (!exists || !Shell.Settings.ConfirmDestructive)
        {
            WriteTo(target);
            return;
        }

        Shell.Dialogs.Confirm($"{PathUtil.GetFileName(target)} already exists. Overwrite it?", yes =>
        {
            if (yes)
                WriteTo(target);
            else
                Shell.Status("Not saved.");
        });
    }

    private void WriteTo(string target)
    {
        // Writing over the file empties it before the first byte goes out, so a write that stops part
        // way - the storage full, the mount gone - would leave nothing of what was there. The text goes
        // to a name beside it instead, in the same folder so that putting it in place is a rename
        // rather than a copy, and the original stands until every byte is down.
        string part = target + PartSuffix;
        try
        {
            string folder = PathUtil.GetDirectoryName(target);
            if (folder.Length > 0)
                FileSystem.CreateDirectoryRecursive(folder);
            // The lines are joined with the file's own ending and written in the file's own encoding, so
            // a save changes only the lines that were edited, not every ending or the encoding.
            FileSystem.WriteAllBytes(part, Encode(string.Join(_newline, _lines)));
        }
        catch (Exception error)
        {
            Discard(part);
            Shell.ReportFailure(
                $"{PathUtil.GetFileName(target)} could not be saved, and is as it was", error, important: true);
            return;
        }

        try
        {
            FileSystem.Move(part, target);
        }
        catch (Exception error)
        {
            // The original is whole and the new text is written, so the user is told where it is rather
            // than being left to guess which of the two they have.
            Shell.ReportFailure(
                $"{PathUtil.GetFileName(target)} could not be replaced. What was saved is in {part}",
                error,
                important: true);
            return;
        }

        // What was written is now the whole of the file at this path, however little of the original was
        // loaded, so the viewer stops treating it as a part.
        _path = target;
        _dirty = false;
        _partial = false;
        try
        {
            _fileSize = FileSystem.GetFileSize(_path);
            _shownBytes = _fileSize;
        }
        catch (Exception)
        {
            // The bytes went out; only the figure in the footer is stale, and the next load corrects it.
        }
        Shell.Notify($"Saved {PathUtil.GetFileName(_path)}.");
    }

    // Takes away the half-written file after a save that failed.
    private static void Discard(string path)
    {
        try
        {
            FileSystem.DeleteFile(path);
        }
        catch (Exception)
        {
            // It holds nothing anyone asked for and the file it was to replace is untouched, so a
            // failure to remove it changes nothing the user needs to hear about.
        }
    }

    private void Reload()
    {
        _host.Close();
        if (!_dirty || !Shell.Settings.ConfirmDestructive)
        {
            DoReload();
            return;
        }

        Shell.Dialogs.Confirm("Reload and lose the changes that are not saved?", yes =>
        {
            if (yes)
                DoReload();
            else
                Shell.Status("Not reloaded.");
        });
    }

    private void DoReload()
    {
        _canvas.ShowLineNumbers = true;
        _askedAboutSize = false;
        Load();
        OnShown();
        if (!_tooLarge)
            Shell.Notify($"Reloaded {PathUtil.GetFileName(_path)}.");
    }

    private void ToggleHex()
    {
        _host.Close();
        if (_hex)
        {
            ShowText();
            return;
        }

        if (!_dirty)
        {
            ShowHex();
            return;
        }

        Shell.Dialogs.Confirm("Showing this file as hex loses the changes that are not saved.\n\nCarry on?", yes =>
        {
            if (yes)
                ShowHex();
            else
                Shell.Status("Still showing the text.");
        });
    }

    private void ToggleWordWrap()
    {
        _host.Close();
        bool wrap = !Shell.Settings.TextWordWrap;
        Shell.Settings.TextWordWrap = wrap;
        _canvas.WordWrap = wrap;
        RequestJump(toMiddle: false);
        StoreSettings();
        Shell.Notify(wrap ? "Word wrap on." : "Word wrap off.");
    }

    private void TextSize()
    {
        _host.Close();

        // Left and right change the size under the panel, so the effect is visible while it is chosen
        // rather than only after the panel closes.
        var stepper = new Stepper(
            "Text size",
            Shell.Settings.TextScale,
            1,
            4,
            1,
            SizeName,
            value =>
            {
                Shell.Settings.TextScale = (int)value;
                _canvas.Scale = (int)value;
                RequestJump(toMiddle: false);
            });

        _host.Show(new StackPanel()
            .Add(new Label("Text size"))
            .Add(new Separator())
            .Add(stepper)
            .Add(new Button("Done", () =>
            {
                _host.Close();
                StoreSettings();
                Shell.Notify($"Text size {SizeName(stepper.Value)}.");
            })));
    }

    private static string SizeName(long scale) => scale switch
    {
        1 => "smallest",
        2 => "small",
        3 => "large",
        _ => "largest",
    };

    private void StoreSettings()
    {
        if (!Shell.Settings.Save())
            Shell.Status("The setting applies now but could not be written.");
    }

    // The keyboard takes a ceiling up front, so a long line gets room to grow without going past what
    // the keyboard accepts.
    private static int EntryLength(string current) => Math.Clamp(current.Length + 64, 256, MaxEntryLength);
}

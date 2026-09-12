// Prospero Explorer
// Copyright (C) 2026 SvenGDK

using ProsperoExplorer.Shell;
using SharpProspero.Application;
using SharpProspero.Graphics;
using SharpProspero.Input;
using SharpProspero.Interop.Pad;
using SharpProspero.Platform;
using SharpProspero.Storage;
using SharpProspero.Text;
using SharpProspero.Ui;
using System;
using System.Collections.Generic;

namespace ProsperoExplorer.Images;

/// <summary>
/// The picture viewer: it shows one file fitted to the screen, moves between the pictures in the same
/// folder, and carries the tools that change what is on screen or write it out in another form.
/// </summary>
/// <remarks>
/// The picture is drawn into a buffer this page owns and handed to a <see cref="Image"/> element, so the
/// page never touches the frame surface and the shell keeps drawing everything in one place. The buffer
/// is redrawn only when something moved, which keeps a still picture free of per-frame work.
/// </remarks>
internal sealed class ImageScreen : ExplorerScreen
{
    // How fast the picture slides under a held direction, and how far a single frame can carry it.
    private const int PanPixelsPerSecond = 900;

    // How many pixels one pass of a per-pixel effect works through. A picture from a camera runs to tens
    // of megapixels, so the work is cut into bands and spread over frames rather than stalling one.
    private const int BandPixels = 2_000_000;

    // Reading a file whole and decoding it cannot be cut into pieces the way the tools below can, so the
    // frame that opens a picture is held for as long as both take, and the pixels are then held twice
    // over - once as decoded, once as the copy the tools work on. These are the ceilings that keep both
    // to something bearable; a file past either is refused with the limit named.
    private const long MaxPictureBytes = 64L * 1024 * 1024;
    private const long MaxPicturePixels = 32_000_000;

    private const int BrightnessStep = 16;
    private const float ContrastUp = 1.15f;
    private const float ContrastDown = 0.87f;
    private const int BlurRadius = 2;

    // Some animated files ask for no delay at all, which would run them as fast as the screen refreshes.
    private const int MinimumFrameMilliseconds = 20;

    // A two-finger pinch scales the picture between a quarter and eight times the size a fit would give it.
    private const float MinZoom = 0.25f;
    private const float MaxZoom = 8f;

    private static readonly string[] FitOptions = ["Fit to the view", "Fill the view", "Actual size"];
    private static readonly string[] FormatOptions = ["PNG", "JPEG", "BMP", "TGA"];

    // The order matches JpegSampling: Ycc422, Ycc420, Grayscale.
    private static readonly string[] JpegColourOptions = ["Sharper colour (4:2:2)", "Smaller file (4:2:0)", "Grey, no colour"];

    private readonly Label _nameLine = new() { Scale = 3 };
    private readonly Label _detailLine = new();
    private readonly Separator _rule = new();
    private readonly Image _display = new(default) { Blend = true };
    private readonly StackPanel _page;
    private readonly ModalHost _modal;
    private readonly List<string> _siblings = [];
    private readonly TouchGestureRecognizer _touch = new();

    private DecodedPicture? _source;
    private PixelBuffer? _working;
    private PixelBuffer? _displayBuffer;
    private ShareCapture? _share;
    private JpegSampling _jpegSampling = JpegSampling.Ycc420;
    private string _path;
    private string? _pendingOpen;
    private long _fileSize;
    private int _index = -1;
    private int _frameIndex;
    private double _frameElapsed;
    private bool _animating;
    private bool _edited;
    private bool _dirty;
    private bool _displayFailed;
    private bool _closed;
    private int _panX;
    private int _panY;
    private float _zoom = 1f;
    private bool _pinching;
    private float _pinchBaseline = 1f;
    private bool _wasTouching;
    private int _drawWidth;
    private int _drawHeight;
    private int _maxPanX;
    private int _maxPanY;

    /// <summary>Opens the picture at <paramref name="imagePath"/>.</summary>
    public ImageScreen(ExplorerShell shell, string imagePath) : base(shell)
    {
        ArgumentException.ThrowIfNullOrEmpty(imagePath);
        _path = imagePath;
        _pendingOpen = imagePath;
        _detailLine.TextColor = shell.Theme.TextMuted;
        _nameLine.Text = PathUtil.GetFileName(imagePath);
        _detailLine.Text = "Opening.";
        _page = new StackPanel()
            .Add(_nameLine)
            .Add(_detailLine)
            .Add(_rule)
            .Add(_display);
        _modal = new ModalHost(_page);
    }

    /// <inheritdoc />
    public override string Title => "Picture";

    /// <inheritdoc />
    public override string Hint => "Triangle for tools, D-pad or touch to pan, Circle goes back.";

    /// <inheritdoc />
    protected override UiElement BuildRoot() => _modal;

    /// <inheritdoc />
    public override void Tick(FrameContext context)
    {
        EnsureDisplay();

        // Opening is left to the frame after the one that asked for it, so the name of the file being
        // read is on screen before the decoder takes the frame it needs.
        if (_pendingOpen is string pending)
        {
            _pendingOpen = null;
            Open(pending);
        }

        Advance(context);

        if (!Shell.Dialogs.IsBusy)
        {
            HandleButtons(context);
            HandleTouch(context);
        }

        if (_dirty)
            Redraw();
    }

    /// <inheritdoc />
    protected override bool OnCancel()
    {
        if (!_modal.IsOpen)
            return false;
        _modal.Close();
        return true;
    }

    /// <inheritdoc />
    protected override void OnDispose()
    {
        _closed = true;

        // The element draws from the buffer, so it is emptied before the pixels go.
        _display.SetContent(default);
        _working?.Dispose();
        _working = null;
        _source?.Dispose();
        _source = null;
        _displayBuffer?.Dispose();
        _displayBuffer = null;

        // The screenshot service is left running across captures and shut down here so the last capture
        // has time to finish being written.
        _share?.Dispose();
        _share = null;
    }

    // The buffer the picture is drawn into fills whatever the layout left below the heading, which is
    // only known once the page has been laid out. It is built on the first frame that has bounds.
    private void EnsureDisplay()
    {
        if (_displayBuffer is not null || _displayFailed)
            return;

        int width = _display.Bounds.Width;
        int height = _page.Bounds.Bottom - _display.Bounds.Y;
        if (width <= 0 || height <= 0)
            return;

        try
        {
            _displayBuffer = new PixelBuffer(width, height);
            _display.SetContent(_displayBuffer.AsSurface());
            _dirty = true;
        }
        catch (Exception error)
        {
            _displayFailed = true;
            Shell.ReportFailure("The viewer could not be prepared", error, important: true);
        }
    }

    private void Open(string path)
    {
        string name = PathUtil.GetFileName(path);
        long size = SizeOf(path);
        DecodedPicture picture;
        try
        {
            // Refusing is put through the same path as a decoder failure so it reaches the user the same
            // way: a box when it is the picture they asked for, the strip when they are stepping past it.
            if (size > MaxPictureBytes)
                throw new NotSupportedException(
                    $"{name} is {TextFormat.ByteSize(size)}. A picture is read and decoded in one go, so this stops at {TextFormat.ByteSize(MaxPictureBytes)}.");

            picture = ImageConvert.Decode(path);
            long pixels = (long)picture.Width * picture.Height;
            if (pixels > MaxPicturePixels)
            {
                long megapixels = pixels / 1_000_000;
                picture.Dispose();
                throw new NotSupportedException(
                    $"{name} is {megapixels} megapixels. A picture is held twice over while it is open, so this stops at {MaxPicturePixels / 1_000_000} megapixels.");
            }
        }
        catch (Exception error)
        {
            // Losing the picture the user asked for is worth a box; failing to step onto the next one in
            // a folder is not, because what they were looking at is still there.
            Shell.ReportFailure($"{name} could not be opened", error, important: _source is null);
            UpdateHeader();
            return;
        }

        _source?.Dispose();
        _source = picture;
        _path = path;
        _fileSize = size;
        _edited = false;
        RestoreOriginal();
        EnsureSiblings();
        UpdateHeader();
    }

    // Rebuilds the working copy from the first frame. Everything that changed the picture goes with it,
    // because the copy is built again from what was decoded rather than unwound step by step.
    private void RestoreOriginal()
    {
        if (_source is null)
            return;

        _working?.Dispose();
        _working = null;
        _frameIndex = 0;
        _frameElapsed = 0;
        _animating = _source.IsAnimated;
        _panX = 0;
        _panY = 0;
        _zoom = 1f;
        _pinching = false;
        _dirty = true;

        try
        {
            _working = new PixelBuffer(_source.Width, _source.Height);
            CopyFrame();
        }
        catch (Exception error)
        {
            Shell.ReportFailure("The picture could not be prepared", error, important: true);
        }
    }

    private void CopyFrame()
    {
        if (_source is null || _working is null)
            return;
        _working.AsSurface().Blit(_source.Frame(_frameIndex), 0, 0);
    }

    private void Advance(FrameContext context)
    {
        // A frame that turned over while the menu or a dialog was up would leave "what is on screen"
        // pointing at a frame the user never chose, so the picture is held on its frame until the
        // panel closes. Both the frame-pinned convert and the on-screen copy then write what is shown.
        if (_modal.IsOpen || Shell.Dialogs.IsBusy)
            return;
        if (!_animating || _source is null || _working is null || !_source.IsAnimated)
            return;

        _frameElapsed += context.DeltaSeconds * 1000.0;
        int delay = Math.Max(MinimumFrameMilliseconds, _source.Delay(_frameIndex));
        if (_frameElapsed < delay)
            return;

        _frameElapsed -= delay;
        _frameIndex = (_frameIndex + 1) % _source.FrameCount;
        CopyFrame();
        UpdateHeader();
        _dirty = true;
    }

    private void HandleButtons(FrameContext context)
    {
        if (context.Pressed(ScePadButton.Triangle))
        {
            if (_modal.IsOpen)
                _modal.Close();
            else
                OpenMenu();
            return;
        }

        if (_modal.IsOpen || _pendingOpen is not null)
            return;

        if (_working is null || _displayBuffer is null)
        {
            // With nothing on screen there is nothing to pan, so left and right still move through the
            // folder rather than doing nothing at all.
            if (context.Pressed(ScePadButton.Left))
                Step(-1);
            else if (context.Pressed(ScePadButton.Right))
                Step(+1);
            return;
        }

        ComputeExtents();

        // At actual size the picture is browsed as well as panned: once it cannot slide any further in a
        // direction, the next press in that direction moves to the neighbouring picture.
        if (Shell.Settings.ImageFit == ImageFit.Actual)
        {
            if (context.Pressed(ScePadButton.Left) && _panX <= -_maxPanX)
            {
                Step(-1);
                return;
            }

            if (context.Pressed(ScePadButton.Right) && _panX >= _maxPanX)
            {
                Step(+1);
                return;
            }
        }

        int step = Math.Max(1, (int)(PanPixelsPerSecond * context.DeltaSeconds));
        int panX = _panX;
        int panY = _panY;
        if (context.Held(ScePadButton.Left))
            panX -= step;
        if (context.Held(ScePadButton.Right))
            panX += step;
        if (context.Held(ScePadButton.Up))
            panY -= step;
        if (context.Held(ScePadButton.Down))
            panY += step;

        panX = Math.Clamp(panX, -_maxPanX, _maxPanX);
        panY = Math.Clamp(panY, -_maxPanY, _maxPanY);
        if (panX == _panX && panY == _panY)
            return;

        _panX = panX;
        _panY = panY;
        _dirty = true;
    }

    // Moves and scales the picture from the touch pad: one finger drags it, two fingers pinch to zoom.
    // The recognizer is fed only while a finger is down (and on the frame the last lifts), so a still
    // picture with no touch does no per-frame work.
    private void HandleTouch(FrameContext context)
    {
        if (_modal.IsOpen || _pendingOpen is not null || _working is null || _displayBuffer is null)
        {
            _wasTouching = context.Input.TouchCount > 0;
            _pinching = false;
            return;
        }

        bool touchNow = context.Input.TouchCount > 0;
        if (!touchNow && !_wasTouching)
            return;
        _wasTouching = touchNow;

        // A pinch's scale is measured from where the two fingers began, so the baseline is reset once they
        // are no longer both down.
        if (context.Input.TouchCount < 2)
            _pinching = false;

        IReadOnlyList<TouchGesture> gestures = _touch.Update(context.Input);
        if (gestures.Count == 0)
            return;

        ComputeExtents();
        foreach (TouchGesture gesture in gestures)
        {
            switch (gesture.Kind)
            {
                case TouchGestureKind.Pinch:
                    if (!_pinching)
                    {
                        _pinching = true;
                        _pinchBaseline = _zoom;
                    }

                    _zoom = Math.Clamp(_pinchBaseline * gesture.Scale, MinZoom, MaxZoom);
                    ComputeExtents();
                    _dirty = true;
                    break;

                case TouchGestureKind.Drag:
                    // The picture follows the finger: dragging right slides it right, which is a smaller
                    // pan offset, so the movement is subtracted.
                    _panX = Math.Clamp(_panX - (int)MathF.Round(gesture.Delta.X), -_maxPanX, _maxPanX);
                    _panY = Math.Clamp(_panY - (int)MathF.Round(gesture.Delta.Y), -_maxPanY, _maxPanY);
                    _dirty = true;
                    break;
            }
        }
    }

    // Works out how large the picture is drawn under the current fit, and how far it can be panned.
    private void ComputeExtents()
    {
        if (_working is null || _displayBuffer is null)
            return;

        int viewWidth = _displayBuffer.Width;
        int viewHeight = _displayBuffer.Height;
        float scale = Shell.Settings.ImageFit switch
        {
            ImageFit.Cover => MathF.Max((float)viewWidth / _working.Width, (float)viewHeight / _working.Height),
            ImageFit.Actual => 1f,
            _ => MathF.Min((float)viewWidth / _working.Width, (float)viewHeight / _working.Height),
        };

        // A pinch scales what the fit produced, so zooming works from whichever fit is chosen.
        scale *= _zoom;

        _drawWidth = Math.Max(1, (int)MathF.Round(_working.Width * scale));
        _drawHeight = Math.Max(1, (int)MathF.Round(_working.Height * scale));
        _maxPanX = Math.Max(0, (_drawWidth - viewWidth + 1) / 2);
        _maxPanY = Math.Max(0, (_drawHeight - viewHeight + 1) / 2);
        _panX = Math.Clamp(_panX, -_maxPanX, _maxPanX);
        _panY = Math.Clamp(_panY, -_maxPanY, _maxPanY);
    }

    private void Redraw()
    {
        // The request is held rather than dropped while the buffer is still being sized, so the first
        // picture is not left undrawn by the frame that opened it running before the first layout.
        if (_displayBuffer is null || _working is null)
            return;

        _dirty = false;
        ComputeExtents();
        Surface view = _displayBuffer.AsSurface();

        // The buffer is left transparent where the picture is not, and the element blends it over the
        // page, so a picture with an alpha channel shows the page through it instead of black.
        view.Clear(Color.Transparent);

        Surface picture = _working.AsSurface();
        int x = ((_displayBuffer.Width - _drawWidth) / 2) - _panX;
        int y = ((_displayBuffer.Height - _drawHeight) / 2) - _panY;

        if (_drawWidth == picture.Width && _drawHeight == picture.Height)
            view.Blit(picture, x, y);
        else if (Shell.Settings.ImageSmoothScaling)
            view.BlitScaledSmooth(picture, x, y, _drawWidth, _drawHeight);
        else
            view.BlitScaled(picture, x, y, _drawWidth, _drawHeight);

        // A picture that does not reach the edges gets a hairline, so a dark one is not mistaken for an
        // empty page.
        if (_drawWidth < _displayBuffer.Width && _drawHeight < _displayBuffer.Height)
            view.DrawRect(x - 1, y - 1, _drawWidth + 2, _drawHeight + 2, Shell.Theme.Border);
    }

    private void UpdateHeader()
    {
        string name = PathUtil.GetFileName(_path);
        _nameLine.Text = _siblings.Count > 1 ? $"{name}    {_index + 1} of {_siblings.Count}" : name;

        if (_source is null || _working is null)
        {
            _detailLine.Text = "Nothing is open.";
            return;
        }

        string detail = $"{_working.Width} x {_working.Height}, {TextFormat.ByteSize(_fileSize)}, "
            + $"{ImageConvert.Name(_source.Form)}, {FitOptions[(int)Shell.Settings.ImageFit].ToLowerInvariant()}";
        if (MathF.Abs(_zoom - 1f) > 0.01f)
            detail += ", zoomed";
        if (_source.IsAnimated)
            detail += $", frame {_frameIndex + 1} of {_source.FrameCount}";
        if (_edited)
            detail += ", changed";
        _detailLine.Text = detail;
    }

    private static long SizeOf(string path)
    {
        try
        {
            return FileSystem.GetFileSize(path);
        }
        catch (Exception)
        {
            // The size is a detail in the heading; a file that will not answer still shows.
            return 0;
        }
    }

    private void EnsureSiblings()
    {
        if (_index >= 0 && _index < _siblings.Count && string.Equals(_siblings[_index], _path, StringComparison.Ordinal))
            return;
        BuildSiblings();
    }

    private void BuildSiblings()
    {
        _siblings.Clear();
        string folder = PathUtil.GetDirectoryName(_path);
        try
        {
            foreach (DirectoryEntry entry in FileSystem.EnumerateDirectory(folder))
            {
                if (!entry.IsFile)
                    continue;
                string full = PathUtil.Combine(folder, entry.Name);
                if (FileKinds.Classify(full, isDirectory: false) == FileKind.Image)
                    _siblings.Add(full);
            }

            _siblings.Sort(TextFormat.NaturalComparer);
        }
        catch (Exception)
        {
            // A folder that will not list leaves the picture on its own, which is still usable.
            _siblings.Clear();
        }

        _index = _siblings.IndexOf(_path);
        if (_index < 0)
        {
            _siblings.Add(_path);
            _index = _siblings.Count - 1;
        }
    }

    private void Step(int direction)
    {
        // The position is not resynchronised to the picture on screen here. When a file fails to open the
        // position stays on it, so the next press carries on past it instead of trying it again.
        if (_siblings.Count == 0)
            BuildSiblings();
        if (_siblings.Count < 2)
        {
            Shell.Status("This is the only picture in the folder.");
            return;
        }

        _index = (_index + direction + _siblings.Count) % _siblings.Count;
        string next = _siblings[_index];
        _pendingOpen = next;
        _nameLine.Text = PathUtil.GetFileName(next);
        _detailLine.Text = "Opening.";
    }

    private void OpenMenu()
    {
        var menu = new ScrollMenu { ViewHeight = MenuHeight() };

        menu.Add(new OptionSelector("Fit", FitOptions, (int)Shell.Settings.ImageFit, index => ChangeFit((ImageFit)index)));
        menu.Add(new OptionSelector("Copies are written as", FormatOptions, (int)Shell.Settings.ImageExportFormat,
            index => SetExportFormat((ImageFormat)index)));
        menu.Add(new OptionSelector("JPEG colour", JpegColourOptions, (int)_jpegSampling, index => _jpegSampling = (JpegSampling)index));
        menu.Add(new Separator());

        menu.Add(Item("Rotate left", () => Rotate(clockwise: false)));
        menu.Add(Item("Rotate right", () => Rotate(clockwise: true)));
        menu.Add(Item("Flip horizontal", () => Banded("Flipping the picture", "Flipped left to right.", surface => surface.FlipHorizontal())));
        menu.Add(Item("Flip vertical", FlipVertical));
        menu.Add(Item("Grayscale", () => Banded("Draining the colour", "Grey.", surface => surface.ToGrayscale())));
        menu.Add(Item("Invert", () => Banded("Inverting the picture", "Inverted.", surface => surface.Invert())));
        menu.Add(Item("Brightness up", () => Banded("Brightening the picture", "Brighter.", surface => surface.AdjustBrightness(BrightnessStep))));
        menu.Add(Item("Brightness down", () => Banded("Darkening the picture", "Darker.", surface => surface.AdjustBrightness(-BrightnessStep))));
        menu.Add(Item("Contrast up", () => Banded("Raising the contrast", "More contrast.", surface => surface.AdjustContrast(ContrastUp))));
        menu.Add(Item("Contrast down", () => Banded("Lowering the contrast", "Less contrast.", surface => surface.AdjustContrast(ContrastDown))));
        menu.Add(Item("Blur", Blur));
        menu.Add(Item("Reset", ResetPicture));
        menu.Add(new Separator());

        menu.Add(Item("Save a copy of what is on screen", SaveCopy));

        // A GIF stores each dimension in sixteen bits and is held whole in memory, so a picture larger
        // than the encoder takes is offered every other form but not GIF, rather than a GIF option that
        // could only ever fail once chosen.
        if (_working is not null && GifEncoder.CanEncode(_working.Width, _working.Height))
            menu.Add(Item("Save a copy as GIF", SaveCopyGif));
        menu.Add(Item("Convert the file to PNG", () => ConvertFile(ImageFormat.Png)));
        menu.Add(Item("Convert the file to JPEG", () => ConvertFile(ImageFormat.Jpeg)));
        menu.Add(Item("Convert the file to BMP", () => ConvertFile(ImageFormat.Bmp)));
        menu.Add(Item("Convert the file to TGA", () => ConvertFile(ImageFormat.Tga)));
        if (_source is not null && GifEncoder.CanEncode(_source.Width, _source.Height))
            menu.Add(Item("Convert the file to GIF", ConvertFileToGif));
        menu.Add(Item("Take a screenshot", TakeScreenshot));
        menu.Add(new Separator());

        menu.Add(Item("Previous picture", () => Step(-1)));
        menu.Add(Item("Next picture", () => Step(+1)));
        menu.Add(Item("Properties", ShowProperties));
        menu.Add(Item("Close this menu", () => { }));

        _modal.Show(menu);
    }

    // Every entry but the selectors closes the menu before it runs, so the result is seen rather
    // than hidden behind the panel that asked for it.
    private Button Item(string text, Action run) => new(text, () =>
    {
        _modal.Close();
        run();
    });

    private int MenuHeight() => Math.Clamp(_page.Bounds.Height - (6 * Screen.Theme.Padding), 240, 620);

    private void ChangeFit(ImageFit fit)
    {
        Shell.Settings.ImageFit = fit;
        _panX = 0;
        _panY = 0;
        _zoom = 1f;
        _pinching = false;
        _dirty = true;
        UpdateHeader();
        Shell.Status($"Fit: {FitOptions[(int)fit].ToLowerInvariant()}.");
    }

    private void SetExportFormat(ImageFormat format)
    {
        Shell.Settings.ImageExportFormat = format;
        string message = $"Copies are written as {ImageConvert.Name(format)}.";
        Shell.Status(Shell.Settings.Save() ? message : $"{message} The choice could not be remembered.");
    }

    private void ResetPicture()
    {
        if (_source is null)
        {
            Shell.Status("There is nothing open to reset.");
            return;
        }

        _edited = false;
        RestoreOriginal();
        UpdateHeader();
        Shell.Status("The picture is back as it was read.");
    }

    private void Rotate(bool clockwise)
    {
        if (_working is null)
        {
            Shell.Status("There is nothing open to turn.");
            return;
        }

        PixelBuffer source = _working;
        _animating = false;

        // A quarter turn lands in a buffer with its sides swapped, and the turn is taken about that
        // buffer's centre so the whole picture falls inside it.
        float angle = clockwise ? MathF.PI / 2f : -MathF.PI / 2f;
        PixelBuffer? turned = null;
        RunWork(
            "Turning the picture",
            _ =>
            {
                turned = new PixelBuffer(source.Height, source.Width);
                turned.AsSurface().BlitRotated(source.AsSurface(), turned.Width / 2, turned.Height / 2, angle);
                return true;
            },
            error =>
            {
                if (error is not null || turned is null)
                {
                    turned?.Dispose();
                    Shell.ReportFailure("The picture could not be turned", error ?? new InvalidOperationException("The turn produced nothing."));
                    return;
                }

                _working = turned;
                source.Dispose();
                _edited = true;
                _panX = 0;
                _panY = 0;
                _dirty = true;
                UpdateHeader();
                Shell.Status(clockwise ? "Turned right." : "Turned left.");
            });
    }

    private void Blur()
    {
        if (_working is null)
        {
            Shell.Status("There is nothing open to blur.");
            return;
        }

        // The blur reads across rows and columns at once, so it cannot be cut into bands the way the
        // per-pixel tools are; it runs in one pass with the bar up.
        PixelBuffer target = _working;
        _animating = false;
        RunWork(
            "Blurring the picture",
            dialog =>
            {
                dialog.SetProgress(50);
                target.AsSurface().BoxBlur(BlurRadius);
                return true;
            },
            error => Finish(error, "The picture could not be blurred", "Blurred."));
    }

    // A flip top to bottom is the one tool here that is not row-local: every row trades places with the
    // one opposite it. Put through Banded it would mirror each band of rows inside itself and leave the
    // picture in slices, so bands are swapped in pairs instead, top against bottom, working inwards. A
    // scratch band holds the top rows while the bottom ones are written over them.
    private void FlipVertical()
    {
        if (_working is null)
        {
            Shell.Status("There is nothing open to change.");
            return;
        }

        PixelBuffer target = _working;
        _animating = false;

        int half = target.Height / 2;
        int rows = Math.Clamp(BandPixels / Math.Max(1, target.Width), 1, Math.Max(1, half));
        PixelBuffer? scratch = null;
        int row = 0;

        RunWork(
            "Flipping the picture",
            dialog =>
            {
                int height = Math.Min(rows, half - row);
                if (height > 0)
                {
                    scratch ??= new PixelBuffer(target.Width, rows);
                    Surface whole = target.AsSurface();
                    Surface top = whole.Region(0, row, target.Width, height);
                    Surface bottom = whole.Region(0, target.Height - row - height, target.Width, height);
                    Surface held = scratch.AsSurface().Region(0, 0, target.Width, height);

                    // Each band takes the other's rows and is then mirrored, which is what every row in
                    // the pair trading places with its opposite comes to once both bands have moved.
                    held.Blit(top, 0, 0);
                    top.Blit(bottom, 0, 0);
                    top.FlipVertical();
                    bottom.Blit(held, 0, 0);
                    bottom.FlipVertical();
                    row += height;
                }

                dialog.SetProgress(half <= 0 ? 100 : row * 100 / half);
                return row >= half;
            },
            error =>
            {
                scratch?.Dispose();
                Finish(error, "Flipping the picture did not finish", "Flipped top to bottom.");
            });
    }

    // Runs a per-pixel tool over the working copy a band of rows at a time, so a very large picture is
    // worked through across frames instead of holding one for as long as it takes.
    private void Banded(string caption, string done, Action<Surface> apply)
    {
        if (_working is null)
        {
            Shell.Status("There is nothing open to change.");
            return;
        }

        PixelBuffer target = _working;
        _animating = false;
        int rows = Math.Max(1, BandPixels / Math.Max(1, target.Width));
        int row = 0;

        RunWork(
            caption,
            dialog =>
            {
                int height = Math.Min(rows, target.Height - row);
                apply(target.AsSurface().Region(0, row, target.Width, height));
                row += height;
                dialog.SetProgress(row * 100 / Math.Max(1, target.Height));
                return row >= target.Height;
            },
            error => Finish(error, $"{caption} did not finish", done));
    }

    private void Finish(Exception? error, string failed, string done)
    {
        if (error is not null)
        {
            Shell.ReportFailure(failed, error);
            return;
        }

        _edited = true;
        _dirty = true;
        UpdateHeader();
        Shell.Status(done);
    }

    private void SaveCopy()
    {
        if (_working is null)
        {
            Shell.Status("There is nothing open to save.");
            return;
        }

        ImageFormat format = Shell.Settings.ImageExportFormat;
        string target;
        try
        {
            target = ImageConvert.CopyPath(_path, format);
        }
        catch (Exception error)
        {
            Shell.ReportFailure("A name for the copy could not be found", error);
            return;
        }

        PixelBuffer source = _working;
        AppSettings settings = Shell.Settings;
        JpegSampling sampling = _jpegSampling;
        RunWork(
            $"Writing {PathUtil.GetFileName(target)}",
            _ =>
            {
                ImageConvert.Save(source.AsSurface(), target, format, settings, sampling);
                return true;
            },
            error =>
            {
                if (error is not null)
                {
                    Shell.ReportFailure($"{PathUtil.GetFileName(target)} could not be written", error, important: true);
                    return;
                }

                _siblings.Clear();
                _index = -1;
                EnsureSiblings();
                UpdateHeader();
                Shell.Notify($"Saved as {PathUtil.GetFileName(target)}.");
            });
    }

    private void SaveCopyGif()
    {
        if (_working is null)
        {
            Shell.Status("There is nothing open to save.");
            return;
        }

        string target;
        try
        {
            target = ImageConvert.CopyPath(_path, ImageConvert.GifExtension);
        }
        catch (Exception error)
        {
            Shell.ReportFailure("A name for the copy could not be found", error);
            return;
        }

        PixelBuffer source = _working;
        RunWork(
            $"Writing {PathUtil.GetFileName(target)}",
            _ =>
            {
                ImageConvert.SaveGif(source.AsSurface(), target);
                return true;
            },
            error =>
            {
                if (error is not null)
                {
                    Shell.ReportFailure($"{PathUtil.GetFileName(target)} could not be written", error, important: true);
                    return;
                }

                _siblings.Clear();
                _index = -1;
                EnsureSiblings();
                UpdateHeader();
                Shell.Notify($"Saved as {PathUtil.GetFileName(target)}.");
            });
    }

    private void ConvertFile(ImageFormat format)
    {
        if (_source is null)
        {
            Shell.Status("There is nothing open to convert.");
            return;
        }

        string target = ImageConvert.TargetPath(_path, format);
        if (string.Equals(target, _path, StringComparison.Ordinal))
        {
            Shell.Status($"{PathUtil.GetFileName(_path)} is already {ImageConvert.Name(format)}.");
            return;
        }

        bool exists;
        try
        {
            exists = FileSystem.Exists(target);
        }
        catch (Exception)
        {
            // Treated as absent; the write reports for itself if the file is in the way.
            exists = false;
        }

        if (exists && Shell.Settings.ConfirmDestructive)
        {
            Shell.Dialogs.Confirm(
                $"{PathUtil.GetFileName(target)} already exists.\n\nReplace it?",
                replace =>
                {
                    if (replace)
                        ConvertNow(format, target);
                    else
                        Shell.Status("Nothing was written.");
                });
            return;
        }

        ConvertNow(format, target);
    }

    private void ConvertNow(ImageFormat format, string target)
    {
        if (_source is null)
        {
            Shell.Status("There is nothing open to convert.");
            return;
        }

        // The picture is already decoded in memory, so the frame is written straight out rather than
        // reading and decoding the file a second time. The tools change only the working copy, so this
        // writes the pristine pixels; for an animation the frame on screen is the one written, which is
        // what the properties note promises. The index is captured now in case the animation advances
        // before the write runs.
        DecodedPicture source = _source;
        int frame = Math.Clamp(_frameIndex, 0, source.FrameCount - 1);
        AppSettings settings = Shell.Settings;
        JpegSampling sampling = _jpegSampling;
        RunWork(
            $"Writing {PathUtil.GetFileName(target)}",
            _ =>
            {
                ImageConvert.Save(source.Frame(frame), target, format, settings, sampling);
                return true;
            },
            error =>
            {
                if (error is not null)
                {
                    Shell.ReportFailure($"{PathUtil.GetFileName(target)} could not be written", error, important: true);
                    return;
                }

                _siblings.Clear();
                _index = -1;
                EnsureSiblings();
                UpdateHeader();
                Shell.Notify($"Converted to {PathUtil.GetFileName(target)}.");
            });
    }

    private void ConvertFileToGif()
    {
        if (_source is null)
        {
            Shell.Status("There is nothing open to convert.");
            return;
        }

        string target = PathUtil.ChangeExtension(_path, ImageConvert.GifExtension);
        if (string.Equals(target, _path, StringComparison.Ordinal))
        {
            Shell.Status($"{PathUtil.GetFileName(_path)} is already {ImageConvert.Name(ImageForm.Gif)}.");
            return;
        }

        bool exists;
        try
        {
            exists = FileSystem.Exists(target);
        }
        catch (Exception)
        {
            // Treated as absent; the write reports for itself if the file is in the way.
            exists = false;
        }

        if (exists && Shell.Settings.ConfirmDestructive)
        {
            Shell.Dialogs.Confirm(
                $"{PathUtil.GetFileName(target)} already exists.\n\nReplace it?",
                replace =>
                {
                    if (replace)
                        ConvertToGifNow(target);
                    else
                        Shell.Status("Nothing was written.");
                });
            return;
        }

        ConvertToGifNow(target);
    }

    private void ConvertToGifNow(string target)
    {
        if (_source is null)
        {
            Shell.Status("There is nothing open to convert.");
            return;
        }

        // The frames are already decoded in memory, so an animation is written straight out with every
        // frame kept rather than reading and decoding the file again.
        DecodedPicture source = _source;
        RunWork(
            $"Writing {PathUtil.GetFileName(target)}",
            _ =>
            {
                ImageConvert.WriteGif(source, target);
                return true;
            },
            error =>
            {
                if (error is not null)
                {
                    Shell.ReportFailure($"{PathUtil.GetFileName(target)} could not be written", error, important: true);
                    return;
                }

                _siblings.Clear();
                _index = -1;
                EnsureSiblings();
                UpdateHeader();
                Shell.Notify(source.FrameCount > 1
                    ? $"Converted {source.FrameCount} frames to {PathUtil.GetFileName(target)}."
                    : $"Converted to {PathUtil.GetFileName(target)}.");
            });
    }

    private void TakeScreenshot()
    {
        try
        {
            // The service is kept in a field and started once, so a later screenshot reuses it.
            ShareCapture share = _share ??= ShareCapture.Start();
            share.CaptureScreenshot();
            Shell.Notify("A screenshot was saved to the capture gallery.");
        }
        catch (Exception error)
        {
            _share?.Dispose();
            _share = null;
            Shell.ReportFailure("A screenshot could not be taken", error);
        }
    }

    private void ShowProperties()
    {
        if (_source is null || _working is null)
        {
            Shell.Status("There is nothing open to describe.");
            return;
        }

        string message = $"{PathUtil.GetFileName(_path)}\n{_path}\n\n"
            + $"Form: {ImageConvert.Name(_source.Form)}\n"
            + $"Read at: {_source.Width} x {_source.Height}\n"
            + $"On screen: {_working.Width} x {_working.Height}\n"
            + $"File size: {TextFormat.ByteSize(_fileSize)}\n";
        if (_source.IsAnimated)
            message += $"Frames: {_source.FrameCount}, {TextFormat.Duration(_source.TotalSeconds)} for one pass\n";
        message += $"\n{ImageConvert.DescribeWriting(_source.Form, _source.FrameCount)}";

        Shell.Dialogs.Alert(message);
    }

    /// <summary>
    /// Puts the progress overlay up and drives <paramref name="step"/> from the frame loop until it
    /// reports it is finished, then hands <paramref name="done"/> whatever went wrong, or null.
    /// </summary>
    /// <remarks>
    /// The first pass does nothing on purpose. The overlay only appears once it has been pumped, so
    /// starting the work a frame later means the bar is on screen while it runs instead of afterwards.
    /// </remarks>
    private void RunWork(string caption, Func<MessageDialog, bool> step, Action<Exception?> done)
    {
        Exception? failure = null;
        bool started = false;
        Shell.Dialogs.RunWithProgress(
            caption,
            dialog =>
            {
                if (_closed)
                    return true;
                if (!started)
                {
                    started = true;
                    return false;
                }

                try
                {
                    return step(dialog);
                }
                catch (Exception error)
                {
                    failure = error;
                    return true;
                }
            },
            () =>
            {
                if (!_closed)
                    done(failure);
            });
    }
}

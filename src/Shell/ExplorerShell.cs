// Prospero Explorer
// Copyright (C) 2026 SvenGDK

using SharpProspero.Application;
using SharpProspero.Graphics;
using SharpProspero.Interop;
using SharpProspero.Platform;
using SharpProspero.Ui;
using System;
using System.Collections.Generic;

namespace ProsperoExplorer.Shell;

/// <summary>
/// What every page is given: where it is in the stack, how to say something, and how to reach the
/// system overlays. The shell owns the chrome as well - the header, the message strip and the
/// background - so a page describes only itself.
/// </summary>
internal sealed class ExplorerShell : IDisposable
{
    private readonly List<ExplorerScreen> _stack = [];
    private readonly Toast _toast = new();
    private bool _disposed;
    private bool _dialogWasBusy;

    public ExplorerShell(AppSettings settings, ITextFont font)
    {
        Settings = settings;
        Theme = BuildTheme(settings, font);
    }

    /// <summary>Everything the application remembers between runs.</summary>
    public AppSettings Settings { get; }

    /// <summary>The system overlays, advanced once a frame by the shell.</summary>
    public SystemDialogs Dialogs { get; } = new();

    /// <summary>The colours and spacing every page draws with.</summary>
    public UiTheme Theme { get; }

    /// <summary>The page at the front, or null before the first one is pushed.</summary>
    public ExplorerScreen? Top => _stack.Count > 0 ? _stack[^1] : null;

    /// <summary>How many pages are open.</summary>
    public int Depth => _stack.Count;

    /// <summary>Set when the last page closes, which ends the application.</summary>
    public bool ExitRequested { get; private set; }

    /// <summary>Opens <paramref name="screen"/> above the current one.</summary>
    public void Push(ExplorerScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);
        Top?.OnHidden();
        _stack.Add(screen);
        screen.OnShown();
    }

    /// <summary>Closes the page at the front. Closing the last one ends the application.</summary>
    public void Pop()
    {
        if (_stack.Count == 0)
            return;
        if (_stack.Count == 1)
        {
            ExitRequested = true;
            return;
        }

        ExplorerScreen leaving = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        leaving.OnHidden();
        leaving.Dispose();
        Top?.OnShown();
    }

    /// <summary>Closes every page above the first.</summary>
    public void PopToRoot()
    {
        while (_stack.Count > 1)
            Pop();
    }

    /// <summary>Ends the application after this frame.</summary>
    public void RequestExit() => ExitRequested = true;

    /// <summary>
    /// Puts <paramref name="message"/> on the strip at the bottom, and raises a system notification as
    /// well when the settings ask for one. Use this for what went right.
    /// </summary>
    public void Notify(string message)
    {
        _toast.Show(message, Settings.ToastSeconds);
        if (!Settings.SystemNotifications)
            return;
        try
        {
            Notification.Show(message);
        }
        catch (ProsperoException)
        {
            // The notification service is not essential; the strip already carried the message.
        }
    }

    /// <summary>
    /// Puts <paramref name="message"/> on the strip without raising a notification. Use this for
    /// something the user is watching for and does not need told about twice.
    /// </summary>
    public void Status(string message) => _toast.Show(message, Settings.ToastSeconds);

    /// <summary>
    /// Reports that <paramref name="what"/> failed, naming the reason. A failure always reaches the
    /// user: it goes to the strip, and to a box they have to dismiss when it lost them work.
    /// </summary>
    public void ReportFailure(string what, Exception error, bool important = false)
    {
        string reason = Describe(error);
        if (important)
            Dialogs.Alert($"{what}\n\n{reason}");
        else
            _toast.Show($"{what}: {reason}", Math.Max(Settings.ToastSeconds, 4f));
    }

    /// <summary>
    /// The reason behind <paramref name="error"/>, in a form worth showing. A platform failure already
    /// carries its own code in the message, so it is used as it stands.
    /// </summary>
    public static string Describe(Exception error) => error.Message;

    /// <summary>The platform's own code behind <paramref name="error"/>, or zero when it has none.</summary>
    public static int CodeOf(Exception error) => error is ProsperoException platform ? platform.Code : 0;

    /// <summary>
    /// Advances the overlays, then the pages, then the message strip.
    /// </summary>
    /// <remarks>
    /// A page that throws is closed rather than allowed to end the application. Losing one page and
    /// being told why leaves the user somewhere they can carry on from; letting it out of the frame
    /// closes everything with nothing said, and a file service or a transfer running behind it goes
    /// with it.
    /// </remarks>
    public void Tick(FrameContext context)
    {
        Guard(Dialogs.Tick, "An overlay failed");

        // A page below the front keeps running only when it says it has to - a server listening, a
        // transfer in flight. Everything else stops when it leaves the front.
        for (int i = 0; i < _stack.Count - 1; i++)
        {
            if (_stack[i].TicksInBackground)
                TickPage(_stack[i], context);
        }

        ExplorerScreen? top = Top;
        if (top is not null)
            TickPage(top, context);
        _toast.Update((float)context.DeltaSeconds);
    }

    private void TickPage(ExplorerScreen page, FrameContext context)
    {
        try
        {
            page.Tick(context);
        }
        catch (Exception error)
        {
            ClosePageAfterFailure(page, error);
        }
    }

    private void Guard(Action work, string what)
    {
        try
        {
            work();
        }
        catch (Exception error)
        {
            _toast.Show($"{what}: {Describe(error)}", Math.Max(Settings.ToastSeconds, 4f));
        }
    }

    // Takes a page out of the stack after it threw. The first page has nothing under it, so it stays
    // and the failure is only reported; closing it would leave nothing to draw.
    private void ClosePageAfterFailure(ExplorerScreen page, Exception error)
    {
        string title = page.Title;
        int at = _stack.IndexOf(page);
        if (at > 0)
        {
            _stack.RemoveAt(at);
            page.Dispose();
            if (at == _stack.Count)
                Top?.OnShown();
        }
        _toast.Show($"{title} stopped: {Describe(error)}", Math.Max(Settings.ToastSeconds, 5f));
    }

    /// <summary>
    /// Draws the frame: the background, the header, the page at the front, and the message strip. The
    /// page is given input only when no overlay is up, so a keyboard on screen cannot also move the
    /// list underneath it.
    /// </summary>
    public void Draw(FrameContext context)
    {
        Surface surface = context.Surface;
        ExplorerScreen? top = Top;
        surface.Clear(Theme.Background);
        if (top is null)
            return;

        try
        {
            DrawHeader(surface, top);

            int headerHeight = HeaderHeight;
            var area = new UiRect(Margin, headerHeight, surface.Width - (2 * Margin), surface.Height - headerHeight - Margin);
            // Building the tree happens on first use and can fail on what the page found when it
            // opened, so it is inside the guard along with laying it out and drawing it.
            UiScreen screen = top.Screen;
            screen.Layout(area);
            // While an overlay is up the page gets no input; and for one frame after it closes the page
            // still gets none, so the button press that dismissed the overlay does not also act on the
            // page underneath it.
            bool busyNow = Dialogs.IsBusy;
            UiInput input = busyNow || _dialogWasBusy ? UiInput.None : UiInput.From(context.Input, context.PreviousInput);
            _dialogWasBusy = busyNow;
            screen.Update(input);
            screen.Draw(surface);
        }
        catch (Exception error)
        {
            ClosePageAfterFailure(top, error);
        }

        _toast.Draw(surface, Theme);
    }

    private void DrawHeader(Surface surface, ExplorerScreen top)
    {
        int bar = HeaderHeight - 12;
        surface.FillRect(0, 0, surface.Width, bar, Theme.Panel);
        surface.HLine(0, bar, surface.Width, Theme.Border);

        int line = Theme.LineHeight;
        int titleY = 16;

        // The hint sits at the right; the title takes the room left of it and is shortened rather than
        // allowed to run underneath it.
        string hint = top.Hint;
        int hintWidth = Theme.MeasureText(hint);
        int hintX = surface.Width - Margin - hintWidth;
        Theme.DrawText(surface, hint, hintX, titleY, Theme.TextMuted);
        Theme.DrawClipped(surface, top.Title, Margin, titleY, Theme.Text, hintX - Theme.Spacing - Margin);

        // A breadcrumb of the open pages, so both how deep the stack is and where it leads are clear.
        if (_stack.Count > 1)
            Theme.DrawClipped(surface, Breadcrumb(), Margin, titleY + line + 6, Theme.Accent, surface.Width - (2 * Margin));
    }

    // The names of the open pages joined into a trail, for the header breadcrumb.
    private string Breadcrumb()
    {
        var trail = new System.Text.StringBuilder();
        for (int i = 0; i < _stack.Count; i++)
        {
            if (i > 0)
                trail.Append("  >  ");
            trail.Append(_stack[i].Title);
        }
        return trail.ToString();
    }

    private const int Margin = 48;

    // The header holds a title/hint line and a breadcrumb line, so it grows with the font's line height.
    private int HeaderHeight => (Theme.LineHeight * 2) + 48;

    private static UiTheme BuildTheme(AppSettings settings, ITextFont font) => new()
    {
        Background = Color.FromRgb(0x0E, 0x12, 0x18),
        Panel = Color.FromRgb(0x18, 0x1E, 0x28),
        PanelFocused = Color.FromRgb(0x24, 0x2E, 0x3C),
        Accent = Color.FromRgb(0x4C, 0x9A, 0xFF),
        Text = Color.FromRgb(0xEC, 0xF0, 0xF4),
        TextMuted = Color.FromRgb(0x8A, 0x94, 0xA0),
        Border = Color.FromRgb(0x2C, 0x36, 0x44),
        Font = font,
        TextScale = settings.TextScale,
    };

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        for (int i = _stack.Count - 1; i >= 0; i--)
            _stack[i].Dispose();
        _stack.Clear();
        Dialogs.Dispose();
    }
}

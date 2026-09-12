// Prospero Explorer
// Copyright (C) 2026 SvenGDK

using SharpProspero.Interop;
using SharpProspero.Platform;
using System;

// Both namespaces publish a MessageDialog and an ErrorDialog: the raw one the platform declares, and
// the wrapper that drives it. This file wants the wrappers, so only the three values it needs are
// taken from the raw side by name.
using ImeDialogEndStatus = SharpProspero.Interop.Dialog.ImeDialogEndStatus;
using ImeType = SharpProspero.Interop.Dialog.ImeType;
using MsgDialogButtonId = SharpProspero.Interop.Dialog.MsgDialogButtonId;

namespace ProsperoExplorer.Shell;

/// <summary>
/// The system overlays, driven from the frame loop instead of a wait loop.
///
/// Each of these draws over the running application and only advances when it is pumped, so a caller
/// that waits for one inside its own loop stops presenting and the whole application looks frozen.
/// Everything here is asked for and answered later: the caller hands over what to do with the result
/// and carries on, and this advances whichever overlay is open once a frame.
///
/// One overlay is open at a time. A request made while another is open is queued and opened when that
/// one closes, so two pages asking at once cannot leave the system with two dialogs.
/// </summary>
internal sealed class SystemDialogs : IDisposable
{
    // A request and what to answer the caller with when the overlay will not open. The two travel
    // together so a queued request that fails later still releases whoever asked for it.
    private readonly System.Collections.Generic.Queue<(Action Open, Action Abandon)> _queued = new();
    private IDisposable? _open;
    private Func<bool>? _pump;
    private bool _disposed;

    /// <summary>Whether an overlay is on screen. The page underneath should not act on input.</summary>
    public bool IsBusy => _open is not null;

    /// <summary>Advances whatever is open, and starts the next request when it closes.</summary>
    public void Tick()
    {
        if (_open is not null)
        {
            bool finished;
            try
            {
                finished = _pump!();
            }
            catch (ProsperoException)
            {
                // The overlay failed while running. Close it rather than pumping a broken one forever.
                finished = true;
            }

            if (!finished)
                return;

            _open.Dispose();
            _open = null;
            _pump = null;
        }

        if (_open is null && _queued.Count > 0)
        {
            (Action open, Action abandon) = _queued.Dequeue();
            Start(open, abandon);
        }
    }

    /// <summary>
    /// Shows <paramref name="message"/> with an OK button. <paramref name="closed"/> runs when it
    /// closes.
    /// </summary>
    public void Alert(string message, Action? closed = null)
        => Request(() =>
        {
            MessageDialog dialog = MessageDialog.ShowMessage(message, MessageDialogButtons.Ok);
            Begin(dialog, () =>
            {
                if (dialog.Update() == MessageDialogState.Running)
                    return false;
                closed?.Invoke();
                return true;
            });
        }, () => closed?.Invoke());

    /// <summary>
    /// Asks <paramref name="question"/> with Yes and No. <paramref name="answered"/> is given true for
    /// Yes.
    /// </summary>
    public void Confirm(string question, Action<bool> answered)
        => Request(() =>
        {
            MessageDialog dialog = MessageDialog.ShowMessage(question, MessageDialogButtons.YesNo);
            Begin(dialog, () =>
            {
                if (dialog.Update() == MessageDialogState.Running)
                    return false;
                answered(dialog.ChosenButton == MsgDialogButtonId.Ok);
                return true;
            });
        }, () => answered(false));

    /// <summary>
    /// Opens the on-screen keyboard. <paramref name="entered"/> is given the text, or null when the
    /// user cancelled.
    /// </summary>
    public void AskText(
        string title,
        string initialText,
        Action<string?> entered,
        int maxLength = 256,
        ImeType type = ImeType.Default,
        string? placeholder = null)
        => Request(() =>
        {
            TextInputDialog dialog = TextInputDialog.Open(
                title, maxLength, type, placeholder, initialText);
            Begin(dialog, () =>
            {
                if (dialog.Update() == TextInputState.Running)
                    return false;
                entered(dialog.EndStatus == ImeDialogEndStatus.Ok ? dialog.Text : null);
                return true;
            });
        }, () => entered(null));

    /// <summary>Opens the browser at <paramref name="url"/>. <paramref name="closed"/> runs when it closes.</summary>
    public void OpenBrowser(string url, Action? closed = null)
        => Request(() =>
        {
            WebBrowser browser = WebBrowser.Open(url);
            Begin(browser, () =>
            {
                if (browser.Update() == WebBrowserState.Running)
                    return false;
                closed?.Invoke();
                return true;
            });
        }, () => closed?.Invoke());

    /// <summary>
    /// Shows the system's own box for <paramref name="errorCode"/>, which is how a platform failure is
    /// meant to be put to the user: the box names the code and says what it means in their language.
    /// </summary>
    public void ShowErrorCode(int errorCode, Action? closed = null)
        => Request(() =>
        {
            ErrorDialog dialog = ErrorDialog.Show(errorCode);
            Begin(dialog, () =>
            {
                if (dialog.Update() == ErrorDialogState.Running)
                    return false;
                closed?.Invoke();
                return true;
            });
        }, () => closed?.Invoke());

    /// <summary>
    /// Opens a progress bar the caller drives. <paramref name="step"/> runs once a frame and returns
    /// false while there is more to do; it is given the bar so it can move it and change its caption.
    /// <paramref name="finished"/> runs once the work reports it is done.
    /// </summary>
    public void RunWithProgress(string caption, Func<MessageDialog, bool> step, Action? finished = null)
        => Request(() =>
        {
            MessageDialog dialog = MessageDialog.ShowProgress(caption);
            Begin(dialog, () =>
            {
                bool done;
                try
                {
                    done = step(dialog);
                }
                catch (Exception)
                {
                    // The work threw. Take the bar down; the caller reports what happened itself.
                    dialog.Update();
                    finished?.Invoke();
                    return true;
                }

                dialog.Update();
                if (!done)
                    return false;
                finished?.Invoke();
                return true;
            });
        }, () => finished?.Invoke());

    /// <summary>
    /// Asks for an overlay, now or when the one on screen closes.
    /// </summary>
    /// <remarks>
    /// Opening can fail: the module may be absent, or the subsystem may refuse. A failure must not
    /// take the application down, and it must not leave the caller waiting either - a page that asked
    /// a question and never hears back sits there with its work half done and no way to carry on. So a
    /// request that cannot be opened is answered with <paramref name="abandon"/> instead, which tells
    /// the caller the same thing a cancelled overlay would.
    /// </remarks>
    private void Request(Action open, Action abandon)
    {
        if (_open is not null)
        {
            _queued.Enqueue((open, abandon));
            return;
        }
        Start(open, abandon);
    }

    private void Start(Action open, Action abandon)
    {
        try
        {
            open();
        }
        catch (Exception)
        {
            _open = null;
            _pump = null;
            SafelyAbandon(abandon);
        }
    }

    // The caller's own answer path can throw as well, and it runs while the overlays are being
    // advanced. Letting it out would take down the frame over a message that could not be shown.
    private static void SafelyAbandon(Action abandon)
    {
        try
        {
            abandon();
        }
        catch (Exception)
        {
        }
    }

    private void Begin(IDisposable dialog, Func<bool> pump)
    {
        _open = dialog;
        _pump = pump;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _queued.Clear();
        _open?.Dispose();
        _open = null;
        _pump = null;
    }
}

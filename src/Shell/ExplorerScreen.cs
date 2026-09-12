// Prospero Explorer
// Copyright (C) 2026 SvenGDK

using SharpProspero.Application;
using SharpProspero.Ui;
using System;

namespace ProsperoExplorer.Shell;

/// <summary>
/// One page of the application. A page builds its own tree of elements once, is told when it comes to
/// the front and when it leaves, and is given the frame while it is showing so it can advance whatever
/// it owns - a decoder, a transfer, a listening socket.
/// </summary>
/// <remarks>
/// A page never draws directly. It returns a tree and the shell lays it out, which keeps every page
/// looking the same and keeps the navigation in one place.
/// </remarks>
internal abstract class ExplorerScreen : IDisposable
{
    private UiScreen? _screen;
    private bool _disposed;

    protected ExplorerScreen(ExplorerShell shell) => Shell = shell;

    /// <summary>The shell this page runs in: navigation, settings, messages and dialogs.</summary>
    protected ExplorerShell Shell { get; }

    /// <summary>The name shown in the header while this page is at the front.</summary>
    public abstract string Title { get; }

    /// <summary>A line under the title saying what the buttons do here.</summary>
    public virtual string Hint => "Cross selects, Circle goes back.";

    /// <summary>The page, built once and kept. It draws with the shell's theme.</summary>
    public UiScreen Screen => _screen ??= new UiScreen(BuildRoot(), Shell.Theme) { Cancelled = HandleCancel };

    /// <summary>
    /// Discards the built page so the next time it is shown it is rebuilt from <see cref="BuildRoot"/>.
    /// A page whose content depends on something that can change while it is open - the set of readable
    /// folders, say - calls this after that thing changes so the tree reflects it.
    /// </summary>
    protected void RebuildScreen() => _screen = null;

    /// <summary>Builds the page's tree of elements. Called the first time the page is shown after a build.</summary>
    protected abstract UiElement BuildRoot();

    /// <summary>Runs while this page is at the front, once a frame, before it is drawn.</summary>
    public virtual void Tick(FrameContext context)
    {
    }

    /// <summary>Runs when this page comes to the front, including after a page above it closes.</summary>
    public virtual void OnShown()
    {
    }

    /// <summary>Runs when a page opens above this one, or when this one closes.</summary>
    public virtual void OnHidden()
    {
    }

    /// <summary>
    /// Runs when Circle is pressed. Return true when the page dealt with it; returning false closes the
    /// page, and closing the last page leaves the application.
    /// </summary>
    protected virtual bool OnCancel() => false;

    /// <summary>
    /// Whether the shell keeps giving this page the frame after it leaves the front. A page holding
    /// something that has to keep running - a server, a transfer - says yes.
    /// </summary>
    public virtual bool TicksInBackground => false;

    private void HandleCancel()
    {
        if (!OnCancel())
            Shell.Pop();
    }

    /// <summary>Releases what the page holds. Runs once, when the page closes.</summary>
    protected virtual void OnDispose()
    {
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        OnDispose();
    }
}

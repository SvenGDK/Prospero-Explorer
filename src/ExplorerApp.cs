// Prospero Explorer
// Copyright (C) 2026 SvenGDK

using ProsperoExplorer.Ftp;
using ProsperoExplorer.Shell;
using SharpProspero.Application;
using SharpProspero.Diagnostics;
using SharpProspero.Graphics;
using SharpProspero.Interop;
using SharpProspero.Interop.Font;
using SharpProspero.Interop.Sysmodule;
using SharpProspero.Modules;
using SharpProspero.Platform;
using SharpProspero.Storage;
using System;

namespace ProsperoExplorer;

/// <summary>
/// The application. It brings up the settings, the shell and the first page, then hands every frame to
/// the shell: overlays, pages, drawing.
/// </summary>
internal sealed class ExplorerApp : ProsperoApp
{
    private AppSettings? _settings;
    private ExplorerShell? _shell;
    private FtpService? _ftp;
    private FileLogSink? _log;
    private SystemModule? _fontModule;
    private SystemModule? _fontFtModule;
    private SystemModule? _freeTypeModule;
    private SystemFont? _systemFont;

    protected override void OnLoad()
    {
        _settings = AppSettings.Load();
        _shell = new ExplorerShell(_settings, LoadUiFont(_settings));

        OpenLog();

        // The file service belongs to the application rather than to the page that shows it, so it
        // keeps running while the user browses and is still there when they come back to it.
        _ftp = new FtpService(_settings);
        if (_settings.FtpAutoStart)
            StartFileServiceAtLaunch();

        _shell.Push(new HomeScreen(_shell, _ftp));

        // A module sees only part of the file system, and which part depends on how it was started.
        // Saying so once is better than the browser refusing whatever it was told to open.
        if (Places.Reachable.Count == 0)
        {
            // The root the module was given is the candidate that should always be there, so the reason
            // it gave is the one worth putting in front of whoever has to work out what happened.
            _shell.ReportFailure(
                "No folder could be read",
                new InvalidOperationException($"The root gave: {Places.ReasonFor("/")}."),
                important: true);
        }
        else if (AppSettings.DataFolder is null)
            _shell.Status("Settings cannot be kept: nowhere would take them.");

        // Browsing and playing are not a game, so let the machine settle rather than holding it awake.
        try
        {
            SystemControl.KeepAwake();
        }
        catch (ProsperoException)
        {
            // Nothing depends on it; the system falls back to its own idle behaviour.
        }
    }

    protected override void OnFrame(FrameContext context)
    {
        ExplorerShell shell = _shell!;

        // The file service belongs to the application, not to the page showing it, so it is advanced
        // here: a client stays connected and a transfer keeps moving while the user browses elsewhere.
        // It moves a bounded amount each frame, so a large transfer never holds up the drawing.
        try
        {
            _ftp!.Tick();
        }
        catch (Exception error)
        {
            // A failure inside the service takes the service down, not the application.
            _ftp!.Stop();
            shell.ReportFailure("The file service stopped", error);
        }

        shell.Tick(context);
        shell.Draw(context);

        if (shell.ExitRequested)
            context.RequestExit();
    }

    protected override void OnUnload()
    {
        // Anything the user changed and did not explicitly save is still worth keeping.
        _settings?.Save();
        _ftp?.Dispose();
        _shell?.Dispose();
        _systemFont?.Dispose();
        _freeTypeModule?.Dispose();
        _fontFtModule?.Dispose();
        _fontModule?.Dispose();
        Log.ClearSinks();
        _log?.Dispose();
    }

    // Loads the crisp system font the interface draws with, falling back to the built-in bitmap text
    // when the font engine or the font set is not available so the interface still draws either way.
    private ITextFont LoadUiFont(AppSettings settings)
    {
        try
        {
            // The glyph renderer draws through the FreeType OpenType backend, so all three modules are
            // loaded before a font is created; without the backend the renderer reaches an unresolved
            // routine and the process faults.
            _fontModule = SystemModule.Load(SystemModuleId.Font);
            _fontFtModule = SystemModule.Load(SystemModuleId.FontFt);
            _freeTypeModule = SystemModule.Load(SystemModuleId.FreeTypeOt);
            float pixelSize = MathF.Max(14f, settings.TextScale * 11f);
            _systemFont = SystemFont.Open(SceFontSet.StdEuropeanW1G, pixelSize);
            Log.Information("Interface font ready.");
            return _systemFont;
        }
        catch (Exception error)
        {
            Log.Warning($"The system font could not be opened; using the built-in text. {error.Message}");
            _systemFont?.Dispose();
            _systemFont = null;
            _freeTypeModule?.Dispose();
            _freeTypeModule = null;
            _fontFtModule?.Dispose();
            _fontFtModule = null;
            _fontModule?.Dispose();
            _fontModule = null;
            return new BitmapTextFont(settings.TextScale);
        }
    }

    private void OpenLog()
    {
        try
        {
            if (AppSettings.DataFolder is null)
                return;
            _log = FileLogSink.Open(AppSettings.DataFolder + "/explorer.log");
            Log.AddSink(_log);
            Log.MinimumLevel = LogLevel.Information;
            Log.Information("Prospero Explorer started.");
        }
        catch (Exception)
        {
            // The writable area may not be there on a first run. The application does not need a log.
        }
    }

    private void StartFileServiceAtLaunch()
    {
        try
        {
            _ftp!.Start();
            _shell!.Notify($"File service listening on port {_settings!.FtpPort}.");
        }
        catch (Exception error)
        {
            _shell!.ReportFailure("The file service could not start", error);
        }
    }
}

internal static class Program
{
    private static void Main()
    {
        using (var app = new ExplorerApp())
            app.Run();

        // Returning from here is reported to the platform as a fault and the user is shown the box
        // that says the application closed unexpectedly, even when everything went as intended. The
        // process is ended through the C library instead.
        ProcessExit.Exit();
    }
}

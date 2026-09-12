// Prospero Explorer
// Copyright (C) 2026 SvenGDK

using ProsperoExplorer.Ftp;
using ProsperoExplorer.Shell;
using SharpProspero.Application;
using SharpProspero.Interop.Dialog;
using SharpProspero.Text;
using SharpProspero.Ui;
using System;
using System.Collections.Generic;

namespace ProsperoExplorer.Settings;

/// <summary>
/// Every setting the application keeps, one control per value, divided into tabs so a section fits on
/// screen without scrolling far. A control writes its value straight into <see cref="AppSettings"/> and
/// says on the message strip what it changed, so nothing is left half applied; the file is written when
/// the user asks for it and again when they leave.
/// </summary>
/// <remarks>
/// The tree is built once, so restoring the defaults cannot rebuild it. Each control registers how to
/// re-read its own value when it is created, and restoring runs those, which puts the shown values back
/// without touching the layout or losing where the focus is.
/// </remarks>
internal sealed class SettingsScreen : ExplorerScreen
{
    // Where the settings ended up. It is not known until the application has looked for somewhere that
    // will take them, so it is read rather than written down.
    private static string SettingsFile =>
        AppSettings.DataFolder is null ? "nowhere writable" : AppSettings.DataFolder + "/settings.json";

    // Tall enough for the longest section to show most of itself at once, short enough that the tab row
    // and the buttons under it still fit above the bottom of the screen.
    private const int MenuHeight = 520;

    private static readonly string[] SortNames = ["name", "size", "kind"];
    private static readonly string[] ArchiveFormatNames = ["zip", "tar"];
    private static readonly string[] ImageFitNames = ["fit", "fill", "actual size"];
    private static readonly string[] ImageFormatNames = ["png", "jpeg", "bmp", "tga"];

    private readonly FtpService _service;
    private readonly List<Action> _refreshers = [];
    private readonly TabView _tabs = new();
    private readonly KeyValueRow _serviceState = new("Service", "stopped");
    private readonly Stepper _servicePort;

    /// <summary>Creates the settings page, restarting <paramref name="service"/> when asked to.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> is null.</exception>
    public SettingsScreen(ExplorerShell shell, FtpService service) : base(shell)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;

        // Built before the section that holds it, because typing a port writes back into it.
        _servicePort = Number(
            "Port",
            () => Shell.Settings.FtpPort,
            1,
            65535,
            1,
            null,
            value => Shell.Settings.FtpPort = (int)value);

        _tabs.Add("General", BuildGeneral());
        _tabs.Add("Browser", BuildBrowser());
        _tabs.Add("Archive", BuildArchive());
        _tabs.Add("Media", BuildMedia());
        _tabs.Add("Text", BuildText());
        _tabs.Add("Pictures", BuildPictures());
        _tabs.Add("File service", BuildFileService());
        _tabs.Add("Packages", BuildPackages());
    }

    /// <inheritdoc/>
    public override string Title => "Settings";

    /// <inheritdoc/>
    public override string Hint => "Left and right change a value, Circle saves and goes back.";

    /// <inheritdoc/>
    protected override UiElement BuildRoot()
        => new StackPanel()
            .Add(new KeyValueRow("Settings file", SettingsFile))
            .Add(_tabs)
            .Add(new Separator())
            .Add(new Row()
                .Add(new Button("Save now", SaveNow))
                .Add(new Button("Restore defaults", RestoreDefaults)))
            .Add(new Label("Every change applies at once. Leaving this page writes the file.")
            {
                TextColor = Shell.Theme.TextMuted,
            });

    /// <inheritdoc/>
    public override void Tick(FrameContext context)
        => _serviceState.Value = _service.IsRunning
            ? $"listening on port {_service.Port}, {_service.ClientCount} connected"
            : "stopped";

    /// <summary>Writes the settings on the way out, so a change survives without pressing Save.</summary>
    protected override void OnDispose()
    {
        if (!Shell.Settings.Save())
            Shell.Status($"The settings could not be written to {SettingsFile}.");
    }

    private ScrollMenu BuildGeneral()
        => new ScrollMenu { ViewHeight = MenuHeight }
            .Add(ValueButton(
                "Start folder",
                () => Shell.Settings.StartPath,
                ImeType.Default,
                256,
                value => Shell.Settings.StartPath = value))
            .Add(Flag(
                "Ask before deleting or overwriting",
                () => Shell.Settings.ConfirmDestructive,
                value => Shell.Settings.ConfirmDestructive = value))
            .Add(Flag(
                "List names beginning with a full stop",
                () => Shell.Settings.ShowHiddenFiles,
                value => Shell.Settings.ShowHiddenFiles = value))
            .Add(Flag(
                "Raise a system notification when something finishes",
                () => Shell.Settings.SystemNotifications,
                value => Shell.Settings.SystemNotifications = value))
            .Add(Slide(
                "Message time",
                () => Shell.Settings.ToastSeconds,
                1f,
                12f,
                0.5f,
                value => $"{value:0.#} seconds",
                value => Shell.Settings.ToastSeconds = value))
            .Add(ValueButton(
                "Web address",
                () => Shell.Settings.HomeUrl,
                ImeType.Url,
                512,
                value => Shell.Settings.HomeUrl = value));

    private ScrollMenu BuildBrowser()
        => new ScrollMenu { ViewHeight = MenuHeight }
            .Add(Choice(
                "Order by",
                SortNames,
                () => (int)Shell.Settings.Sort,
                index => Shell.Settings.Sort = (SortOrder)index))
            .Add(Flag(
                "Folders before files",
                () => Shell.Settings.FoldersFirst,
                value => Shell.Settings.FoldersFirst = value))
            .Add(Number(
                "Rows shown",
                () => Shell.Settings.ListRows,
                6,
                24,
                1,
                null,
                value => Shell.Settings.ListRows = (int)value))
            .Add(Flag(
                "Show each file's size",
                () => Shell.Settings.ShowSizes,
                value => Shell.Settings.ShowSizes = value));

    private ScrollMenu BuildArchive()
        => new ScrollMenu { ViewHeight = MenuHeight }
            .Add(Choice(
                "New archives",
                ArchiveFormatNames,
                () => (int)Shell.Settings.ArchiveFormat,
                index => Shell.Settings.ArchiveFormat = (ArchiveFormat)index))
            .Add(Flag(
                "Compress what is added",
                () => Shell.Settings.ArchiveCompress,
                value => Shell.Settings.ArchiveCompress = value))
            .Add(ValueButton(
                "Unpack to",
                () => Shell.Settings.ExtractPath,
                ImeType.Default,
                256,
                value => Shell.Settings.ExtractPath = value))
            .Add(new Label("Tar holds its entries uncompressed whatever this says.")
            {
                TextColor = Shell.Theme.TextMuted,
            });

    private ScrollMenu BuildMedia()
        => new ScrollMenu { ViewHeight = MenuHeight }
            .Add(Slide(
                "Volume",
                () => Shell.Settings.MediaVolume,
                0f,
                100f,
                5f,
                value => $"{(int)value}%",
                value => Shell.Settings.MediaVolume = (int)value))
            .Add(Flag(
                "Start again at the end",
                () => Shell.Settings.MediaLoop,
                value => Shell.Settings.MediaLoop = value))
            .Add(Flag(
                "Play as soon as a file opens",
                () => Shell.Settings.MediaAutoPlay,
                value => Shell.Settings.MediaAutoPlay = value))
            .Add(Flag(
                "Draw the time and controls over the picture",
                () => Shell.Settings.MediaShowOverlay,
                value => Shell.Settings.MediaShowOverlay = value));

    private ScrollMenu BuildText()
        => new ScrollMenu { ViewHeight = MenuHeight }
            .Add(Flag(
                "Break a long line to fit",
                () => Shell.Settings.TextWordWrap,
                value => Shell.Settings.TextWordWrap = value))
            .Add(Number(
                "Text size",
                () => Shell.Settings.TextScale,
                1,
                4,
                1,
                null,
                value => Shell.Settings.TextScale = (int)value))
            .Add(Number(
                "Spaces to a tab",
                () => Shell.Settings.TextTabWidth,
                1,
                8,
                1,
                null,
                value => Shell.Settings.TextTabWidth = (int)value))
            .Add(Number(
                "Largest file opened",
                () => Shell.Settings.TextMaxKilobytes,
                16,
                65536,
                512,
                value => TextFormat.ByteSize(value * 1024L),
                value => Shell.Settings.TextMaxKilobytes = (int)value));

    private ScrollMenu BuildPictures()
        => new ScrollMenu { ViewHeight = MenuHeight }
            .Add(Choice(
                "Fit to screen",
                ImageFitNames,
                () => (int)Shell.Settings.ImageFit,
                index => Shell.Settings.ImageFit = (ImageFit)index))
            .Add(Choice(
                "Write pictures as",
                ImageFormatNames,
                () => (int)Shell.Settings.ImageExportFormat,
                index => Shell.Settings.ImageExportFormat = (ImageFormat)index))
            .Add(Number(
                "Detail kept in a jpeg",
                () => Shell.Settings.JpegQuality,
                1,
                100,
                5,
                value => value + "%",
                value => Shell.Settings.JpegQuality = (int)value))
            .Add(Number(
                "Png compression",
                () => Shell.Settings.PngCompression,
                0,
                9,
                1,
                null,
                value => Shell.Settings.PngCompression = (int)value))
            .Add(Flag(
                "Smooth a picture drawn at another size",
                () => Shell.Settings.ImageSmoothScaling,
                value => Shell.Settings.ImageSmoothScaling = value));

    private ScrollMenu BuildFileService()
        => new ScrollMenu { ViewHeight = MenuHeight }
            .Add(_serviceState)
            .Add(new Label("The port, the client root folder and the login requirement take effect for new connections only. The other settings here apply at once, including to clients already connected.")
            {
                TextColor = Shell.Theme.TextMuted,
            })
            .Add(_servicePort)
            .Add(new Button("Type a port number", AskPort))
            .Add(Flag(
                "Start with the application",
                () => Shell.Settings.FtpAutoStart,
                value => Shell.Settings.FtpAutoStart = value))
            .Add(Flag(
                "Require a name and a password",
                () => Shell.Settings.FtpRequireLogin,
                value => Shell.Settings.FtpRequireLogin = value))
            .Add(ValueButton(
                "Name",
                () => Shell.Settings.FtpUser,
                ImeType.BasicLatin,
                64,
                value => Shell.Settings.FtpUser = value))
            .Add(ValueButton(
                "Password",
                () => Shell.Settings.FtpPassword,
                ImeType.BasicLatin,
                64,
                value => Shell.Settings.FtpPassword = value,
                Mask,
                trim: false))
            .Add(ValueButton(
                "Client root folder",
                () => Shell.Settings.FtpRoot,
                ImeType.Default,
                256,
                value => Shell.Settings.FtpRoot = value))
            .Add(Flag(
                "Let clients write, delete and rename",
                () => Shell.Settings.FtpAllowWrite,
                value => Shell.Settings.FtpAllowWrite = value))
            .Add(new Button("Restart the service", RestartService));

    private ScrollMenu BuildPackages()
        => new ScrollMenu { ViewHeight = MenuHeight }
            .Add(ValueButton(
                "Folders searched",
                () => Shell.Settings.PackageSearchPaths,
                ImeType.Default,
                512,
                value => Shell.Settings.PackageSearchPaths = value))
            .Add(new Label("Separate one folder from the next with a colon.")
            {
                TextColor = Shell.Theme.TextMuted,
            })
            .Add(Flag(
                "Ask before installing",
                () => Shell.Settings.ConfirmInstall,
                value => Shell.Settings.ConfirmInstall = value));

    // A flag: reads its state when it is built and again when the defaults are restored.
    private Checkbox Flag(string name, Func<bool> read, Action<bool> apply)
    {
        var box = new Checkbox(name, read(), value =>
        {
            apply(value);
            Announce(name, value ? "on" : "off");
        });
        _refreshers.Add(() => box.Checked = read());
        return box;
    }

    // One choice from a fixed set. The index is the enum's own value, so the order of the names has to
    // match the order the enum declares.
    private OptionSelector Choice(string name, string[] options, Func<int> read, Action<int> apply)
    {
        var selector = new OptionSelector(name, options, read(), index =>
        {
            apply(index);
            Announce(name, options[index]);
        });
        _refreshers.Add(() => selector.SelectedIndex = read());
        return selector;
    }

    // A whole number over a range. The control clamps what it is given, so the clamped value is written
    // straight back: a stored value outside the range would otherwise differ from the one on screen.
    private Stepper Number(
        string name,
        Func<long> read,
        long minimum,
        long maximum,
        long step,
        Func<long, string>? format,
        Action<long> apply)
    {
        var stepper = new Stepper(name, read(), minimum, maximum, step, format, value =>
        {
            apply(value);
            Announce(name, format is null ? value.ToString() : format(value));
        });
        apply(stepper.Value);
        _refreshers.Add(() => stepper.Value = read());
        return stepper;
    }

    private Slider Slide(
        string name,
        Func<float> read,
        float minimum,
        float maximum,
        float step,
        Func<float, string> describe,
        Action<float> apply)
    {
        var slider = new Slider(name, minimum, maximum, read(), step, value =>
        {
            apply(value);
            Announce(name, describe(value));
        });
        apply(slider.Value);
        _refreshers.Add(() => slider.Value = read());
        return slider;
    }

    // A path or a name, typed on the on-screen keyboard. The button carries the current value, so the
    // section reads as a list of settings rather than a list of things to press.
    private Button ValueButton(
        string name,
        Func<string> read,
        ImeType type,
        int maxLength,
        Action<string> apply,
        Func<string, string>? display = null,
        bool trim = true)
    {
        string Shown() => display is null ? read() : display(read());

        var button = new Button(name + ": " + Shown());
        button.Activated = () => Shell.Dialogs.AskText(
            name,
            read(),
            text =>
            {
                if (text is null)
                {
                    Shell.Status($"{name} left as it was.");
                    return;
                }

                string value = trim ? text.Trim() : text;
                if (value.Length == 0)
                {
                    Shell.Status($"{name} needs a value.");
                    return;
                }

                apply(value);
                button.Text = name + ": " + Shown();
                Announce(name, Shown());
            },
            maxLength: maxLength,
            type: type);

        _refreshers.Add(() => button.Text = name + ": " + Shown());
        return button;
    }

    // Stepping one at a time from 2121 to a port at the other end of the range is not worth asking of
    // anyone, so a port can also be typed.
    private void AskPort()
        => Shell.Dialogs.AskText(
            "File service port",
            Shell.Settings.FtpPort.ToString(),
            text =>
            {
                if (text is null)
                {
                    Shell.Status("Port left as it was.");
                    return;
                }

                if (!int.TryParse(text.Trim(), out int port) || port < 1 || port > 65535)
                {
                    Shell.Status("A port has to be a number from 1 to 65535.");
                    return;
                }

                Shell.Settings.FtpPort = port;
                _servicePort.Value = port;
                Announce("Port", port.ToString());
            },
            maxLength: 5,
            type: ImeType.Number);

    private void RestartService()
    {
        try
        {
            if (_service.IsRunning)
                _service.Stop();
            _service.Start();
            Shell.Notify($"File service listening on port {_service.Port}.");
        }
        catch (Exception error)
        {
            Shell.ReportFailure("The file service could not restart", error);
        }
    }

    private void SaveNow()
    {
        if (Shell.Settings.Save())
            Shell.Notify("Settings saved.");
        else
            Shell.Dialogs.Alert($"The settings could not be written to {SettingsFile}.");
    }

    // Restoring throws away everything the user set, so it asks whatever the confirm setting says.
    private void RestoreDefaults()
        => Shell.Dialogs.Confirm("Put every setting back to its default?", confirmed =>
        {
            if (!confirmed)
            {
                Shell.Status("Settings left as they are.");
                return;
            }

            Shell.Settings.Reset();
            foreach (Action refresh in _refreshers)
                refresh();
            Shell.Notify("Every setting is back to its default.");
        });

    private void Announce(string name, string value) => Shell.Status($"{name}: {value}");

    private static string Mask(string value)
        => value.Length == 0 ? "(not set)" : new string('*', Math.Min(value.Length, 12));
}

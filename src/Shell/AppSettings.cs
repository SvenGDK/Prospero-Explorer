// Prospero Explorer
// Copyright (C) 2026 SvenGDK

using SharpProspero.Storage;
using System;
using System.Collections.Generic;

namespace ProsperoExplorer.Shell;

/// <summary>How a folder listing is ordered.</summary>
internal enum SortOrder
{
    /// <summary>By name, a to z.</summary>
    Name,

    /// <summary>Largest first.</summary>
    Size,

    /// <summary>By what the name ends in, then by name.</summary>
    Kind,
}

/// <summary>How a picture is fitted to the screen.</summary>
internal enum ImageFit
{
    /// <summary>As large as it goes without cropping or changing its shape.</summary>
    Contain,

    /// <summary>Filling the screen, cropping whatever does not fit.</summary>
    Cover,

    /// <summary>At its own size, however that lands.</summary>
    Actual,
}

/// <summary>The form a picture is written back out in.</summary>
internal enum ImageFormat
{
    /// <summary>Lossless, with an alpha channel.</summary>
    Png,

    /// <summary>Lossy, no alpha channel, smaller.</summary>
    Jpeg,

    /// <summary>Uncompressed, widely readable.</summary>
    Bmp,

    /// <summary>Uncompressed with an alpha channel.</summary>
    Tga,
}

/// <summary>The form an archive is written in.</summary>
internal enum ArchiveFormat
{
    /// <summary>One file per entry, each compressed on its own.</summary>
    Zip,

    /// <summary>Entries stored end to end, uncompressed.</summary>
    Tar,
}

/// <summary>
/// Everything the application remembers between runs, held as one object and written to the writable
/// area as text. Every value has a default that stands on its own, so a first run with no file, or a
/// file that has been damaged, still starts.
/// </summary>
internal sealed class AppSettings
{
    /// <summary>The name of the folder the application keeps its own files in.</summary>
    public const string FolderName = "prospero-explorer";

    /// <summary>
    /// Where that folder ended up, or null when nowhere would take it. A module sees only part of the
    /// file system and which part depends on how it was started, so this is found by writing rather
    /// than named here.
    /// </summary>
    public static string? DataFolder { get; private set; }

    private static string? SettingsPath => DataFolder is null ? null : DataFolder + "/settings.json";

    // General.

    /// <summary>
    /// The folder the browser opens at. Empty means the first folder that can be read, which is what
    /// a first run uses because nothing is known about the file system yet.
    /// </summary>
    public string StartPath { get; set; } = string.Empty;

    /// <summary>Whether deleting and overwriting ask first.</summary>
    public bool ConfirmDestructive { get; set; } = true;

    /// <summary>Whether names beginning with a full stop are listed.</summary>
    public bool ShowHiddenFiles { get; set; } = false;

    /// <summary>Whether a finished action also raises a system notification.</summary>
    public bool SystemNotifications { get; set; } = true;

    /// <summary>How long a message stays on screen, in seconds.</summary>
    public float ToastSeconds { get; set; } = 3f;

    // Browser.

    /// <summary>How a folder listing is ordered.</summary>
    public SortOrder Sort { get; set; } = SortOrder.Name;

    /// <summary>Whether folders are listed before files whatever the order.</summary>
    public bool FoldersFirst { get; set; } = true;

    /// <summary>How many rows the listing shows at once.</summary>
    public int ListRows { get; set; } = 14;

    /// <summary>Whether each file's size is shown beside its name.</summary>
    public bool ShowSizes { get; set; } = true;

    // Archive.

    /// <summary>The form a new archive is written in.</summary>
    public ArchiveFormat ArchiveFormat { get; set; } = ArchiveFormat.Zip;

    /// <summary>Whether entries added to a new archive are compressed.</summary>
    public bool ArchiveCompress { get; set; } = true;

    /// <summary>
    /// Where an archive is unpacked to when no folder is chosen. Empty means beside the archive.
    /// </summary>
    public string ExtractPath { get; set; } = string.Empty;

    // Media.

    /// <summary>The output level, 0 to 100.</summary>
    public int MediaVolume { get; set; } = 80;

    /// <summary>Whether playback starts again at the end.</summary>
    public bool MediaLoop { get; set; } = false;

    /// <summary>Whether opening a file starts it playing.</summary>
    public bool MediaAutoPlay { get; set; } = true;

    /// <summary>Whether the elapsed time and the controls are drawn over the picture.</summary>
    public bool MediaShowOverlay { get; set; } = true;

    // Text.

    /// <summary>Whether a long line is broken to fit the width.</summary>
    public bool TextWordWrap { get; set; } = true;

    /// <summary>How large the text is drawn, 1 to 4.</summary>
    public int TextScale { get; set; } = 2;

    /// <summary>How many spaces a tab stands for.</summary>
    public int TextTabWidth { get; set; } = 4;

    /// <summary>The largest file the viewer opens, in kilobytes.</summary>
    public int TextMaxKilobytes { get; set; } = 4096;

    // Images.

    /// <summary>How a picture is fitted to the screen.</summary>
    public ImageFit ImageFit { get; set; } = ImageFit.Contain;

    /// <summary>The form a picture is written back out in.</summary>
    public ImageFormat ImageExportFormat { get; set; } = ImageFormat.Png;

    /// <summary>How much detail a lossy picture keeps, 1 to 100.</summary>
    public int JpegQuality { get; set; } = 90;

    /// <summary>How hard a lossless picture is compressed, 0 to 9.</summary>
    public int PngCompression { get; set; } = 6;

    /// <summary>Whether a smooth scale is used when a picture is drawn at another size.</summary>
    public bool ImageSmoothScaling { get; set; } = true;

    // Transfers.

    /// <summary>Which port the file service listens on.</summary>
    public int FtpPort { get; set; } = 2121;

    /// <summary>Whether the service starts with the application.</summary>
    public bool FtpAutoStart { get; set; } = false;

    /// <summary>Whether a client has to give a name and a password.</summary>
    public bool FtpRequireLogin { get; set; } = false;

    /// <summary>The name a client gives when a login is required.</summary>
    public string FtpUser { get; set; } = "explorer";

    /// <summary>The password a client gives when a login is required.</summary>
    public string FtpPassword { get; set; } = "explorer";

    /// <summary>The folder a client starts in and cannot climb above.</summary>
    public string FtpRoot { get; set; } = "/";

    /// <summary>Whether a client may write, delete and rename.</summary>
    public bool FtpAllowWrite { get; set; } = true;

    // Packages.

    /// <summary>
    /// The folders searched for installable files, separated by a colon. Empty means every folder the
    /// application can reach, which is what a first run uses.
    /// </summary>
    public string PackageSearchPaths { get; set; } = string.Empty;

    /// <summary>Whether installing asks first.</summary>
    public bool ConfirmInstall { get; set; } = true;

    // Web.

    /// <summary>The address the browser opens at when nothing else is given.</summary>
    public string HomeUrl { get; set; } = "https://";

    /// <summary>
    /// Finds somewhere to keep the settings, then reads them back, falling back to the defaults for
    /// anything absent or damaged.
    /// </summary>
    public static AppSettings Load()
    {
        DataFolder = Places.DataFolder(FolderName);

        var settings = new AppSettings();
        try
        {
            string? path = SettingsPath;
            if (path is null || !FileSystem.Exists(path))
                return settings;
            settings.Read(JsonValue.Load(path));
        }
        catch (Exception)
        {
            // A file that cannot be read or does not parse leaves the defaults in place. Reporting it
            // would be the first thing the user saw on a first run, and there is nothing to act on.
        }
        return settings;
    }

    /// <summary>
    /// Writes the settings out. Returns false when they could not be written, which includes there
    /// being nowhere to write them.
    /// </summary>
    public bool Save()
    {
        try
        {
            string? path = SettingsPath;
            if (path is null || DataFolder is null)
                return false;
            FileSystem.CreateDirectoryRecursive(DataFolder);
            Write().Save(path, indented: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Puts every value back to what it was before the file was first written.</summary>
    public void Reset()
    {
        var fresh = new AppSettings();
        StartPath = fresh.StartPath;
        ConfirmDestructive = fresh.ConfirmDestructive;
        ShowHiddenFiles = fresh.ShowHiddenFiles;
        SystemNotifications = fresh.SystemNotifications;
        ToastSeconds = fresh.ToastSeconds;
        Sort = fresh.Sort;
        FoldersFirst = fresh.FoldersFirst;
        ListRows = fresh.ListRows;
        ShowSizes = fresh.ShowSizes;
        ArchiveFormat = fresh.ArchiveFormat;
        ArchiveCompress = fresh.ArchiveCompress;
        ExtractPath = fresh.ExtractPath;
        MediaVolume = fresh.MediaVolume;
        MediaLoop = fresh.MediaLoop;
        MediaAutoPlay = fresh.MediaAutoPlay;
        MediaShowOverlay = fresh.MediaShowOverlay;
        TextWordWrap = fresh.TextWordWrap;
        TextScale = fresh.TextScale;
        TextTabWidth = fresh.TextTabWidth;
        TextMaxKilobytes = fresh.TextMaxKilobytes;
        ImageFit = fresh.ImageFit;
        ImageExportFormat = fresh.ImageExportFormat;
        JpegQuality = fresh.JpegQuality;
        PngCompression = fresh.PngCompression;
        ImageSmoothScaling = fresh.ImageSmoothScaling;
        FtpPort = fresh.FtpPort;
        FtpAutoStart = fresh.FtpAutoStart;
        FtpRequireLogin = fresh.FtpRequireLogin;
        FtpUser = fresh.FtpUser;
        FtpPassword = fresh.FtpPassword;
        FtpRoot = fresh.FtpRoot;
        FtpAllowWrite = fresh.FtpAllowWrite;
        PackageSearchPaths = fresh.PackageSearchPaths;
        ConfirmInstall = fresh.ConfirmInstall;
        HomeUrl = fresh.HomeUrl;
    }

    /// <summary>
    /// The folders searched for installable files. The stored line when it names any, and otherwise
    /// every folder the application can reach.
    /// </summary>
    public string[] PackageSearchFolders()
    {
        string[] named = PackageSearchPaths.Split(
            ':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (named.Length > 0)
            return named;

        var reachable = new List<string>();
        foreach (Place place in Places.Reachable)
        {
            if (place.Path != "/")
                reachable.Add(place.Path);
        }
        return reachable.ToArray();
    }

    private void Read(JsonValue root)
    {
        StartPath = root.GetString("startPath", StartPath);
        ConfirmDestructive = root.GetBool("confirmDestructive", ConfirmDestructive);
        ShowHiddenFiles = root.GetBool("showHiddenFiles", ShowHiddenFiles);
        SystemNotifications = root.GetBool("systemNotifications", SystemNotifications);
        // Clamped like every other numeric so a damaged file cannot leave a non-positive duration that
        // the message strip would reject. The range matches the settings slider.
        ToastSeconds = Math.Clamp((float)root.GetNumber("toastSeconds", ToastSeconds), 1f, 12f);

        Sort = ReadEnum(root, "sort", Sort);
        FoldersFirst = root.GetBool("foldersFirst", FoldersFirst);
        ListRows = Clamp(root.GetInt("listRows", ListRows), 6, 24);
        ShowSizes = root.GetBool("showSizes", ShowSizes);

        ArchiveFormat = ReadEnum(root, "archiveFormat", ArchiveFormat);
        ArchiveCompress = root.GetBool("archiveCompress", ArchiveCompress);
        ExtractPath = root.GetString("extractPath", ExtractPath);

        MediaVolume = Clamp(root.GetInt("mediaVolume", MediaVolume), 0, 100);
        MediaLoop = root.GetBool("mediaLoop", MediaLoop);
        MediaAutoPlay = root.GetBool("mediaAutoPlay", MediaAutoPlay);
        MediaShowOverlay = root.GetBool("mediaShowOverlay", MediaShowOverlay);

        TextWordWrap = root.GetBool("textWordWrap", TextWordWrap);
        TextScale = Clamp(root.GetInt("textScale", TextScale), 1, 4);
        TextTabWidth = Clamp(root.GetInt("textTabWidth", TextTabWidth), 1, 8);
        TextMaxKilobytes = Clamp(root.GetInt("textMaxKilobytes", TextMaxKilobytes), 16, 65536);

        ImageFit = ReadEnum(root, "imageFit", ImageFit);
        ImageExportFormat = ReadEnum(root, "imageExportFormat", ImageExportFormat);
        JpegQuality = Clamp(root.GetInt("jpegQuality", JpegQuality), 1, 100);
        PngCompression = Clamp(root.GetInt("pngCompression", PngCompression), 0, 9);
        ImageSmoothScaling = root.GetBool("imageSmoothScaling", ImageSmoothScaling);

        FtpPort = Clamp(root.GetInt("ftpPort", FtpPort), 1, 65535);
        FtpAutoStart = root.GetBool("ftpAutoStart", FtpAutoStart);
        FtpRequireLogin = root.GetBool("ftpRequireLogin", FtpRequireLogin);
        FtpUser = root.GetString("ftpUser", FtpUser);
        FtpPassword = root.GetString("ftpPassword", FtpPassword);
        FtpRoot = root.GetString("ftpRoot", FtpRoot);
        FtpAllowWrite = root.GetBool("ftpAllowWrite", FtpAllowWrite);

        PackageSearchPaths = root.GetString("packageSearchPaths", PackageSearchPaths);
        ConfirmInstall = root.GetBool("confirmInstall", ConfirmInstall);

        HomeUrl = root.GetString("homeUrl", HomeUrl);
    }

    private JsonValue Write()
    {
        JsonValue root = JsonValue.NewObject();
        root["startPath"] = JsonValue.Of(StartPath);
        root["confirmDestructive"] = JsonValue.Of(ConfirmDestructive);
        root["showHiddenFiles"] = JsonValue.Of(ShowHiddenFiles);
        root["systemNotifications"] = JsonValue.Of(SystemNotifications);
        root["toastSeconds"] = JsonValue.Of(ToastSeconds);

        root["sort"] = JsonValue.Of(Sort.ToString());
        root["foldersFirst"] = JsonValue.Of(FoldersFirst);
        root["listRows"] = JsonValue.Of(ListRows);
        root["showSizes"] = JsonValue.Of(ShowSizes);

        root["archiveFormat"] = JsonValue.Of(ArchiveFormat.ToString());
        root["archiveCompress"] = JsonValue.Of(ArchiveCompress);
        root["extractPath"] = JsonValue.Of(ExtractPath);

        root["mediaVolume"] = JsonValue.Of(MediaVolume);
        root["mediaLoop"] = JsonValue.Of(MediaLoop);
        root["mediaAutoPlay"] = JsonValue.Of(MediaAutoPlay);
        root["mediaShowOverlay"] = JsonValue.Of(MediaShowOverlay);

        root["textWordWrap"] = JsonValue.Of(TextWordWrap);
        root["textScale"] = JsonValue.Of(TextScale);
        root["textTabWidth"] = JsonValue.Of(TextTabWidth);
        root["textMaxKilobytes"] = JsonValue.Of(TextMaxKilobytes);

        root["imageFit"] = JsonValue.Of(ImageFit.ToString());
        root["imageExportFormat"] = JsonValue.Of(ImageExportFormat.ToString());
        root["jpegQuality"] = JsonValue.Of(JpegQuality);
        root["pngCompression"] = JsonValue.Of(PngCompression);
        root["imageSmoothScaling"] = JsonValue.Of(ImageSmoothScaling);

        root["ftpPort"] = JsonValue.Of(FtpPort);
        root["ftpAutoStart"] = JsonValue.Of(FtpAutoStart);
        root["ftpRequireLogin"] = JsonValue.Of(FtpRequireLogin);
        root["ftpUser"] = JsonValue.Of(FtpUser);
        root["ftpPassword"] = JsonValue.Of(FtpPassword);
        root["ftpRoot"] = JsonValue.Of(FtpRoot);
        root["ftpAllowWrite"] = JsonValue.Of(FtpAllowWrite);

        root["packageSearchPaths"] = JsonValue.Of(PackageSearchPaths);
        root["confirmInstall"] = JsonValue.Of(ConfirmInstall);

        root["homeUrl"] = JsonValue.Of(HomeUrl);
        return root;
    }

    private static int Clamp(int value, int low, int high) => value < low ? low : value > high ? high : value;

    private static TEnum ReadEnum<TEnum>(JsonValue root, string key, TEnum fallback) where TEnum : struct, Enum
        => Enum.TryParse(root.GetString(key, fallback.ToString()), ignoreCase: true, out TEnum parsed)
            ? parsed
            : fallback;
}

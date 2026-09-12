// Prospero Explorer
// Copyright (C) 2026 SvenGDK

using SharpProspero.Storage;
using System;

namespace ProsperoExplorer.Shell;

/// <summary>What a file holds, as far as the name says.</summary>
internal enum FileKind
{
    /// <summary>A folder.</summary>
    Folder,

    /// <summary>Readable text.</summary>
    Text,

    /// <summary>A picture.</summary>
    Image,

    /// <summary>Sound.</summary>
    Audio,

    /// <summary>Moving pictures.</summary>
    Video,

    /// <summary>A container of other files.</summary>
    Archive,

    /// <summary>Something the system installs.</summary>
    Package,

    /// <summary>Anything else.</summary>
    Binary,
}

/// <summary>
/// What a name says a file holds, and the mark shown beside it. The name is all there is to go on
/// before the file is opened, so this decides which viewer a selection reaches.
/// </summary>
internal static class FileKinds
{
    private static readonly string[] TextExtensions =
    [
        ".txt", ".log", ".ini", ".cfg", ".conf", ".json", ".xml", ".csv", ".tsv", ".md", ".yml",
        ".yaml", ".html", ".htm", ".css", ".js", ".cs", ".c", ".h", ".cpp", ".hpp", ".py", ".sh",
        ".ps1", ".bat", ".lua", ".sql", ".toml", ".properties", ".gitignore", ".srt", ".vtt",
    ];

    private static readonly string[] ImageExtensions =
    [
        ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".gif",
    ];

    private static readonly string[] AudioExtensions =
    [
        ".wav", ".mp3", ".m4a", ".aac", ".at9", ".vag", ".flac", ".ogg", ".opus",
    ];

    private static readonly string[] VideoExtensions =
    [
        ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".webm", ".m2ts", ".ts",
    ];

    private static readonly string[] ArchiveExtensions =
    [
        ".zip", ".tar", ".gz", ".tgz", ".zlib",
    ];

    private static readonly string[] PackageExtensions =
    [
        ".pkg",
    ];

    /// <summary>What <paramref name="path"/> holds, going by the name alone.</summary>
    public static FileKind Classify(string path, bool isDirectory)
    {
        if (isDirectory)
            return FileKind.Folder;

        string extension = PathUtil.GetExtension(path).ToLowerInvariant();
        if (extension.Length == 0)
            return FileKind.Binary;

        if (Contains(PackageExtensions, extension))
            return FileKind.Package;
        if (Contains(ImageExtensions, extension))
            return FileKind.Image;
        if (Contains(AudioExtensions, extension))
            return FileKind.Audio;
        if (Contains(VideoExtensions, extension))
            return FileKind.Video;
        if (Contains(ArchiveExtensions, extension))
            return FileKind.Archive;
        if (Contains(TextExtensions, extension))
            return FileKind.Text;
        return FileKind.Binary;
    }

    /// <summary>A short mark drawn before a name so the kind reads at a glance.</summary>
    public static string Marker(FileKind kind) => kind switch
    {
        FileKind.Folder => "[DIR]",
        FileKind.Text => "[TXT]",
        FileKind.Image => "[IMG]",
        FileKind.Audio => "[SND]",
        FileKind.Video => "[VID]",
        FileKind.Archive => "[ZIP]",
        FileKind.Package => "[PKG]",
        _ => "[BIN]",
    };

    /// <summary>What the kind is called, for a properties list or a message.</summary>
    public static string Describe(FileKind kind) => kind switch
    {
        FileKind.Folder => "Folder",
        FileKind.Text => "Text",
        FileKind.Image => "Picture",
        FileKind.Audio => "Audio",
        FileKind.Video => "Video",
        FileKind.Archive => "Archive",
        FileKind.Package => "Package",
        _ => "Binary",
    };

    /// <summary>
    /// Whether the archive tools read this one. A compressed stream that is not a container - a lone
    /// gzip or zlib file - is unpacked rather than browsed, so it is told apart here.
    /// </summary>
    public static bool IsBrowsableArchive(string path)
    {
        string extension = PathUtil.GetExtension(path).ToLowerInvariant();
        return extension is ".zip" or ".tar" or ".tgz";
    }

    private static bool Contains(string[] set, string extension)
    {
        foreach (string candidate in set)
        {
            if (string.Equals(candidate, extension, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}

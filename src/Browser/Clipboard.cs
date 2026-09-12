// Prospero Explorer
// Copyright (C) 2026 SvenGDK

using SharpProspero.Storage;
using System;
using System.Collections.Generic;

namespace ProsperoExplorer.Browser;

/// <summary>
/// What the browser is holding for a paste, and whether pasting moves it or leaves the original where
/// it is. One holder serves every folder, so something taken in one place pastes in another; a paste
/// that moves clears the holder afterwards because the paths it names no longer exist.
/// </summary>
internal static class Clipboard
{
    private static readonly List<string> Held = [];

    /// <summary>The full paths waiting to be pasted, in the order they were taken.</summary>
    public static IReadOnlyList<string> Paths => Held;

    /// <summary>How many paths are waiting.</summary>
    public static int Count => Held.Count;

    /// <summary>Whether anything is waiting to be pasted.</summary>
    public static bool HasContent => Held.Count > 0;

    /// <summary>Whether a paste moves what is held rather than copying it.</summary>
    public static bool IsCut { get; private set; }

    /// <summary>Holds <paramref name="paths"/> for a paste that leaves the originals in place.</summary>
    public static void Copy(IEnumerable<string> paths) => Take(paths, cut: false);

    /// <summary>Holds <paramref name="paths"/> for a paste that removes the originals.</summary>
    public static void Cut(IEnumerable<string> paths) => Take(paths, cut: true);

    /// <summary>Drops whatever is held.</summary>
    public static void Clear()
    {
        Held.Clear();
        IsCut = false;
    }

    /// <summary>What is held, in a form worth putting on the status line.</summary>
    public static string Describe()
    {
        if (Held.Count == 0)
            return "clipboard empty";
        string action = IsCut ? "to move" : "to copy";
        return Held.Count == 1
            ? $"{PathUtil.GetFileName(Held[0])} {action}"
            : $"{Held.Count} items {action}";
    }

    private static void Take(IEnumerable<string> paths, bool cut)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Held.Clear();
        foreach (string path in paths)
        {
            if (!string.IsNullOrEmpty(path))
                Held.Add(path);
        }
        IsCut = cut && Held.Count > 0;
    }
}

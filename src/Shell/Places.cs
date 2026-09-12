// Prospero Explorer
// Copyright (C) 2026 SvenGDK

using SharpProspero.Interop;
using SharpProspero.Storage;
using System;
using System.Collections.Generic;

namespace ProsperoExplorer.Shell;

/// <summary>One folder the application can actually reach, and what it is for.</summary>
/// <param name="Path">Where it is.</param>
/// <param name="Name">What to call it in a listing.</param>
/// <param name="Description">What it holds.</param>
/// <param name="Writable">Whether the application may write there.</param>
internal readonly record struct Place(string Path, string Name, string Description, bool Writable);

/// <summary>What came back when one candidate folder was opened.</summary>
/// <param name="Place">The folder that was tried.</param>
/// <param name="Readable">Whether it listed.</param>
/// <param name="ErrorCode">Zero when it listed, otherwise the code the failing call returned.</param>
internal readonly record struct PlaceProbe(Place Place, bool Readable, int ErrorCode)
{
    /// <summary>Why the folder could not be read, or an empty string when it could.</summary>
    public string Reason => Readable ? string.Empty : SceResult.Describe(ErrorCode);
}

/// <summary>
/// The folders this application can reach, found by trying them rather than assumed.
/// </summary>
/// <remarks>
/// A module is given its own root before it starts, so a leading slash means that root and not the
/// whole file system. Everything above it is out of reach by name: a path the module cannot see does
/// not come back refused for want of permission, it comes back as no such path, the same answer a
/// misspelling gets. Which folders sit under that root depends on how the module was started and what
/// was mounted for it, so a folder that is there for one module is absent for the next.
///
/// So every candidate is opened once at start-up and the ones that answer are kept, along with the
/// code each of the others refused with. Nothing here is fatal - a module that can reach only its own
/// package still gets a listing of that - but the codes are what make the next run tell us something.
/// </remarks>
internal static class Places
{
    // Every folder worth trying, most useful first. Anything absent is dropped rather than reported:
    // the list is a set of candidates, not a set of expectations.
    private static readonly Place[] Candidates =
    [
        new("/app0", "Application", "This application's own files, which it may only read", false),
        new("/download0", "Downloaded data", "The area this application downloads into", true),
        new("/savedata0", "Save data", "Mounted save data", true),
        new("/data", "Data", "The writable area", true),
        new("/user", "User", "Per-user files", true),
        new("/mnt/usb0", "USB 1", "A removable device", true),
        new("/mnt/usb1", "USB 2", "A removable device", true),
        new("/mnt/usb2", "USB 3", "A removable device", true),
        new("/mnt/usb3", "USB 4", "A removable device", true),
        new("/mnt/usb4", "USB 5", "A removable device", true),
        new("/mnt/usb5", "USB 6", "A removable device", true),
        new("/mnt/usb6", "USB 7", "A removable device", true),
        new("/mnt/usb7", "USB 8", "A removable device", true),
        new("/hostapp", "Host", "A folder shared from a connected machine", false),
        new("/system", "System", "System files", false),
        new("/system_data", "System data", "System data", false),
        new("/preinst", "Preinstalled", "Preinstalled content", false),
        // The root this module was given, which is not the console's. Listing it is the one candidate
        // that names the others rather than guessing at them, so it is always worth trying.
        new("/", "Root", "The root this module was given, and everything under it", false),
    ];

    private static List<PlaceProbe>? _probed;

    /// <summary>
    /// Every candidate with what came back when it was opened, in the order above. Worked out once.
    /// A candidate that refused carries the code it refused with, which is the only thing that turns the
    /// next run on a console into evidence rather than another guess.
    /// </summary>
    public static IReadOnlyList<PlaceProbe> Probed => _probed ??= Probe();

    /// <summary>
    /// The folders that answered when they were opened, in the order above. Worked out once.
    /// </summary>
    public static IReadOnlyList<Place> Reachable
    {
        get
        {
            var found = new List<Place>();
            foreach (PlaceProbe probe in Probed)
            {
                if (probe.Readable)
                    found.Add(probe.Place);
            }
            return found;
        }
    }

    /// <summary>
    /// The folders that refused, with the reason each gave.
    /// </summary>
    public static IReadOnlyList<PlaceProbe> Unreachable
    {
        get
        {
            var refused = new List<PlaceProbe>();
            foreach (PlaceProbe probe in Probed)
            {
                if (!probe.Readable)
                    refused.Add(probe);
            }
            return refused;
        }
    }

    /// <summary>
    /// Why <paramref name="path"/> could not be read the last time it was tried, or an empty string when
    /// it could be read or was never a candidate.
    /// </summary>
    public static string ReasonFor(string path)
    {
        foreach (PlaceProbe probe in Probed)
        {
            if (probe.Place.Path == path)
                return probe.Reason;
        }
        return string.Empty;
    }

    /// <summary>Looks again, for a device plugged in since the last time.</summary>
    public static void Refresh() => _probed = Probe();

    /// <summary>
    /// The folder the browser opens at: <paramref name="preferred"/> when it can be read, and
    /// otherwise the first that can. Null when nothing can be read at all.
    /// </summary>
    public static string? StartingPoint(string preferred)
    {
        if (!string.IsNullOrEmpty(preferred) && CanRead(preferred))
            return preferred;
        IReadOnlyList<Place> reachable = Reachable;
        return reachable.Count > 0 ? reachable[0].Path : null;
    }

    /// <summary>Whether <paramref name="path"/> can be listed.</summary>
    public static bool CanRead(string path) => TryRead(path, out _);

    /// <summary>
    /// Whether <paramref name="path"/> can be listed, and the code it refused with when it cannot.
    /// </summary>
    /// <param name="path">The folder to try.</param>
    /// <param name="errorCode">Zero when the folder listed, otherwise the code the failing call returned.</param>
    public static bool TryRead(string path, out int errorCode)
    {
        try
        {
            return FileSystem.TryEnumerateDirectory(path, out _, out errorCode);
        }
        catch (Exception)
        {
            // Only an argument the SDK refuses outright reaches here; a device failure comes back as a
            // code. Report it as a failure with no code so the caller still gets an answer.
            errorCode = -1;
            return false;
        }
    }

    /// <summary>
    /// Where the application keeps its own settings and log, found by writing rather than assumed.
    /// Null when nowhere will take them, in which case the application runs on its defaults and says
    /// so once.
    /// </summary>
    public static string? DataFolder(string folderName)
    {
        foreach (Place place in Reachable)
        {
            if (!place.Writable)
                continue;
            string candidate = PathUtil.Combine(place.Path, folderName);
            try
            {
                FileSystem.CreateDirectoryRecursive(candidate);
                // Creating it can succeed on a read-only mount that swallows the request, so the
                // folder is only accepted once a file has actually been written into it.
                string probe = PathUtil.Combine(candidate, ".writable");
                FileSystem.WriteAllText(probe, "1");
                FileSystem.DeleteFile(probe);
                return candidate;
            }
            catch (Exception)
            {
                // Not this one. Try the next.
            }
        }
        return null;
    }

    private static List<PlaceProbe> Probe()
    {
        var results = new List<PlaceProbe>(Candidates.Length);
        foreach (Place place in Candidates)
        {
            bool readable = TryRead(place.Path, out int errorCode);
            results.Add(new PlaceProbe(place, readable, readable ? 0 : errorCode));
        }
        return results;
    }
}

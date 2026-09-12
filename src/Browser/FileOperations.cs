// Prospero Explorer
// Copyright (C) 2026 SvenGDK

using ProsperoExplorer.Archive;
using ProsperoExplorer.Shell;
using SharpProspero.Interop;
using SharpProspero.Interop.Kernel;
using SharpProspero.Storage;
using System;
using System.Collections.Generic;
using System.Text;

namespace ProsperoExplorer.Browser;

/// <summary>
/// What of a set of paths a folder already holds: how many, the name of the first of them, and
/// whether any of them is already there as a folder. A folder that is already there is merged into
/// rather than replaced, so a question about the clash reads differently for one.
/// </summary>
internal readonly record struct ClashReport(int Count, string? FirstName, bool HasDirectory);

/// <summary>
/// Work that is done a slice at a time. The caller keeps calling <see cref="Step"/> from the frame loop
/// until it returns true, so the application carries on drawing while a copy of several gigabytes runs.
/// </summary>
/// <remarks>
/// A task never lets a failure escape <see cref="Step"/>. One item that cannot be handled is recorded in
/// <see cref="Failures"/> and the rest of the work continues, because stopping a batch of forty files on
/// the eighth is worse for the user than finishing the other thirty-two and saying which one failed.
/// </remarks>
internal interface IFileTask : IDisposable
{
    /// <summary>What the work is, for the caption above the progress bar.</summary>
    string Caption { get; }

    /// <summary>The item being handled right now.</summary>
    string CurrentItem { get; }

    /// <summary>How far along the work is, from 0 to 1.</summary>
    float Progress { get; }

    /// <summary>Whether the work has run to its end.</summary>
    bool IsFinished { get; }

    /// <summary>How many items could not be handled.</summary>
    int FailureCount { get; }

    /// <summary>The first failures, each naming the item and the reason. Capped, so a bad mount cannot fill memory.</summary>
    IReadOnlyList<string> Failures { get; }

    /// <summary>Does a bounded amount of work. Returns true once there is nothing left to do.</summary>
    bool Step();
}

/// <summary>What a <see cref="FileTask"/> is doing to the paths it was given.</summary>
internal enum FileTaskKind
{
    /// <summary>Duplicate into another folder, leaving the originals.</summary>
    Copy,

    /// <summary>Put into another folder and remove the originals.</summary>
    Move,

    /// <summary>Remove, including everything inside a folder.</summary>
    Delete,
}

/// <summary>
/// Copies, moves or removes a set of paths, folders included, a slice at a time.
/// </summary>
/// <remarks>
/// The work runs in two parts. First the tree under each path is walked to a plan, a bounded number of
/// entries per call, which is also what gives the total byte count the progress fraction needs. Then the
/// plan is carried out, one buffered chunk of one file per call. Neither part holds a whole file or a
/// whole listing longer than it has to.
/// </remarks>
internal sealed class FileTask : IFileTask
{
    // A copy reads and writes through a fixed buffer rather than loading a file whole: a module's heap
    // is far smaller than the files a user keeps, so a whole-file read fails on anything large. The
    // chunk is the trade between how fast a copy runs and how much of a frame it takes.
    private const int ChunkBytes = 2 * 1024 * 1024;
    private const int EntriesPerStep = 256;
    private const int QuickJobsPerStep = 32;
    private const int MaxRecordedFailures = 32;

    // Two ceilings on the walk. The plan is held in memory, so the number of entries it can hold is
    // bounded; and a folder that leads back to itself would otherwise be walked without end, which the
    // depth limit cuts short long before any real tree reaches it.
    private const int MaxDepth = 64;
    private const int MaxPlannedEntries = 65536;

    private readonly FileTaskKind _kind;
    private readonly List<string> _roots = [];
    private readonly string _destinationFolder;
    private readonly List<Job> _jobs = [];
    private readonly List<Node> _walk = [];
    private readonly List<Node> _pending = [];
    private readonly List<string> _failures = [];
    private readonly HashSet<int> _failedGroups = [];
    private readonly Dictionary<string, FileEntryType> _siblingTypes = new(StringComparer.Ordinal);

    private string _siblingFolder = string.Empty;
    private int _planned;
    private bool _ceilingReached;
    private bool _scanning = true;
    private bool _rootActive;
    private bool _destinationReady;
    private int _rootIndex;

    private int _jobIndex;
    private long _bytesTotal;
    private long _bytesDone;
    private long _chunkDone;
    private long _chunkLength;
    private byte[]? _buffer;
    private int _sourceFile = -1;
    private int _destinationFile = -1;
    private string _current = "Reading the folder";
    private bool _disposed;

    private FileTask(FileTaskKind kind, IReadOnlyList<string> paths, string destinationFolder)
    {
        _kind = kind;
        _destinationFolder = destinationFolder;
        foreach (string path in paths)
        {
            string trimmed = Normalize(path);
            if (trimmed.Length > 0)
                _roots.Add(trimmed);
        }

        string what = _roots.Count == 1 ? PathUtil.GetFileName(_roots[0]) : $"{_roots.Count} items";
        Caption = kind switch
        {
            FileTaskKind.Copy => $"Copying {what}",
            FileTaskKind.Move => $"Moving {what}",
            _ => $"Deleting {what}",
        };
    }

    /// <summary>Copies <paramref name="sources"/> into <paramref name="destinationFolder"/>.</summary>
    public static FileTask Copy(IReadOnlyList<string> sources, string destinationFolder)
        => new(FileTaskKind.Copy, sources, Normalize(destinationFolder));

    /// <summary>Moves <paramref name="sources"/> into <paramref name="destinationFolder"/>.</summary>
    public static FileTask Move(IReadOnlyList<string> sources, string destinationFolder)
        => new(FileTaskKind.Move, sources, Normalize(destinationFolder));

    /// <summary>Removes <paramref name="targets"/>, walking into any folder among them.</summary>
    public static FileTask Delete(IReadOnlyList<string> targets)
        => new(FileTaskKind.Delete, targets, string.Empty);

    /// <inheritdoc />
    public string Caption { get; }

    /// <inheritdoc />
    public string CurrentItem => _current;

    /// <inheritdoc />
    public bool IsFinished { get; private set; }

    /// <inheritdoc />
    public int FailureCount { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<string> Failures => _failures;

    /// <inheritdoc />
    public float Progress
    {
        get
        {
            if (_scanning)
                return 0f;

            // A removal is quick whatever the file weighs, so counting the entries left tracks it far
            // better than counting the bytes they hold.
            if (_kind != FileTaskKind.Delete && _bytesTotal > 0)
                return Clamp01((float)((_bytesDone + _chunkDone) / (double)_bytesTotal));
            return _jobs.Count == 0 ? 1f : Clamp01(_jobIndex / (float)_jobs.Count);
        }
    }

    /// <inheritdoc />
    public bool Step()
    {
        if (IsFinished || _disposed)
            return true;

        if (_scanning)
        {
            if (Scan())
                _scanning = false;
            return false;
        }

        if (Run())
            IsFinished = true;
        return IsFinished;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CloseChunk();
        _buffer = null;
    }

    // Walking the tree. One call expands a bounded number of entries, so a folder of ten thousand files
    // costs many cheap calls instead of one that misses the frame.
    private bool Scan()
    {
        int budget = EntriesPerStep;
        while (budget > 0)
        {
            if (_pending.Count == 0)
            {
                if (_rootActive)
                {
                    FinishRoot();
                    _rootActive = false;
                    _rootIndex++;
                }

                if (_rootIndex >= _roots.Count)
                    return true;

                if (BeginRoot(_roots[_rootIndex]))
                    _rootActive = true;
                else
                    _rootIndex++;
                budget--;
                continue;
            }

            Node node = _pending[^1];
            _pending.RemoveAt(_pending.Count - 1);
            _walk.Add(node);
            budget--;

            if (node.Kind != EntryKind.Directory)
                continue;

            _current = PathUtil.GetFileName(node.Source);
            budget -= Expand(node);
        }

        return false;
    }

    private int Expand(Node node)
    {
        // A link that names one of its own ancestors turns the tree into a ring. Nothing in a listing
        // says which entry closes the ring, so the walk is cut off by how deep it has gone instead.
        if (node.Depth >= MaxDepth)
        {
            Fail(node.Source, "the folders below this one are nested deeper than a walk goes.", node.Group);
            return 1;
        }

        IReadOnlyList<DirectoryEntry> entries;
        try
        {
            entries = FileSystem.EnumerateDirectory(node.Source);
        }
        catch (Exception error)
        {
            Fail(node.Source, error, node.Group);
            return 1;
        }

        for (int i = entries.Count - 1; i >= 0; i--)
        {
            string name = entries[i].Name;
            if (name is "." or "..")
                continue;

            if (!HasRoom(node.Group))
                break;

            string source = PathUtil.Combine(node.Source, name);
            string destination = node.Destination.Length == 0 ? string.Empty : PathUtil.Combine(node.Destination, name);
            EntryKind kind = ChildKind(entries[i], source);

            // Asking a pipe or a device how large it is means opening it, and opening a pipe waits for
            // a writer that may never come. Only a regular file is measured.
            long size = kind == EntryKind.File ? SizeOf(source) : 0;
            _pending.Add(new Node(source, destination, kind, size, node.Group, node.Depth + 1));
            _planned++;
        }

        return entries.Count;
    }

    // Records one entry against the ceiling on the plan. Once it is reached the walk stops adding, and
    // a move stops with it: the group is marked failed, so nothing it copied is removed afterwards.
    private bool HasRoom(int group)
    {
        if (_planned < MaxPlannedEntries)
            return true;
        if (!_ceilingReached)
        {
            _ceilingReached = true;
            Fail(
                _roots.Count > 0 ? _roots[0] : "/",
                $"the selection holds more than {MaxPlannedEntries} entries, which is more than one action can plan.",
                group);
        }
        return false;
    }

    // Returns true when the path still has to be walked. A move that the file system can settle with a
    // rename never gets that far: renaming within one mount is instant, so it is tried first and only a
    // refusal (a different mount, most often) falls back to copying and removing.
    private bool BeginRoot(string source)
    {
        int group = _rootIndex;
        try
        {
            EntryKind kind = RootKind(source);
            string destination = string.Empty;

            if (_kind != FileTaskKind.Delete)
            {
                string name = PathUtil.GetFileName(source);
                if (name.Length == 0)
                {
                    Fail(source, new InvalidOperationException("The path has no name."), group);
                    return false;
                }

                destination = PathUtil.Combine(_destinationFolder, name);
                if (IsInside(source, destination))
                {
                    Fail(source, new InvalidOperationException("The destination is inside the folder being copied."), group);
                    return false;
                }

                if (!_destinationReady)
                {
                    FileSystem.CreateDirectoryRecursive(_destinationFolder);
                    _destinationReady = true;
                }

                if (_kind == FileTaskKind.Move)
                {
                    try
                    {
                        FileSystem.Move(source, destination);
                        return false;
                    }
                    catch (ProsperoException)
                    {
                        // Not one mount. Fall through and carry the bytes across by hand.
                    }
                }
            }

            if (!HasRoom(group))
                return false;

            _pending.Add(new Node(source, destination, kind, kind == EntryKind.File ? SizeOf(source) : 0, group, 0));
            _planned++;
            return true;
        }
        catch (Exception error)
        {
            Fail(source, error, group);
            return false;
        }
    }

    private void FinishRoot()
    {
        if (_kind != FileTaskKind.Delete)
        {
            // Parents come before their contents, which is the order the walk produced them in.
            foreach (Node node in _walk)
            {
                switch (node.Kind)
                {
                    case EntryKind.Directory:
                        _jobs.Add(new Job(JobAction.CreateDirectory, node.Source, node.Destination, 0, node.Group, false));
                        break;
                    case EntryKind.File:
                        _jobs.Add(new Job(JobAction.CopyFile, node.Source, node.Destination, node.Size, node.Group, false));
                        _bytesTotal += node.Size;
                        break;
                    default:
                        // A link, a pipe, a socket or a device is a name the file system answers itself
                        // rather than a run of bytes. Reading one either returns nothing useful or never
                        // returns at all, so it is passed over and counted, and a move that met one
                        // leaves its originals alone.
                        Fail(node.Source, "this kind of entry is not copied.", node.Group);
                        break;
                }
            }
        }

        if (_kind != FileTaskKind.Copy)
        {
            // Removal runs the walk backwards, so a folder is only reached once everything inside it is
            // gone. A move only removes an original when its copy came through, which the group check
            // settles at the time the job runs. Removing a link removes the link and not what it names,
            // which is why everything that is not a folder takes the same job.
            bool guarded = _kind == FileTaskKind.Move;
            for (int i = _walk.Count - 1; i >= 0; i--)
            {
                Node node = _walk[i];
                JobAction action = node.Kind == EntryKind.Directory ? JobAction.DeleteDirectory : JobAction.DeleteFile;
                _jobs.Add(new Job(action, node.Source, string.Empty, 0, node.Group, guarded));
            }
        }

        _walk.Clear();
    }

    // Carrying the plan out. A file copy holds its descriptors between calls and moves one chunk per
    // call; everything else is cheap enough to run several at a time.
    private bool Run()
    {
        if (_sourceFile >= 0)
        {
            AdvanceChunk();
            return false;
        }

        int budget = QuickJobsPerStep;
        while (budget-- > 0)
        {
            if (_jobIndex >= _jobs.Count)
                return true;

            Job job = _jobs[_jobIndex++];
            _current = PathUtil.GetFileName(job.Source);

            if (job.Action == JobAction.CopyFile)
            {
                if (BeginChunkedCopy(job))
                {
                    AdvanceChunk();
                    return false;
                }
                continue;
            }

            RunQuickJob(job);
        }

        return _jobIndex >= _jobs.Count;
    }

    private void RunQuickJob(Job job)
    {
        if (job.NeedsGroupSuccess && _failedGroups.Contains(job.Group))
            return;

        try
        {
            switch (job.Action)
            {
                case JobAction.CreateDirectory:
                    if (!FileSystem.Exists(job.Destination))
                        FileSystem.CreateDirectoryRecursive(job.Destination);
                    break;
                case JobAction.DeleteFile:
                    FileSystem.DeleteFile(job.Source);
                    break;
                case JobAction.DeleteDirectory:
                    FileSystem.DeleteDirectory(job.Source);
                    break;
                default:
                    break;
            }
        }
        catch (Exception error)
        {
            Fail(job.Source, error, job.Group);
        }
    }

    private bool BeginChunkedCopy(Job job)
    {
        try
        {
            _buffer ??= new byte[ChunkBytes];
            _sourceFile = RawFile.OpenRead(job.Source);
            try
            {
                _destinationFile = RawFile.OpenWrite(job.Destination);
            }
            catch (Exception)
            {
                RawFile.Close(_sourceFile);
                _sourceFile = -1;
                throw;
            }

            _chunkDone = 0;
            _chunkLength = job.Size;
            return true;
        }
        catch (Exception error)
        {
            Fail(job.Source, error, job.Group);
            return false;
        }
    }

    private void AdvanceChunk()
    {
        Job job = _jobs[_jobIndex - 1];
        try
        {
            // The copy ends where the file ends, not where the plan said it would. The size was taken
            // when the folder was walked and the file may have grown since; stopping at the older figure
            // would leave the tail behind, and a move would then remove the only copy that had it.
            int read = RawFile.Read(_sourceFile, _buffer!);
            if (read == 0)
            {
                CompleteChunkedCopy(job);
                return;
            }

            RawFile.Write(_destinationFile, _buffer!, read);
            _chunkDone += read;
        }
        catch (Exception error)
        {
            Fail(job.Source, error, job.Group);
            CloseChunk();
            RemovePartial(job.Destination);
            _bytesDone += _chunkLength;
            _chunkDone = 0;
            _chunkLength = 0;
        }
    }

    // A copy is only finished once the write side has been closed without complaint. A file system that
    // holds writes back reports a refusal - a full volume, most often - when the last of them is pushed
    // out at close time, and that report is the only sign that a copy came up short. A move is checked
    // once more against the length of what it wrote, because it is about to remove the only other copy.
    private void CompleteChunkedCopy(Job job)
    {
        if (_sourceFile >= 0)
        {
            RawFile.Close(_sourceFile);
            _sourceFile = -1;
        }

        int destination = _destinationFile;
        _destinationFile = -1;
        try
        {
            if (destination >= 0)
                RawFile.CloseWritten(destination);
            if (_kind == FileTaskKind.Move)
                ConfirmCopied(job);
        }
        catch (Exception error)
        {
            // The close reported a short write, or the copy came up shorter than the original. Either
            // way the destination holds an incomplete file, so it is removed rather than left to read
            // as a whole one - a move keeps its original, so nothing is lost by dropping the remnant.
            Fail(job.Source, error, job.Group);
            RemovePartial(job.Destination);
        }

        _bytesDone += Math.Max(_chunkDone, _chunkLength);
        _chunkDone = 0;
        _chunkLength = 0;
    }

    private void ConfirmCopied(Job job)
    {
        long written = FileSystem.GetFileSize(job.Destination);
        if (written != _chunkDone)
            throw new InvalidOperationException($"The copy holds {written} bytes of {_chunkDone}.");
    }

    private void CloseChunk()
    {
        if (_sourceFile >= 0)
        {
            RawFile.Close(_sourceFile);
            _sourceFile = -1;
        }
        if (_destinationFile >= 0)
        {
            RawFile.Close(_destinationFile);
            _destinationFile = -1;
        }
    }

    // A copy that failed part way has already created its destination and written some of it. Left in
    // place a partial file reads as a whole one, so it is removed before the failure is recorded. The
    // descriptors are closed by the caller first.
    private static void RemovePartial(string destination)
    {
        if (destination.Length == 0)
            return;
        try
        {
            if (FileSystem.Exists(destination))
                FileSystem.DeleteFile(destination);
        }
        catch (ProsperoException)
        {
            // The remnant could not be removed. There is nothing more to do about it here, and the
            // copy's own failure is the one worth putting to the user.
        }
    }

    private void Fail(string path, Exception error, int group) => Fail(path, error.Message, group);

    private void Fail(string path, string reason, int group)
    {
        _failedGroups.Add(group);
        FailureCount++;
        if (_failures.Count < MaxRecordedFailures)
            _failures.Add($"{PathUtil.GetFileName(path)}: {reason}");
    }

    private static long SizeOf(string path)
    {
        try
        {
            return FileSystem.GetFileSize(path);
        }
        catch (ProsperoException)
        {
            return 0;
        }
    }

    // A directory record names what the entry itself is, a link included, so it is taken at its word.
    // Only a record that reports nothing costs the extra call, and for that one there is no way left to
    // tell a link from what it names.
    private static EntryKind ChildKind(DirectoryEntry entry, string path)
        => Classify(entry.Type == FileEntryType.Unknown ? FileSystem.GetEntryType(path) : entry.Type);

    // A status call follows a link through to what it names, so it answers "folder" for a link to one.
    // Walking into that would plan work on files outside what the user chose, and a removal would take
    // the contents of the target with it. The listing of the folder above names the entry itself, so it
    // settles the question - and it is only read for a path that already looks like a folder, which is
    // the only case where being wrong costs anything.
    private EntryKind RootKind(string path)
    {
        EntryKind kind = Classify(FileSystem.GetEntryType(path));
        if (kind != EntryKind.Directory)
            return kind;
        return SiblingType(path) == FileEntryType.SymbolicLink ? EntryKind.Other : EntryKind.Directory;
    }

    // Everything one action was given comes out of the same folder, so the listing is read once and
    // kept rather than read again for every path in the selection.
    private FileEntryType SiblingType(string path)
    {
        string folder = PathUtil.GetDirectoryName(path);
        if (folder.Length == 0)
            folder = "/";

        if (!string.Equals(folder, _siblingFolder, StringComparison.Ordinal))
        {
            _siblingFolder = folder;
            _siblingTypes.Clear();
            try
            {
                foreach (DirectoryEntry entry in FileSystem.EnumerateDirectory(folder))
                    _siblingTypes[entry.Name] = entry.Type;
            }
            catch (ProsperoException)
            {
                // The folder above cannot be listed, so there is nothing more to learn about the entry
                // and it stands as the status call reported it.
            }
        }

        return _siblingTypes.TryGetValue(PathUtil.GetFileName(path), out FileEntryType type)
            ? type
            : FileEntryType.Unknown;
    }

    private static EntryKind Classify(FileEntryType type) => type switch
    {
        FileEntryType.Directory => EntryKind.Directory,
        FileEntryType.File => EntryKind.File,
        _ => EntryKind.Other,
    };

    private static bool IsInside(string root, string candidate)
    {
        string a = Normalize(root);
        string b = Normalize(candidate);
        return b == a || b.StartsWith(a + "/", StringComparison.Ordinal);
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;
        string trimmed = path.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    private static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;

    private enum JobAction
    {
        CreateDirectory,
        CopyFile,
        DeleteFile,
        DeleteDirectory,
    }

    /// <summary>What one entry of the walk is, as far as the plan cares.</summary>
    private enum EntryKind
    {
        /// <summary>A folder, which is walked into.</summary>
        Directory,

        /// <summary>A regular file, which holds bytes a copy can carry.</summary>
        File,

        /// <summary>A link, a pipe, a socket or a device: a name, with no run of bytes behind it.</summary>
        Other,
    }

    private readonly record struct Node(
        string Source,
        string Destination,
        EntryKind Kind,
        long Size,
        int Group,
        int Depth);

    private readonly record struct Job(
        JobAction Action,
        string Source,
        string Destination,
        long Size,
        int Group,
        bool NeedsGroupSuccess);
}

/// <summary>
/// Reads one file through once and reports both digests of it. Both are fed from the same pass, so a
/// large file is read once rather than twice.
/// </summary>
internal sealed class ChecksumTask : IFileTask
{
    private const int ChunkBytes = 1024 * 1024;

    private readonly string _path;
    private readonly List<string> _failures = [];
    private readonly SharpProspero.Security.Sha256 _digest = new();
    private readonly SharpProspero.Security.Crc32 _check = new();

    private byte[]? _buffer;
    private int _file = -1;
    private long _length;
    private long _read;
    private bool _opened;
    private bool _disposed;

    /// <summary>Prepares to read <paramref name="path"/>.</summary>
    public ChecksumTask(string path)
    {
        _path = path;
        Caption = $"Checking {PathUtil.GetFileName(path)}";
        Sha256 = string.Empty;
    }

    /// <inheritdoc />
    public string Caption { get; }

    /// <inheritdoc />
    public string CurrentItem => PathUtil.GetFileName(_path);

    /// <inheritdoc />
    public bool IsFinished { get; private set; }

    /// <inheritdoc />
    public int FailureCount => _failures.Count;

    /// <inheritdoc />
    public IReadOnlyList<string> Failures => _failures;

    /// <summary>The digest as lowercase hexadecimal, once the task has finished.</summary>
    public string Sha256 { get; private set; }

    /// <summary>The check value, once the task has finished.</summary>
    public uint Crc32 { get; private set; }

    /// <inheritdoc />
    public float Progress => _length > 0 ? (float)Math.Min(1d, _read / (double)_length) : IsFinished ? 1f : 0f;

    /// <inheritdoc />
    public bool Step()
    {
        if (IsFinished || _disposed)
            return true;

        try
        {
            if (!_opened)
            {
                _opened = true;
                _buffer = new byte[ChunkBytes];
                _length = FileSystem.GetFileSize(_path);
                _file = RawFile.OpenRead(_path);
                return false;
            }

            int read = RawFile.Read(_file, _buffer!);
            if (read > 0)
            {
                var slice = new ReadOnlySpan<byte>(_buffer, 0, read);
                _digest.Update(slice);
                _check.Update(slice);
                _read += read;
                return false;
            }

            Sha256 = _digest.FinishHex();
            Crc32 = _check.Value;
            Finish();
            return true;
        }
        catch (Exception error)
        {
            _failures.Add($"{PathUtil.GetFileName(_path)}: {error.Message}");
            Finish();
            return true;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Finish();
    }

    private void Finish()
    {
        IsFinished = true;
        if (_file >= 0)
        {
            RawFile.Close(_file);
            _file = -1;
        }
        _buffer = null;
    }
}

/// <summary>
/// Packs a set of paths into one archive, a slice at a time.
/// </summary>
/// <remarks>
/// The work runs in three parts: the chosen paths are walked to a list of members, a bounded number of
/// folder listings per call; the files are read and added, a few megabytes per call; and the finished
/// archive is written out. The writer takes each member as a whole block and hands back the archive as
/// one, so everything packed has to fit in memory at once - which is what the ceilings are for, and
/// they are checked as the walk goes rather than after everything has been read.
/// </remarks>
internal sealed class ArchiveAddTask : IFileTask
{
    private const long MaxTotalBytes = ArchiveService.MaxArchiveBytes;
    private const int MaxEntries = 20000;
    private const int ListingsPerStep = 64;
    private const long BytesPerStep = 4L * 1024 * 1024;
    private const long FloorPerItem = 4096;

    private readonly string _archivePath;
    private readonly Queue<Pending> _pending = new();
    private readonly List<Item> _items = [];
    private readonly List<string> _failures = [];
    private readonly IArchiveWriter _builder;

    private Phase _phase = Phase.Walk;
    private long _totalBytes;
    private long _addedBytes;
    private int _index;
    private bool _refused;
    private bool _disposed;

    /// <summary>Packs <paramref name="sources"/> into <paramref name="archivePath"/>.</summary>
    public ArchiveAddTask(IReadOnlyList<string> sources, string archivePath, bool compress, ArchiveFormat format)
    {
        _archivePath = archivePath;
        _builder = ArchiveService.CreateWriter(format, compress);
        Caption = $"Packing {PathUtil.GetFileName(archivePath)}";

        // This runs on the frame the user pressed the button, so it only writes down what was chosen.
        // Reading the folders and asking after every file is the walk's work, and the walk is stepped.
        foreach (string source in sources)
        {
            string trimmed = source.TrimEnd('/');
            string name = PathUtil.GetFileName(trimmed);
            if (name.Length > 0)
                _pending.Enqueue(new Pending(trimmed, name, FileEntryType.Unknown));
        }
    }

    /// <inheritdoc />
    public string Caption { get; }

    /// <inheritdoc />
    public string CurrentItem { get; private set; } = string.Empty;

    /// <inheritdoc />
    public bool IsFinished { get; private set; }

    /// <inheritdoc />
    public int FailureCount => _failures.Count;

    /// <inheritdoc />
    public IReadOnlyList<string> Failures => _failures;

    /// <inheritdoc />
    public float Progress => _phase switch
    {
        // How much the walk has left is not known until it ends, so the bar only starts moving once the
        // files themselves begin going in, and the last of it covers writing the archive out.
        Phase.Walk => 0f,
        Phase.Add => _totalBytes > 0 ? 0.9f * Clamp01((float)(_addedBytes / (double)_totalBytes)) : 0.9f,
        Phase.Write => 0.95f,
        _ => 1f,
    };

    /// <inheritdoc />
    public bool Step()
    {
        if (IsFinished || _disposed)
            return true;

        switch (_phase)
        {
            case Phase.Walk:
                WalkSlice();
                break;
            case Phase.Add:
                AddSlice();
                break;
            default:
                WriteOut();
                break;
        }

        return IsFinished;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _pending.Clear();
        _items.Clear();
    }

    private void WalkSlice()
    {
        for (int i = 0; i < ListingsPerStep && _pending.Count > 0; i++)
        {
            Pending entry = _pending.Dequeue();
            CurrentItem = PathUtil.GetFileName(entry.Path);
            try
            {
                Visit(entry);
            }
            catch (Exception error)
            {
                _failures.Add($"{PathUtil.GetFileName(entry.Path)}: {error.Message}");
            }

            if (_refused)
            {
                _pending.Clear();
                break;
            }
        }

        if (_pending.Count == 0)
            _phase = _items.Count > 0 && !_refused ? Phase.Add : Phase.Write;
    }

    private void Visit(Pending entry)
    {
        // Only a path given directly by the user has no listing behind it to say what it is. Everything
        // the walk found carries the kind its own folder reported, which a status call cannot give back
        // because it follows a link through to whatever it names.
        FileEntryType type = entry.Type == FileEntryType.Unknown ? FileSystem.GetEntryType(entry.Path) : entry.Type;

        if (type == FileEntryType.Directory)
        {
            if (!HasRoom())
                return;

            _items.Add(new Item(entry.Path, entry.Name + "/", true));
            foreach (DirectoryEntry child in FileSystem.EnumerateDirectory(entry.Path))
            {
                if (child.Name is "." or "..")
                    continue;
                _pending.Enqueue(new Pending(
                    PathUtil.Combine(entry.Path, child.Name),
                    entry.Name + "/" + child.Name,
                    child.Type));
            }
            return;
        }

        // A link, a pipe, a socket or a device holds nothing an archive can carry, and opening a pipe
        // waits for a writer that may never come, so only a regular file is packed.
        if (type != FileEntryType.File)
            return;

        if (!HasRoom())
            return;

        long size = SizeOf(entry.Path);
        if (_totalBytes + size > MaxTotalBytes)
        {
            Refuse($"The selection holds more than {MaxTotalBytes / (1024 * 1024)} MB, which is more than one archive can be built from at once.");
            return;
        }

        _totalBytes += size;
        _items.Add(new Item(entry.Path, entry.Name, false));
    }

    private void AddSlice()
    {
        long slice = 0;
        while (_index < _items.Count)
        {
            Item item = _items[_index++];
            CurrentItem = PathUtil.GetFileName(item.Path);
            long cost = FloorPerItem;
            try
            {
                if (item.IsDirectory)
                {
                    _builder.AddDirectory(item.Name);
                }
                else
                {
                    byte[] content = FileSystem.ReadAllBytes(item.Path);
                    _builder.AddFile(item.Name, content);
                    _addedBytes += content.Length;
                    cost = Math.Max(content.Length, FloorPerItem);
                }
            }
            catch (Exception error)
            {
                _failures.Add($"{PathUtil.GetFileName(item.Path)}: {error.Message}");
            }

            slice += cost;
            if (slice >= BytesPerStep)
                break;
        }

        if (_index >= _items.Count)
            _phase = Phase.Write;
    }

    private void WriteOut()
    {
        // A selection that ran past a ceiling never produces a file. Writing what did fit would replace
        // whatever is already there with an archive holding part of what was asked for.
        if (!_refused)
        {
            CurrentItem = PathUtil.GetFileName(_archivePath);
            try
            {
                FileSystem.WriteAllBytes(_archivePath, _builder.Finish());
            }
            catch (Exception error)
            {
                _failures.Add($"{PathUtil.GetFileName(_archivePath)}: {error.Message}");
            }
        }

        _phase = Phase.Done;
        IsFinished = true;
    }

    // The members and their content are all held until the archive is written, so how many there can be
    // is bounded as well as how much they weigh: a folder of tiny files would otherwise pass the byte
    // ceiling and still fill the heap.
    private bool HasRoom()
    {
        if (_items.Count < MaxEntries)
            return true;
        Refuse($"The selection holds more than {MaxEntries} entries, which is more than one archive can be built from at once.");
        return false;
    }

    private void Refuse(string reason)
    {
        if (_refused)
            return;
        _refused = true;
        _failures.Add(reason);
    }

    private static long SizeOf(string path)
    {
        try
        {
            return FileSystem.GetFileSize(path);
        }
        catch (ProsperoException)
        {
            return 0;
        }
    }

    private static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;

    private enum Phase
    {
        Walk,
        Add,
        Write,
        Done,
    }

    private readonly record struct Pending(string Path, string Name, FileEntryType Type);

    private readonly record struct Item(string Path, string Name, bool IsDirectory);
}

/// <summary>
/// Unpacks an archive into a folder, a slice at a time. A lone compressed stream, which holds one file
/// and no listing, is written out under the name with the compression suffix removed.
/// </summary>
/// <remarks>
/// Reading the file, making sense of it, and writing the members out are separate calls, and the last
/// of them takes a few megabytes at a time. A member is only unpacked when it is about to be written,
/// so the largest single member is all that is held on top of the archive itself.
/// </remarks>
internal sealed class ArchiveExtractTask : IFileTask
{
    private const long BytesPerStep = 4L * 1024 * 1024;
    private const long FloorPerItem = 4096;

    private readonly string _archivePath;
    private readonly string _destinationFolder;
    private readonly List<string> _failures = [];
    private readonly HashSet<string> _made = new(StringComparer.Ordinal);

    private ArchiveListing? _listing;
    private byte[]? _raw;
    private Phase _phase = Phase.Read;
    private int _index;
    private long _done;
    private long _total;
    private bool _disposed;

    /// <summary>Unpacks <paramref name="archivePath"/> into <paramref name="destinationFolder"/>.</summary>
    public ArchiveExtractTask(string archivePath, string destinationFolder)
    {
        _archivePath = archivePath;
        string folder = destinationFolder.TrimEnd('/');
        _destinationFolder = folder.Length == 0 ? "/" : folder;
        Caption = $"Unpacking {PathUtil.GetFileName(archivePath)}";
        CurrentItem = PathUtil.GetFileName(archivePath);
    }

    /// <inheritdoc />
    public string Caption { get; }

    /// <inheritdoc />
    public string CurrentItem { get; private set; }

    /// <inheritdoc />
    public bool IsFinished { get; private set; }

    /// <inheritdoc />
    public int FailureCount => _failures.Count;

    /// <inheritdoc />
    public IReadOnlyList<string> Failures => _failures;

    /// <inheritdoc />
    public float Progress => _phase switch
    {
        Phase.Read or Phase.Open => 0f,
        Phase.Write => _total > 0 ? Clamp01((float)(_done / (double)_total)) : 0f,
        _ => 1f,
    };

    /// <inheritdoc />
    public bool Step()
    {
        if (IsFinished || _disposed)
            return true;

        try
        {
            switch (_phase)
            {
                case Phase.Read:
                    ReadFile();
                    break;
                case Phase.Open:
                    OpenListing();
                    break;
                case Phase.Write:
                    WriteSlice();
                    break;
                default:
                    Finish();
                    break;
            }
        }
        catch (Exception error)
        {
            // Reading the file or making sense of it failed, which leaves nothing to unpack.
            _failures.Add($"{PathUtil.GetFileName(_archivePath)}: {error.Message}");
            Finish();
        }

        return IsFinished;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _listing?.Dispose();
        _listing = null;
        _raw = null;
    }

    private void ReadFile()
    {
        // Both readers work from a copy of the whole file, so its size is checked before it is read
        // rather than after the heap has already gone.
        _raw = ArchiveService.ReadArchiveFile(_archivePath);
        _phase = Phase.Open;
    }

    private void OpenListing()
    {
        _listing = ArchiveService.OpenBytes(_archivePath, _raw!);
        _raw = null;

        foreach (ArchiveItem item in _listing.Items)
            _total += Math.Max(item.Size, FloorPerItem);

        // The folder is made before the first member, so a place that cannot be written fails while the
        // user is still looking at where they chose rather than part way through.
        FileSystem.CreateDirectoryRecursive(_destinationFolder);
        _made.Add(_destinationFolder);

        if (_listing.Items.Count == 0)
            Finish();
        else
            _phase = Phase.Write;
    }

    private void WriteSlice()
    {
        ArchiveListing listing = _listing!;
        long slice = 0;
        while (_index < listing.Items.Count)
        {
            ArchiveItem item = listing.Items[_index++];
            CurrentItem = item.Name;
            long cost = Math.Max(item.Size, FloorPerItem);
            _done += cost;
            slice += cost;

            try
            {
                WriteOne(listing, item);
            }
            catch (Exception error)
            {
                _failures.Add($"{item.Name}: {error.Message}");
            }

            if (slice >= BytesPerStep)
                break;
        }

        if (_index >= listing.Items.Count)
            Finish();
    }

    private void WriteOne(ArchiveListing listing, ArchiveItem item)
    {
        // An archive can name anything it likes, a path that climbs out of the folder it is being
        // unpacked into included. Such a member is passed over rather than having its path resolved.
        string? relative = ArchiveService.SafeRelativePath(item.Name, string.Empty);
        if (relative is null)
        {
            _failures.Add($"{item.Name}: the entry names a place outside the folder.");
            return;
        }

        string target = PathUtil.Combine(_destinationFolder, relative);
        if (item.IsDirectory)
        {
            MakeFolder(target);
            return;
        }

        MakeFolder(PathUtil.GetDirectoryName(target));
        FileSystem.WriteAllBytes(target, listing.Read(item));
    }

    // Members of a deep archive share their parent folders, and making one costs a call per part of the
    // path, so the folders already made are remembered.
    private void MakeFolder(string folder)
    {
        if (folder.Length == 0 || !_made.Add(folder))
            return;
        FileSystem.CreateDirectoryRecursive(folder);
    }

    private void Finish()
    {
        _phase = Phase.Done;
        IsFinished = true;
        _listing?.Dispose();
        _listing = null;
        _raw = null;
    }

    private static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;

    private enum Phase
    {
        Read,
        Open,
        Write,
        Done,
    }
}

/// <summary>
/// Every change the browser makes to a file or a folder. The one-call members here finish before they
/// return and suit anything quick; the members returning an <see cref="IFileTask"/> hand back work the
/// caller drives from the frame loop.
/// </summary>
internal static class FileOperations
{
    /// <summary>Copies <paramref name="sources"/> into <paramref name="destinationFolder"/>.</summary>
    public static IFileTask Copy(IReadOnlyList<string> sources, string destinationFolder)
        => FileTask.Copy(sources, destinationFolder);

    /// <summary>Moves <paramref name="sources"/> into <paramref name="destinationFolder"/>.</summary>
    public static IFileTask Move(IReadOnlyList<string> sources, string destinationFolder)
        => FileTask.Move(sources, destinationFolder);

    /// <summary>Removes <paramref name="targets"/>, including everything inside any folder among them.</summary>
    public static IFileTask Delete(IReadOnlyList<string> targets)
        => FileTask.Delete(targets);

    /// <summary>Reads <paramref name="path"/> through once for both of its check values.</summary>
    public static ChecksumTask Checksum(string path) => new(path);

    /// <summary>Packs <paramref name="sources"/> into a new archive at <paramref name="archivePath"/>.</summary>
    public static IFileTask AddToArchive(IReadOnlyList<string> sources, string archivePath, bool compress, ArchiveFormat format)
        => new ArchiveAddTask(sources, archivePath, compress, format);

    /// <summary>Unpacks <paramref name="archivePath"/> into <paramref name="destinationFolder"/>.</summary>
    public static IFileTask ExtractArchive(string archivePath, string destinationFolder)
        => new ArchiveExtractTask(archivePath, destinationFolder);

    /// <summary>Gives the entry at <paramref name="path"/> the name <paramref name="newName"/>, and returns its new path.</summary>
    /// <exception cref="InvalidOperationException">The name is not usable, or something already has it.</exception>
    /// <exception cref="ProsperoException">The file system refused the change.</exception>
    public static string Rename(string path, string newName)
    {
        string name = CheckName(newName);
        string parent = PathUtil.GetDirectoryName(path);
        string destination = PathUtil.Combine(parent, name);
        if (string.Equals(destination, path, StringComparison.Ordinal))
            return path;
        if (FileSystem.Exists(destination))
            throw new InvalidOperationException($"{name} already exists here.");

        FileSystem.Move(path, destination);
        return destination;
    }

    /// <summary>Creates an empty folder called <paramref name="name"/> inside <paramref name="parent"/>.</summary>
    /// <exception cref="InvalidOperationException">The name is not usable, or something already has it.</exception>
    /// <exception cref="ProsperoException">The folder could not be created.</exception>
    public static string CreateFolder(string parent, string name)
    {
        string path = PathUtil.Combine(parent, CheckName(name));
        if (FileSystem.Exists(path))
            throw new InvalidOperationException($"{PathUtil.GetFileName(path)} already exists here.");

        FileSystem.CreateDirectoryRecursive(path);
        return path;
    }

    /// <summary>Creates an empty file called <paramref name="name"/> inside <paramref name="parent"/>.</summary>
    /// <exception cref="InvalidOperationException">The name is not usable, or something already has it.</exception>
    /// <exception cref="ProsperoException">The file could not be written.</exception>
    public static string CreateFile(string parent, string name)
    {
        string path = PathUtil.Combine(parent, CheckName(name));
        if (FileSystem.Exists(path))
            throw new InvalidOperationException($"{PathUtil.GetFileName(path)} already exists here.");

        FileSystem.WriteAllText(path, string.Empty);
        return path;
    }

    /// <summary>The digest of the file at <paramref name="path"/>, as lowercase hexadecimal.</summary>
    /// <remarks>
    /// This reads the whole file before it returns. Use <see cref="Checksum"/> for anything a user might
    /// have to wait on.
    /// </remarks>
    /// <exception cref="ProsperoException">The file could not be read.</exception>
    public static string Sha256(string path) => SharpProspero.Security.Sha256.HashFileHex(path);

    /// <summary>The check value of the file at <paramref name="path"/>.</summary>
    /// <remarks>This reads the whole file before it returns, as <see cref="Sha256"/> does.</remarks>
    /// <exception cref="ProsperoException">The file could not be read.</exception>
    public static uint Crc32(string path) => SharpProspero.Security.Crc32.ComputeFileValue(path);

    /// <summary>
    /// Which of <paramref name="sources"/> already exist in <paramref name="destinationFolder"/>: how
    /// many there are, and what the first of them is called.
    /// </summary>
    /// <remarks>
    /// A paste replaces every one of them, so the count is what a question about it has to carry. All
    /// of the sources are looked at rather than stopping at the first, because naming one and replacing
    /// six is not what the user was asked.
    /// </remarks>
    public static ClashReport Clashes(IReadOnlyList<string> sources, string destinationFolder)
    {
        int count = 0;
        string? first = null;
        bool hasDirectory = false;
        foreach (string source in sources)
        {
            string name = PathUtil.GetFileName(source.TrimEnd('/'));
            if (name.Length == 0)
                continue;
            string destination = PathUtil.Combine(destinationFolder, name);
            if (!FileSystem.Exists(destination))
                continue;

            count++;
            first ??= name;

            // A status call reads the entry's record and does not open it, so learning that the clash
            // is a folder costs nothing a pipe or a device could stall on.
            if (FileSystem.GetEntryType(destination) == FileEntryType.Directory)
                hasDirectory = true;
        }

        return new ClashReport(count, first, hasDirectory);
    }

    /// <summary>
    /// What the entry at <paramref name="path"/> is and, for a regular file, how large it is, from a
    /// single status call that does not open it.
    /// </summary>
    /// <remarks>
    /// The call reads the entry's record rather than opening it, so it settles the kind and the length
    /// of a pipe, a socket or a device as readily as those of a file, and without the wait that opening
    /// one of them can bring. A symbolic link is followed to what it names, matching how a directory
    /// walk decides whether to descend.
    /// </remarks>
    /// <returns>
    /// The kind the status call reports and, for a regular file, its length in bytes. The length is -1
    /// for anything that is not a regular file. The kind is <see cref="FileEntryType.Unknown"/> and the
    /// length -1 when the path cannot be reached.
    /// </returns>
    public static unsafe (FileEntryType Type, long Size) Stat(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        int length = Encoding.UTF8.GetByteCount(path);
        byte[] owned = new byte[length + 1];
        Encoding.UTF8.GetBytes(path, 0, path.Length, owned, 0);

        SceKernelStat status = default;
        int result;
        fixed (byte* p = owned)
            result = KernelFile.stat(p, &status);
        if (result != 0)
            return (FileEntryType.Unknown, -1);

        var type = (FileEntryType)((status.Mode & KernelFile.FileTypeMask) >> 12);
        return (type, type == FileEntryType.File ? status.Size : -1);
    }

    // A name is one part of a path. Anything with a separator in it, or either of the two names a
    // listing uses for itself and its parent, would put the entry somewhere the user did not ask for.
    private static string CheckName(string name)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            throw new InvalidOperationException("The name is empty.");
        if (trimmed.IndexOf('/') >= 0)
            throw new InvalidOperationException("A name cannot hold a separator.");
        if (trimmed is "." or "..")
            throw new InvalidOperationException("That name is reserved.");
        return trimmed;
    }
}

// Reading and writing a file in pieces. The storage layer offers whole-file calls only, which cannot
// serve a copy that has to run inside a frame budget or a file larger than the heap, so these bind the
// descriptor calls directly. They stay inside this file because nothing above needs a descriptor.
file static class RawFile
{
    private const ushort NewFileMode = 0x1B6;

    public static unsafe int OpenRead(string path)
    {
        byte[] name = ToNullTerminated(path);
        int file;
        fixed (byte* p = name)
            file = KernelFile.sceKernelOpen(p, KernelFile.ReadOnly, 0);
        return SceResult.ThrowIfFailed(file, "Opening the file");
    }

    public static unsafe int OpenWrite(string path)
    {
        byte[] name = ToNullTerminated(path);
        int file;
        fixed (byte* p = name)
            file = KernelFile.sceKernelOpen(p, KernelFile.WriteOnly | KernelFile.Create | KernelFile.Truncate, NewFileMode);
        return SceResult.ThrowIfFailed(file, "Creating the file");
    }

    public static unsafe int Read(int file, byte[] buffer)
    {
        long read;
        fixed (byte* p = buffer)
            read = KernelFile.sceKernelRead(file, p, (nuint)buffer.Length);
        if (read < 0)
            throw new ProsperoException("Reading the file", (int)read);
        return (int)read;
    }

    public static unsafe void Write(int file, byte[] buffer, int count)
    {
        int written = 0;
        fixed (byte* p = buffer)
        {
            while (written < count)
            {
                long n = KernelFile.sceKernelWrite(file, p + written, (nuint)(count - written));
                if (n < 0)
                    throw new ProsperoException("Writing the file", (int)n);
                if (n == 0)
                    throw new ProsperoException("Writing the file", -1);
                written += (int)n;
            }
        }
    }

    // Closing a descriptor that was only read from can lose nothing, so its result is of no use to the
    // caller and there is nothing to report.
    public static void Close(int file) => KernelFile.sceKernelClose(file);

    /// <summary>Closes a descriptor that was written to, reporting a close that did not succeed.</summary>
    /// <remarks>
    /// The last of a write can be held back until the descriptor is closed, and a refusal - a volume
    /// with nothing left on it, most often - arrives with that close and nowhere else. A caller that
    /// discards it takes a file missing its tail for a whole one.
    /// </remarks>
    /// <exception cref="ProsperoException">The close failed.</exception>
    public static void CloseWritten(int file)
    {
        int result = KernelFile.sceKernelClose(file);
        if (result < 0)
            throw new ProsperoException("Closing the file", result);
    }

    private static byte[] ToNullTerminated(string path)
    {
        int length = Encoding.UTF8.GetByteCount(path);
        byte[] buffer = new byte[length + 1];
        Encoding.UTF8.GetBytes(path, 0, path.Length, buffer, 0);
        return buffer;
    }
}

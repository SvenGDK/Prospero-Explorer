// Prospero Explorer
// Copyright (C) 2026 SvenGDK

using ProsperoExplorer.Shell;
using SharpProspero.Compression;
using SharpProspero.Storage;
using SharpProspero.Text;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace ProsperoExplorer.Archive;

/// <summary>What a file turned out to hold once it was opened.</summary>
internal enum ArchiveKind
{
    /// <summary>A container whose members are each compressed on their own.</summary>
    Zip,

    /// <summary>A container whose members are stored end to end, the whole file compressed or not.</summary>
    Tar,

    /// <summary>A single compressed stream holding one file, with no directory of its own.</summary>
    Stream,
}

/// <summary>A file is not an archive these tools read, or cannot be written as one.</summary>
internal sealed class ArchiveException : Exception
{
    /// <summary>Creates a failure carrying <paramref name="message"/>.</summary>
    public ArchiveException(string message) : base(message)
    {
    }

    /// <summary>Creates a failure carrying <paramref name="message"/> and what caused it.</summary>
    public ArchiveException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// One member of an archive, described the same way whatever the container is, so a listing of a zip
/// and a listing of a tar are shown by the same page.
/// </summary>
internal sealed class ArchiveItem
{
    internal ArchiveItem(int index, string name, long size, long packedSize, bool isDirectory, DateTime? modified)
    {
        Index = index;
        Name = name;
        Size = size;
        PackedSize = packedSize;
        IsDirectory = isDirectory;
        Modified = modified;
    }

    /// <summary>The path within the archive, with forward slashes.</summary>
    public string Name { get; }

    /// <summary>The size once unpacked, in bytes.</summary>
    public long Size { get; }

    /// <summary>The size as stored, in bytes. It equals <see cref="Size"/> where nothing is compressed.</summary>
    public long PackedSize { get; }

    /// <summary>Whether the member is a folder rather than a file.</summary>
    public bool IsDirectory { get; }

    /// <summary>When the member was last written, where the container records it at all.</summary>
    public DateTime? Modified { get; }

    /// <summary>How much of the original the stored form takes, as a percentage.</summary>
    public int Ratio => Size <= 0 ? 100 : (int)Math.Clamp(PackedSize * 100L / Size, 0L, 1000L);

    /// <summary>Where the member sits in the container, so its bytes can be found again.</summary>
    internal int Index { get; }
}

/// <summary>
/// An opened archive: what is inside it, how large it is packed and unpacked, and a way to get any one
/// member's bytes. Both readers the platform offers work from a copy of the whole file in memory, so
/// the listing holds that copy and releasing it is what <see cref="Dispose"/> does.
/// </summary>
internal sealed class ArchiveListing : IDisposable
{
    private ZipArchive? _zip;
    private IReadOnlyList<TarEntry>? _tar;
    private byte[]? _content;
    private bool _disposed;

    internal ArchiveListing(
        string path,
        ArchiveKind kind,
        long fileSize,
        IReadOnlyList<ArchiveItem> items,
        ZipArchive? zip,
        IReadOnlyList<TarEntry>? tar,
        byte[]? content)
    {
        Path = path;
        Kind = kind;
        FileSize = fileSize;
        Items = items;
        _zip = zip;
        _tar = tar;
        _content = content;

        long unpacked = 0;
        foreach (ArchiveItem item in items)
            unpacked += item.Size;
        UnpackedTotal = unpacked;
    }

    /// <summary>The archive's own path.</summary>
    public string Path { get; }

    /// <summary>What the file turned out to hold.</summary>
    public ArchiveKind Kind { get; }

    /// <summary>The archive's size on disk, which is the packed total for every container.</summary>
    public long FileSize { get; }

    /// <summary>What the members come to once unpacked, in bytes.</summary>
    public long UnpackedTotal { get; }

    /// <summary>The members, in the order the container lists them.</summary>
    public IReadOnlyList<ArchiveItem> Items { get; }

    /// <summary>How much of the unpacked total the archive takes, as a percentage.</summary>
    public int Ratio => UnpackedTotal <= 0 ? 100 : (int)Math.Clamp(FileSize * 100L / UnpackedTotal, 0L, 1000L);

    /// <summary>What the kind is called, for a heading or a message.</summary>
    public string KindName => Kind switch
    {
        ArchiveKind.Zip => "Zip archive",
        ArchiveKind.Tar => "Tar archive",
        _ => "Compressed stream",
    };

    /// <summary>The bytes of <paramref name="item"/>. A folder returns an empty array.</summary>
    /// <exception cref="ArchiveException"><paramref name="item"/> came from another archive.</exception>
    public byte[] Read(ArchiveItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (item.IsDirectory)
            return [];

        if (_zip is not null)
        {
            if (item.Index < 0 || item.Index >= _zip.Entries.Count)
                throw new ArchiveException($"{item.Name} does not belong to this archive.");

            // A zip member is decompressed into a buffer that grows as the bytes arrive, and the size
            // the directory gives it is the buffer's starting capacity rather than a limit on it. So a
            // member claiming more than can be held is refused here, before any of it is decoded.
            if (item.Size > ArchiveService.MaxUnpackedBytes)
            {
                throw new ArchiveException(
                    $"{item.Name} unpacks to {TextFormat.ByteSize(item.Size)}, more than the "
                    + $"{TextFormat.ByteSize(ArchiveService.MaxUnpackedBytes)} one member can be unpacked into here.");
            }

            return _zip.Extract(_zip.Entries[item.Index]);
        }

        if (_tar is not null)
        {
            if (item.Index < 0 || item.Index >= _tar.Count)
                throw new ArchiveException($"{item.Name} does not belong to this archive.");
            return _tar[item.Index].Data;
        }

        return _content ?? [];
    }

    /// <summary>Releases the copy of the archive the readers work from.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _zip = null;
        _tar = null;
        _content = null;
    }
}

/// <summary>
/// Work that runs a slice at a time. Unpacking and building both touch every file in turn, which takes
/// far longer than a frame, so the caller steps one of these from the frame loop and the application
/// keeps drawing while it runs.
/// </summary>
internal abstract class ArchiveOperation
{
    /// <summary>What the work is on now, for a caption.</summary>
    public string CurrentItem { get; protected set; } = "";

    /// <summary>How much of the work is done, from 0 to 1.</summary>
    public abstract float Progress { get; }

    /// <summary>Whether there is nothing left to do.</summary>
    public bool IsComplete { get; protected set; }

    /// <summary>What stopped the work, or null when nothing did.</summary>
    public Exception? Error { get; protected set; }

    /// <summary>
    /// Does one slice. Returns true once the work has finished, whether it finished by completing or by
    /// failing; <see cref="Error"/> tells the two apart.
    /// </summary>
    public abstract bool Step();
}

/// <summary>Unpacks members of an archive into a folder, a slice at a time.</summary>
internal sealed class ExtractOperation : ArchiveOperation
{
    // How much a single slice takes on before it hands the frame back. A member costs at least the
    // floor even when it is empty, so a folder of thousands of tiny files still runs a slice at a time
    // rather than in one long stall.
    private const long BytesPerSlice = 4L * 1024 * 1024;
    private const long FloorPerItem = 4096;

    // How many failure reasons are kept for the summary. A whole archive failing onto a full volume
    // would otherwise fill the heap with one message per member; the count stays exact, only the kept
    // reasons are capped.
    private const int MaxRecordedFailures = 16;

    private readonly ArchiveListing _listing;
    private readonly IReadOnlyList<ArchiveItem> _items;
    private readonly string _destination;
    private readonly string _prefix;
    private readonly HashSet<string> _made = new(StringComparer.Ordinal);
    private readonly List<string> _failures = [];
    private long _processed;
    private readonly long _total;
    private int _next;
    private int _failed;

    internal ExtractOperation(ArchiveListing listing, IReadOnlyList<ArchiveItem> items, string destination, string prefix)
    {
        _listing = listing;
        _items = items;
        _destination = destination;
        _prefix = prefix;

        long total = 0;
        foreach (ArchiveItem item in items)
            total += Math.Max(item.Size, FloorPerItem);
        _total = total;

        // The folder is made before the first slice so a path that cannot be written fails at once,
        // while the user is still looking at where they chose, rather than part way through.
        FileSystem.CreateDirectoryRecursive(destination);
        _made.Add(destination);
    }

    /// <summary>How many files were written.</summary>
    public int Extracted { get; private set; }

    /// <summary>How many members were passed over because their stored path leaves the folder.</summary>
    public int Skipped { get; private set; }

    /// <summary>How many members could not be unpacked and were passed over.</summary>
    public int Failed => _failed;

    /// <summary>The reason the first failed member gave, or null when none failed.</summary>
    public string? FirstFailure => _failures.Count > 0 ? _failures[0] : null;

    /// <summary>How many bytes were written.</summary>
    public long BytesWritten { get; private set; }

    /// <summary>How many members the work covers.</summary>
    public int Count => _items.Count;

    /// <summary>Where the members are being written.</summary>
    public string Destination => _destination;

    /// <inheritdoc/>
    public override float Progress
        => _total <= 0 ? 1f : Math.Clamp((float)(_processed / (double)_total), 0f, 1f);

    /// <inheritdoc/>
    public override bool Step()
    {
        if (IsComplete)
            return true;

        long slice = 0;
        while (_next < _items.Count)
        {
            ArchiveItem item = _items[_next];
            CurrentItem = item.Name;
            try
            {
                slice += ExtractOne(item);
            }
            catch (Exception error)
            {
                // A member that will not decode or write is passed over with its reason kept, and the
                // rest of the archive is still unpacked, rather than the first bad one stopping it all.
                // The item's cost was charged as the attempt began, so the bar still advances past it;
                // charging the slice too keeps the frame handed back on time even if every member fails.
                _failed++;
                if (_failures.Count < MaxRecordedFailures)
                    _failures.Add($"{PathUtil.GetFileName(item.Name)}: {error.Message}");
                slice += Math.Max(item.Size, FloorPerItem);
            }

            _next++;
            if (slice >= BytesPerSlice)
                break;
        }

        if (_next >= _items.Count)
            IsComplete = true;
        return IsComplete;
    }

    private long ExtractOne(ArchiveItem item)
    {
        long cost = Math.Max(item.Size, FloorPerItem);
        _processed += cost;

        string? relative = ArchiveService.SafeRelativePath(item.Name, _prefix);
        if (relative is null)
        {
            Skipped++;
            return cost;
        }

        string target = PathUtil.Combine(_destination, relative);
        if (item.IsDirectory)
        {
            EnsureFolder(target);
            return cost;
        }

        EnsureFolder(PathUtil.GetDirectoryName(target));
        byte[] bytes = _listing.Read(item);
        FileSystem.WriteAllBytes(target, bytes);
        BytesWritten += bytes.Length;
        Extracted++;
        return cost;
    }

    // Every member of a deep archive shares its parent folders with the members around it, and making a
    // folder costs a call per path segment, so the folders already made are remembered.
    private void EnsureFolder(string folder)
    {
        if (folder.Length == 0 || !_made.Add(folder))
            return;
        FileSystem.CreateDirectoryRecursive(folder);
    }
}

/// <summary>What a container needs to be written: folders, files, and a finished set of bytes.</summary>
internal interface IArchiveWriter
{
    void AddDirectory(string name);

    void AddFile(string name, ReadOnlySpan<byte> content);

    byte[] Finish();
}

internal sealed class ZipWriter(bool compress) : IArchiveWriter
{
    private readonly ZipBuilder _builder = new();

    public void AddDirectory(string name) => _builder.AddDirectory(name);

    public void AddFile(string name, ReadOnlySpan<byte> content) => _builder.Add(name, content, compress);

    public byte[] Finish() => _builder.ToArray();
}

/// <summary>
/// Writes a tar archive. The platform reads tar archives but does not write them, so the records are
/// laid out here: a 512-byte header per member, its bytes padded up to the next block, and two empty
/// blocks at the end.
/// </summary>
internal sealed class TarWriter : IArchiveWriter
{
    private const int BlockSize = 512;
    private const int RecordSize = BlockSize * 20;
    private const int NameField = 100;

    // A path too long for the header's name field is carried in a record of its own placed ahead of
    // the member. This is the name that record is filed under, which is what marks it as one.
    private static readonly byte[] LongNameMarker = "././@LongLink"u8.ToArray();

    private readonly List<byte> _output = [];

    public void AddDirectory(string name)
    {
        string folder = name.Replace('\\', '/');
        if (!folder.EndsWith('/'))
            folder += "/";
        WriteEntry(folder, (byte)'5', "0000755", ReadOnlySpan<byte>.Empty);
    }

    public void AddFile(string name, ReadOnlySpan<byte> content)
        => WriteEntry(name.Replace('\\', '/'), (byte)'0', "0000644", content);

    public byte[] Finish()
    {
        // Two empty blocks close the archive and the rest fills out the last record, which is the
        // shape every reader expects to run into.
        AddZeros(BlockSize * 2);
        int remainder = _output.Count % RecordSize;
        if (remainder != 0)
            AddZeros(RecordSize - remainder);
        return [.. _output];
    }

    private void WriteEntry(string name, byte typeFlag, string mode, ReadOnlySpan<byte> content)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        if (nameBytes.Length > NameField)
        {
            byte[] payload = new byte[nameBytes.Length + 1];
            nameBytes.CopyTo(payload, 0);
            WriteHeader(LongNameMarker, (byte)'L', "0000644", payload.Length);
            WriteData(payload);
            nameBytes = TrimToField(nameBytes);
        }

        WriteHeader(nameBytes, typeFlag, mode, content.Length);
        WriteData(content);
    }

    private void WriteHeader(ReadOnlySpan<byte> name, byte typeFlag, string mode, long size)
    {
        byte[] header = new byte[BlockSize];
        name[..Math.Min(name.Length, NameField)].CopyTo(header);
        WriteAscii(header, 100, mode);
        WriteAscii(header, 108, "0000000");
        WriteAscii(header, 116, "0000000");
        WriteAscii(header, 124, Octal(size, 11));

        // No per-file modification time is available to copy, so a fixed one is written rather than
        // the time of the build, which would claim every file had just changed.
        WriteAscii(header, 136, Octal(0, 11));
        header[156] = typeFlag;
        WriteAscii(header, 257, "ustar");
        WriteAscii(header, 263, "00");

        // The checksum counts every byte of the header with its own field read as spaces.
        for (int i = 148; i < 156; i++)
            header[i] = (byte)' ';
        int sum = 0;
        foreach (byte b in header)
            sum += b;
        WriteAscii(header, 148, Octal(sum, 6));
        header[154] = 0;
        header[155] = (byte)' ';

        _output.AddRange(header);
    }

    private void WriteData(ReadOnlySpan<byte> content)
    {
        if (content.Length == 0)
            return;
        _output.AddRange(content);
        int remainder = content.Length % BlockSize;
        if (remainder != 0)
            AddZeros(BlockSize - remainder);
    }

    private void AddZeros(int count) => _output.AddRange(new byte[count]);

    private static void WriteAscii(byte[] header, int offset, string text)
    {
        for (int i = 0; i < text.Length; i++)
            header[offset + i] = (byte)text[i];
    }

    private static string Octal(long value, int digits)
    {
        if (value < 0)
            throw new ArchiveException("A tar record cannot carry a negative length.");
        string text = Convert.ToString(value, 8);
        if (text.Length > digits)
            throw new ArchiveException($"A file of {TextFormat.ByteSize(value)} is larger than a tar record can describe.");
        return text.PadLeft(digits, '0');
    }

    // The header keeps a shortened name as well, for a reader that ignores the record carrying the
    // full one. Cutting at a fixed byte would leave half a character behind, so the cut moves back
    // to the start of the character it lands in.
    private static byte[] TrimToField(byte[] name)
    {
        int end = NameField;
        while (end > 0 && (name[end] & 0xC0) == 0x80)
            end--;
        return name[..end];
    }
}

/// <summary>
/// Reads and writes the archive forms the application handles: zip and tar containers, and a lone gzip
/// or zlib stream, which is listed as a container of one member so every page treats it the same way.
/// </summary>
internal static class ArchiveService
{
    /// <summary>
    /// The largest archive file the readers take. Both of them work from a copy of the whole file in
    /// memory, so a larger one would not fit beside what the application is already holding.
    /// </summary>
    public const long MaxArchiveBytes = 96L * 1024 * 1024;

    /// <summary>
    /// The largest lone compressed stream the readers take, measured as it is stored.
    /// </summary>
    public const long MaxStreamBytes = 32L * 1024 * 1024;

    /// <summary>
    /// The largest one member or one stream may come to once unpacked. Both readers build their output
    /// in a buffer that doubles as the bytes arrive and stop only when the compressed data does, so a
    /// file of a few kilobytes can ask for the whole heap. What is unpacked is held beside the copy of
    /// the archive it came from, which is why the two ceilings together stay well inside the heap the
    /// module is given.
    /// </summary>
    public const long MaxUnpackedBytes = 64L * 1024 * 1024;

    // The decompression service writes at most 64 KiB per call, so only a small stream can come back
    // through it when the reader in the SDK refuses one.
    private const int ServiceStreamLimit = 64 * 1024;

    /// <summary>
    /// Reads an archive file into memory, refusing one larger than the readers can hold.
    /// </summary>
    /// <exception cref="ArchiveException">The file is larger than <see cref="MaxArchiveBytes"/>.</exception>
    public static byte[] ReadArchiveFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        long size = FileSystem.GetFileSize(path);
        if (size > MaxArchiveBytes)
        {
            throw new ArchiveException(
                $"{PathUtil.GetFileName(path)} is {TextFormat.ByteSize(size)}. An archive is read whole, so "
                + $"{TextFormat.ByteSize(MaxArchiveBytes)} is as large as one can be opened here.");
        }

        return FileSystem.ReadAllBytes(path);
    }

    /// <summary>Opens an archive already read into <paramref name="data"/>.</summary>
    /// <remarks>
    /// Reading the file and making sense of it are separate so a caller driving this from the frame loop
    /// can do them in different frames.
    /// </remarks>
    /// <exception cref="ArchiveException">The data is not an archive these tools read.</exception>
    public static ArchiveListing OpenBytes(string path, byte[] data)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(data);

        // The bytes at the very front only suggest a format. A tar names its first member there, and
        // those bytes can fall on a zip or a compressed-stream header by chance, so a positive match is
        // tried but not taken as final: if the file does not actually read as that container, it still
        // gets its chance to read as a tar before it is called unreadable.
        if (LooksLikeZip(data))
        {
            try
            {
                return ZipListing(path, data);
            }
            catch (Exception zipError)
            {
                return TarOrRethrow(path, data, zipError);
            }
        }

        if (LooksLikeGzip(data) || LooksLikeZlib(data))
        {
            try
            {
                return StreamOrWrappedTar(path, data);
            }
            catch (Exception streamError)
            {
                return TarOrRethrow(path, data, streamError);
            }
        }

        if (TryReadTar(data, out List<TarEntry>? entries))
            return TarListing(path, data.Length, entries);

        throw new ArchiveException(
            $"{PathUtil.GetFileName(path)} is not a zip or tar archive, and not a gzip or zlib stream either.");
    }

    // Reads a lone compressed stream: the tar inside it where it holds one, and otherwise its single
    // unpacked member.
    private static ArchiveListing StreamOrWrappedTar(string path, byte[] data)
    {
        byte[] plain = Unpack(path, data);
        return TryReadTar(plain, out List<TarEntry>? wrapped)
            ? TarListing(path, data.Length, wrapped)
            : StreamListing(path, data.Length, plain);
    }

    // The file did not read as the container its leading bytes resembled. A tar reading is tried, since
    // a tar's first bytes can coincide with another format's header; where it is not a tar either, the
    // first failure is raised, as it is the telling one - a stream genuinely too large to unpack, or a
    // checksum that did not hold - rather than a general "not an archive".
    private static ArchiveListing TarOrRethrow(string path, byte[] data, Exception original)
    {
        if (TryReadTar(data, out List<TarEntry>? entries))
            return TarListing(path, data.Length, entries);
        throw original;
    }

    /// <summary>Unpacks every member of <paramref name="listing"/> into <paramref name="destination"/>.</summary>
    public static ExtractOperation ExtractAll(ArchiveListing listing, string destination)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        return new ExtractOperation(listing, listing.Items, destination, "");
    }

    /// <summary>
    /// Unpacks one member into <paramref name="destination"/>. A file lands in that folder under its own
    /// name; a folder brings everything below it, keeping the tree it had inside the archive.
    /// </summary>
    public static ExtractOperation ExtractEntry(ArchiveListing listing, ArchiveItem item, string destination)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrEmpty(destination);

        if (!item.IsDirectory)
        {
            // Asking for one file means that file where it was asked for, not buried under the folders it
            // happened to sit in inside the archive, so its own folders are stripped off.
            int slash = item.Name.LastIndexOf('/');
            string parent = slash < 0 ? "" : item.Name[..(slash + 1)];
            return new ExtractOperation(listing, [item], destination, parent);
        }

        string prefix = item.Name.EndsWith('/') ? item.Name : item.Name + "/";
        var members = new List<ArchiveItem>();
        foreach (ArchiveItem candidate in listing.Items)
        {
            if (candidate.Name.Length > prefix.Length && candidate.Name.StartsWith(prefix, StringComparison.Ordinal))
                members.Add(candidate);
        }

        return new ExtractOperation(listing, members, destination, prefix);
    }

    /// <summary>Creates a writer for <paramref name="format"/>.</summary>
    /// <param name="format">Which container to write.</param>
    /// <param name="compress">Whether zip members are compressed. A tar stores its members either way.</param>
    internal static IArchiveWriter CreateWriter(ArchiveFormat format, bool compress)
        => format == ArchiveFormat.Tar ? new TarWriter() : new ZipWriter(compress);

    /// <summary>
    /// The path a member should be written to under an extraction folder, with
    /// <paramref name="prefix"/> taken off the front, or null when it must not be written at all.
    /// </summary>
    internal static string? SafeRelativePath(string name, string prefix)
    {
        string relative = name.Replace('\\', '/');
        if (prefix.Length > 0 && relative.StartsWith(prefix, StringComparison.Ordinal))
            relative = relative[prefix.Length..];

        var parts = new List<string>();
        foreach (string part in relative.Split('/'))
        {
            if (part.Length == 0 || part == ".")
                continue;

            // A stored path naming its parent would write outside the folder the user chose, so the
            // member is passed over rather than having the path resolved for it.
            if (part == "..")
                return null;
            parts.Add(part);
        }

        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    private static ArchiveListing ZipListing(string path, byte[] data)
    {
        ZipArchive zip = ZipArchive.Open(data);
        var items = new List<ArchiveItem>(zip.Entries.Count);
        for (int i = 0; i < zip.Entries.Count; i++)
        {
            ZipEntry entry = zip.Entries[i];
            DateTime? modified = entry.LastModified == default ? null : entry.LastModified;
            items.Add(new ArchiveItem(
                i, entry.Name, entry.UncompressedSize, entry.CompressedSize, entry.IsDirectory, modified));
        }

        return new ArchiveListing(path, ArchiveKind.Zip, data.Length, items, zip, null, null);
    }

    private static ArchiveListing TarListing(string path, long fileSize, IReadOnlyList<TarEntry> entries)
    {
        var items = new List<ArchiveItem>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            TarEntry entry = entries[i];
            long size = entry.IsDirectory ? 0 : entry.Data.Length;

            // A tar stores its members as they are and records no modification time these readers
            // surface, so the stored size is the packed size and nothing is claimed about the date.
            items.Add(new ArchiveItem(i, entry.Name, size, size, entry.IsDirectory, null));
        }

        return new ArchiveListing(path, ArchiveKind.Tar, fileSize, items, null, entries, null);
    }

    private static ArchiveListing StreamListing(string path, long fileSize, byte[] content)
    {
        var items = new List<ArchiveItem>(1)
        {
            new(0, InnerName(path), content.Length, fileSize, false, null),
        };
        return new ArchiveListing(path, ArchiveKind.Stream, fileSize, items, null, null, content);
    }

    private static byte[] Unpack(string path, byte[] data)
    {
        if (data.Length > MaxStreamBytes)
        {
            throw new ArchiveException(
                $"{PathUtil.GetFileName(path)} is {TextFormat.ByteSize(data.Length)}, more than the "
                + $"{TextFormat.ByteSize(MaxStreamBytes)} a compressed stream can be as it is stored.");
        }

        try
        {
            // The reader is capped at the unpacked ceiling, so a stream that expands far past its stored
            // size - a decompression bomb - is stopped during the decode rather than after the memory is
            // taken, while a stream comfortably inside the ceiling is not turned away on its stored size.
            return LooksLikeGzip(data) ? Inflate.Gzip(data, (int)MaxUnpackedBytes) : UnpackZlib(data);
        }
        catch (CompressionException error) when (error.Message.Contains("exceeds the maximum", StringComparison.Ordinal))
        {
            throw new ArchiveException(
                $"{PathUtil.GetFileName(path)} unpacks to more than the {TextFormat.ByteSize(MaxUnpackedBytes)} a "
                + "compressed stream can be unpacked into here. Unpack it with a tool that writes straight to storage instead.");
        }
    }

    private static byte[] UnpackZlib(byte[] data)
    {
        try
        {
            return Inflate.Zlib(data, (int)MaxUnpackedBytes);
        }
        catch (CompressionException original)
        {
            // A bomb (capped) or a stream the service is too large to retry is not worth a second try.
            if (original.Message.Contains("exceeds the maximum", StringComparison.Ordinal) || data.Length > ServiceStreamLimit)
                throw;

            // The reader above rejects a stream whose checksum does not match what it produced. The
            // system service decodes independently, so a small stream is worth one attempt through it
            // before the file is called unreadable.
            try
            {
                using ZlibDecompressor service = ZlibDecompressor.Create();
                return service.Decompress(data);
            }
            catch (Exception)
            {
                throw original;
            }
        }
    }

    private static string InnerName(string path)
    {
        string name = PathUtil.GetFileName(path);
        string extension = PathUtil.GetExtension(name).ToLowerInvariant();
        string inner = extension switch
        {
            ".gz" or ".zlib" => PathUtil.GetFileNameWithoutExtension(name),
            ".tgz" => PathUtil.GetFileNameWithoutExtension(name) + ".tar",
            _ => name + ".out",
        };
        return inner.Length == 0 ? "content" : inner;
    }

    private static bool TryReadTar(byte[] data, [NotNullWhen(true)] out List<TarEntry>? entries)
    {
        entries = null;
        if (data.Length < 512)
            return false;

        try
        {
            List<TarEntry> read = TarArchive.Read(data);
            if (read.Count == 0)
                return false;
            entries = read;
            return true;
        }
        catch (Exception)
        {
            // Whatever the file is, it does not read as a tar. The caller says so in its own words.
            return false;
        }
    }

    private static bool LooksLikeZip(byte[] data)
        => data.Length >= 4 && data[0] == 'P' && data[1] == 'K' && (data[2] == 3 || data[2] == 5 || data[2] == 7);

    private static bool LooksLikeGzip(byte[] data) => data.Length >= 18 && data[0] == 0x1F && data[1] == 0x8B;

    // A zlib stream opens with a compression byte and a check byte whose two-byte value is a multiple of
    // thirty-one. That pairing is specific enough to tell one from a file that merely starts with data.
    private static bool LooksLikeZlib(byte[] data)
        => data.Length >= 6 && (data[0] & 0x0F) == 8 && (((data[0] << 8) | data[1]) % 31) == 0;
}

// Prospero Explorer
// Copyright (C) 2026 SvenGDK

using ProsperoExplorer.Shell;
using SharpProspero.Interop;
using SharpProspero.Interop.Kernel;
using SharpProspero.Platform;
using SharpProspero.Storage;
using SharpProspero.Text;
using SharpProspero.Timing;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace ProsperoExplorer.Ftp;

/// <summary>What a file's status call reported: the kind, the length and when it last changed.</summary>
internal readonly struct FtpFileStatus
{
    /// <summary>Records one status reading.</summary>
    public FtpFileStatus(ushort mode, long size, long modifiedSeconds)
    {
        Mode = mode;
        Size = size;
        ModifiedSeconds = modifiedSeconds;
    }

    /// <summary>The kind bits and the permission bits together.</summary>
    public ushort Mode { get; }

    /// <summary>The length in bytes.</summary>
    public long Size { get; }

    /// <summary>When the file last changed, in seconds since the start of 1970.</summary>
    public long ModifiedSeconds { get; }

    /// <summary>True when the path is a directory.</summary>
    public bool IsDirectory => (Mode & KernelFile.FileTypeMask) == 0x4000;

    /// <summary>
    /// True when the path is an ordinary file. A pipe, a socket or a device carries the same read and
    /// write calls but answers them only when something at the other end is ready, so a transfer may
    /// open none of them.
    /// </summary>
    public bool IsRegularFile => (Mode & KernelFile.FileTypeMask) == 0x8000;
}

/// <summary>
/// Reading and writing one file a piece at a time.
///
/// The storage API reads and writes whole files, which a transfer cannot use: a client may ask for a
/// file far larger than the heap, and pulling one in before the first byte goes out would hold up the
/// frame for as long as it took. These open a descriptor and move a bounded slice at a time, so a
/// transfer of any size costs the same per frame.
/// </summary>
internal static unsafe class FtpFile
{
    /// <summary>Opens <paramref name="path"/> and returns its descriptor.</summary>
    /// <exception cref="ProsperoException">The file could not be opened.</exception>
    public static int Open(string path, int flags, ushort mode = 0)
    {
        byte[] owned = NullTerminated(path);
        int descriptor;
        fixed (byte* p = owned)
            descriptor = KernelFile.sceKernelOpen(p, flags, mode);
        return SceResult.ThrowIfFailed(descriptor, nameof(KernelFile.sceKernelOpen));
    }

    /// <summary>Closes <paramref name="descriptor"/>, ignoring one that was never opened.</summary>
    public static void Close(int descriptor)
    {
        if (descriptor >= 0)
            KernelFile.sceKernelClose(descriptor);
    }

    /// <summary>Moves the read or write position and returns where it landed.</summary>
    /// <exception cref="ProsperoException">The position could not be moved.</exception>
    public static long Seek(int descriptor, long offset, int whence)
    {
        long result = KernelFile.sceKernelLseek(descriptor, offset, whence);
        if (result < 0)
            throw new ProsperoException(nameof(KernelFile.sceKernelLseek), (int)result);
        return result;
    }

    /// <summary>Reads up to <paramref name="count"/> bytes and returns how many arrived; zero at the end.</summary>
    /// <exception cref="ProsperoException">The read failed.</exception>
    public static int Read(int descriptor, byte[] buffer, int count)
    {
        if (count <= 0)
            return 0;
        long read;
        fixed (byte* p = buffer)
            read = KernelFile.sceKernelRead(descriptor, p, (nuint)count);
        if (read < 0)
            throw new ProsperoException(nameof(KernelFile.sceKernelRead), (int)read);
        return (int)read;
    }

    /// <summary>Writes <paramref name="count"/> bytes, repeating until the file system has taken them all.</summary>
    /// <exception cref="ProsperoException">The write failed or the device stopped accepting bytes.</exception>
    public static void Write(int descriptor, byte[] buffer, int count)
    {
        int written = 0;
        while (written < count)
        {
            long step;
            fixed (byte* p = buffer)
                step = KernelFile.sceKernelWrite(descriptor, p + written, (nuint)(count - written));
            if (step <= 0)
                throw new ProsperoException(nameof(KernelFile.sceKernelWrite), step < 0 ? (int)step : 0);
            written += (int)step;
        }
    }

    /// <summary>
    /// Reads one batch of entries from a directory descriptor into <paramref name="into"/> and returns
    /// false once the directory has no more. Reading a folder whole costs a frame in proportion to how
    /// many entries it holds, so a listing takes it a batch at a time and resumes on the next frame.
    /// </summary>
    /// <exception cref="ProsperoException">The directory could not be read.</exception>
    public static bool ReadEntries(int descriptor, byte[] buffer, int length, List<DirectoryEntry> into)
    {
        int read;
        fixed (byte* p = buffer)
            read = KernelFile.sceKernelGetdents(descriptor, p, length);
        if (read < 0)
            throw new ProsperoException(nameof(KernelFile.sceKernelGetdents), read);
        if (read == 0)
            return false;
        FileSystem.DecodeEntries(buffer.AsSpan(0, read), into);
        return true;
    }

    /// <summary>
    /// Reads the status of <paramref name="path"/>, returning false when it cannot be reached. A
    /// listing asks for the kind, the length and the date together, and one call answers all three.
    /// </summary>
    public static bool TryStatus(string path, out FtpFileStatus status)
    {
        byte[] owned = NullTerminated(path);
        SceKernelStat raw = default;
        int result;
        fixed (byte* p = owned)
            result = KernelFile.stat(p, &raw);
        if (result != 0)
        {
            status = default;
            return false;
        }
        status = new FtpFileStatus(raw.Mode, raw.Size, raw.ModifySeconds);
        return true;
    }

    private static byte[] NullTerminated(string path)
    {
        int count = Encoding.UTF8.GetByteCount(path);
        byte[] buffer = new byte[count + 1];
        Encoding.UTF8.GetBytes(path, buffer);
        return buffer;
    }
}

/// <summary>
/// A file service that reaches the whole file system over the network, so a computer on the same
/// network can browse, fetch and place files with any client that speaks the protocol.
///
/// The application draws every frame, so nothing here is allowed to wait. Every socket is set to
/// return at once, readiness comes from one poller asked with a zero timeout, and a transfer moves a
/// bounded number of bytes per <see cref="Tick"/> and picks up where it left off on the next frame.
/// The whole service therefore costs a fixed, small amount of time per frame however large the file
/// being moved is, and however many clients are connected.
/// </summary>
internal sealed class FtpService : IDisposable
{
    // A socket that returns at once reports "nothing to do" by failing the call rather than by
    // returning zero, so every send, receive and accept is attempted only where a failure can be read
    // as "not now" and retried on the next frame.

    /// <summary>The token the listening socket carries in the poller. Client tokens start above it.</summary>
    private const uint ListenerToken = 0;

    /// <summary>How many clients may be connected at once. Further connections are refused politely.</summary>
    private const int MaxClients = 8;

    /// <summary>How many waiting connections are accepted in one frame.</summary>
    private const int AcceptsPerTick = 4;

    /// <summary>The shared buffer a command read or a stored slice passes through.</summary>
    private const int ScratchBytes = 64 * 1024;

    /// <summary>How many recent lines the activity list keeps.</summary>
    private const int MaxActivityLines = 60;

    private readonly AppSettings _settings;
    private readonly List<string> _activity = [];
    private readonly List<FtpClient> _clients = [];
    private readonly Dictionary<uint, PollEvents> _ready = [];
    private readonly PollReady[] _readyBuffer = new PollReady[64];
    private readonly byte[] _scratch = new byte[ScratchBytes];

    private TcpListener? _listener;
    private SocketPoller? _poller;
    private string _address = "";
    private uint _nextToken = ListenerToken + 1;
    private bool _disposed;

    /// <summary>Creates the service against the settings it takes its rules from.</summary>
    public FtpService(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>Whether the service is listening.</summary>
    public bool IsRunning => _listener is not null;

    /// <summary>The network port the service listens on while it is running.</summary>
    public int Port { get; private set; }

    /// <summary>How many clients are connected.</summary>
    public int ClientCount => _clients.Count;

    /// <summary>The most recent lines of activity, newest first.</summary>
    public IReadOnlyList<string> Activity => _activity;

    /// <summary>
    /// Starts listening on the port in the settings. Does nothing when it is already running.
    /// </summary>
    /// <exception cref="ProsperoException">The port could not be opened.</exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
            return;

        int port = _settings.FtpPort;
        TcpListener listener = TcpListener.Listen(SocketAddress.Any(port), MaxClients);
        SocketPoller poller;
        try
        {
            listener.Blocking = false;
            poller = SocketPoller.Create();
        }
        catch
        {
            listener.Dispose();
            throw;
        }

        try
        {
            poller.Add(listener.Handle, PollEvents.Read, ListenerToken);
        }
        catch
        {
            poller.Dispose();
            listener.Dispose();
            throw;
        }

        _listener = listener;
        _poller = poller;
        Port = port;
        _address = ReadOwnAddress();
        Record($"Listening on port {port}.");
    }

    /// <summary>
    /// Stops listening and drops every client. Safe to call when the service is not running.
    /// </summary>
    public void Stop()
    {
        if (_clients.Count > 0)
        {
            foreach (FtpClient client in _clients)
                client.Close("the service stopped");
            _clients.Clear();
        }

        if (_listener is null)
            return;

        _poller?.Dispose();
        _poller = null;
        _listener.Dispose();
        _listener = null;
        Record("Stopped.");
    }

    /// <summary>
    /// Advances the service by one frame: takes new clients, reads commands, moves transfers on and
    /// sends replies. Safe to call every frame whether the service is running or not, and returns
    /// without waiting when there is nothing to do.
    /// </summary>
    /// <exception cref="ProsperoException">The poller failed, which takes the service down.</exception>
    public void Tick()
    {
        if (_disposed || _listener is null || _poller is null)
            return;

        CollectReady();
        AcceptClients();

        for (int i = _clients.Count - 1; i >= 0; i--)
        {
            FtpClient client = _clients[i];
            try
            {
                client.Tick(_ready);
            }
            catch (Exception error)
            {
                // One client's failure closes that client. The service and the other clients carry on.
                client.Close(ExplorerShell.Describe(error));
            }

            if (client.IsClosed)
                _clients.RemoveAt(i);
        }
    }

    /// <summary>Stops the service and releases every socket it holds.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }

    /// <summary>Adds <paramref name="message"/> to the activity list, dropping the oldest line when full.</summary>
    private void Record(string message)
    {
        string stamp = Timestamp();
        _activity.Insert(0, stamp.Length == 0 ? message : stamp + "  " + message);
        if (_activity.Count > MaxActivityLines)
            _activity.RemoveAt(_activity.Count - 1);
    }

    private static string Timestamp()
    {
        try
        {
            DateTime now = SystemClock.LocalNow;
            return $"{Pad2(now.Hour)}:{Pad2(now.Minute)}:{Pad2(now.Second)}";
        }
        catch (ProsperoException)
        {
            // The clock is not essential to the line; the message stands on its own without it.
            return "";
        }
    }

    private static string Pad2(int value) => value < 10 ? "0" + value.ToString() : value.ToString();

    // The address a passive reply has to hand back is the console's own, which the connection itself
    // cannot report: a connection knows only its remote end.
    private static string ReadOwnAddress()
    {
        try
        {
            using NetworkInfo info = NetworkInfo.Open();
            return info.IsConnected ? info.IpAddress : "";
        }
        catch (ProsperoException)
        {
            return "";
        }
    }

    private void CollectReady()
    {
        _ready.Clear();
        int count = _poller!.Wait(_readyBuffer, timeoutMicroseconds: 0);
        for (int i = 0; i < count; i++)
        {
            PollReady entry = _readyBuffer[i];
            _ready[entry.Token] = _ready.TryGetValue(entry.Token, out PollEvents already)
                ? already | entry.Events
                : entry.Events;
        }
    }

    private void AcceptClients()
    {
        if (!_ready.TryGetValue(ListenerToken, out PollEvents events) || (events & PollEvents.Read) == 0)
            return;

        for (int i = 0; i < AcceptsPerTick; i++)
        {
            TcpConnection connection;
            try
            {
                connection = _listener!.Accept();
            }
            catch (ProsperoException)
            {
                // No further connection is waiting. The listener reports that by failing the call.
                return;
            }

            if (_clients.Count >= MaxClients)
            {
                Refuse(connection, "421 Too many connections; try again shortly.\r\n");
                Record("Refused a connection: the service is full.");
                continue;
            }

            Adopt(connection);
        }
    }

    private void Adopt(TcpConnection connection)
    {
        SocketAddress peer = PeerAddress(connection);
        string remote = DescribeAddress(peer);
        uint token = _nextToken++;
        try
        {
            connection.Blocking = false;
            _poller!.Add(connection.Handle, PollEvents.Read, token);
        }
        catch (ProsperoException)
        {
            connection.Dispose();
            Record($"A connection from {remote} could not be taken up.");
            return;
        }

        var client = new FtpClient(this, connection, token, remote, peer);
        _clients.Add(client);
        client.Greet();
        Record($"{remote} connected.");
    }

    /// <summary>The machine at the other end, or a zero address when it cannot be read.</summary>
    private static SocketAddress PeerAddress(TcpConnection connection)
    {
        try
        {
            return connection.RemoteAddress;
        }
        catch (ProsperoException)
        {
            return default;
        }
    }

    /// <summary>True when nothing is known about where a connection came from.</summary>
    private static bool IsUnknownAddress(SocketAddress address)
        => address.A == 0 && address.B == 0 && address.C == 0 && address.D == 0;

    /// <summary>True when two connections come from the same machine, whatever ports they use.</summary>
    private static bool IsSameHost(SocketAddress left, SocketAddress right)
        => left.A == right.A && left.B == right.B && left.C == right.C && left.D == right.D;

    private static string DescribeAddress(SocketAddress address)
        => IsUnknownAddress(address) ? "a client" : address.IpString;

    private static void Refuse(TcpConnection connection, string message)
    {
        try
        {
            connection.Send(Encoding.ASCII.GetBytes(message));
            connection.Shutdown();
        }
        catch (ProsperoException)
        {
            // The refusal is a courtesy; the connection is closed either way.
        }
        connection.Dispose();
    }

    /// <summary>
    /// Turns a path the client gave into a path within the service's own space, resolving <c>.</c> and
    /// <c>..</c> without touching the file system. A <c>..</c> at the top stays at the top, which is
    /// what keeps a client inside the folder the settings name.
    /// </summary>
    internal static string NormalizeVirtual(string current, string argument)
    {
        string combined = argument.Length == 0
            ? current
            : argument[0] == '/'
                ? argument
                : current.EndsWith('/') ? current + argument : current + "/" + argument;

        var parts = new List<string>();
        foreach (string part in combined.Split('/'))
        {
            if (part.Length == 0 || part == ".")
                continue;
            if (part == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return parts.Count == 0 ? "/" : "/" + string.Join('/', parts);
    }

    /// <summary>
    /// The prefix every path the client names sits under, taken from the settings. The whole file
    /// system is the default, and that case is an empty prefix so a client path is used as it stands.
    /// </summary>
    internal static string RealRoot(string configured)
    {
        string root = string.IsNullOrWhiteSpace(configured) ? "/" : configured.Trim();
        root = NormalizeVirtual("/", root);
        return root == "/" ? "" : root;
    }

    /// <summary>Joins a directory and a name into one path.</summary>
    internal static string JoinPath(string directory, string name)
        => directory.EndsWith('/') ? directory + name : directory + "/" + name;

    /// <summary>
    /// One connected client: its control connection, where it is in the file system, and whatever
    /// transfer it has in flight. Every method here returns within the frame that called it.
    /// </summary>
    private sealed class FtpClient
    {
        /// <summary>How far a listing has got.</summary>
        private enum ListPhase
        {
            /// <summary>Reading the folder, a batch of entries per frame.</summary>
            Scanning,

            /// <summary>Ordering what was read, a bounded number of comparisons per frame.</summary>
            Sorting,

            /// <summary>Formatting the lines and sending them.</summary>
            Sending,
        }

        /// <summary>What a client is moving over its data connection.</summary>
        private enum TransferKind
        {
            /// <summary>Nothing.</summary>
            None,

            /// <summary>A directory listing, going out.</summary>
            Listing,

            /// <summary>A file, going out.</summary>
            Retrieve,

            /// <summary>A file, coming in.</summary>
            Store,
        }

        /// <summary>The largest command line accepted, which bounds what one client can make us hold.</summary>
        private const int MaxCommandBytes = 8192;

        /// <summary>The largest run of unsent replies allowed before the client is dropped.</summary>
        private const int MaxPendingReplyBytes = 256 * 1024;

        /// <summary>How much of a file moves in one slice.</summary>
        private const int SliceBytes = 64 * 1024;

        /// <summary>How many slices one client moves per frame, which caps its share of the frame.</summary>
        private const int SlicesPerTick = 4;

        /// <summary>How many reads of the control connection one frame makes.</summary>
        private const int CommandReadsPerTick = 4;

        /// <summary>
        /// How many commands one client runs per frame. A client may send thousands in one packet, and
        /// each of them costs at least one call into the file system, so the rest wait in the buffer
        /// and run on the frames that follow.
        /// </summary>
        private const int CommandsPerTick = 8;

        /// <summary>
        /// How many bytes of unparsed input may wait before the control connection is left unread for
        /// the frame. What stays in the socket is the back pressure that stops a client sending faster
        /// than its commands run.
        /// </summary>
        private const int MaxBufferedInputBytes = 64 * 1024;

        /// <summary>How many listing lines are formatted per frame, so a huge folder never stalls one.</summary>
        private const int ListEntriesPerTick = 96;

        /// <summary>How many directory entries are read in one call while a listing is being gathered.</summary>
        private const int ListBatchBytes = 8192;

        /// <summary>How many of those batches one frame reads.</summary>
        private const int ListBatchesPerTick = 4;

        /// <summary>How many entries one listing may hold, which is the ceiling on what it costs.</summary>
        private const int MaxListEntries = 20000;

        /// <summary>How many entries the ordering pass places per frame.</summary>
        private const int ListMergesPerTick = 8192;

        /// <summary>How many waiting data connections one frame takes off the queue.</summary>
        private const int DataAcceptsPerTick = 4;

        /// <summary>
        /// How many frames a transfer may make no progress before it is given up. At sixty frames a
        /// second this is about half a minute, which covers a slow client without holding a dead one
        /// open for ever.
        /// </summary>
        private const int StallTicksBeforeAbort = 1800;

        private static readonly string[] MonthNames =
        [
            "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
        ];

        private readonly FtpService _service;
        private readonly TcpConnection _control;
        private readonly uint _controlToken;
        private readonly string _remote;
        private readonly SocketAddress _peer;

        // The sign-in rule and the folder a client is confined to are read once, when it connects, so a
        // change made in the settings while it is connected cannot move it into another folder or wave
        // it past a sign-in it has already cleared. Such a change reaches only the clients that connect
        // after it.
        private readonly bool _requireLogin;
        private readonly string _root;

        private readonly List<byte> _pending = [];
        private readonly List<byte> _input = [];

        private PollEvents _controlWatch = PollEvents.Read;
        private bool _authenticated;
        private string _named = "";
        private string _directory = "/";
        private string? _renameFrom;
        private long _restartOffset;
        private bool _binary = true;
        private bool _closeWhenFlushed;
        private bool _discarding;
        private bool _peerFinished;

        private TcpListener? _dataListener;
        private uint _dataListenToken;
        private TcpConnection? _data;
        private uint _dataToken;
        private bool _reportedForeignData;

        private TransferKind _transfer;
        private int _file = -1;
        private byte[] _chunk = [];
        private int _chunkLength;
        private int _chunkSent;
        private List<DirectoryEntry>? _listEntries;
        private DirectoryEntry[] _listOrdering = [];
        private ListPhase _listPhase;
        private int _mergeWidth;
        private int _mergeStart;
        private int _mergeMiddle;
        private int _mergeEnd;
        private int _mergeLeft;
        private int _mergeRight;
        private int _mergeOut;
        private bool _mergeFlipped;
        private int _listIndex;
        private string _listDirectory = "";
        private bool _listNamesOnly;
        private string _transferName = "";
        private long _transferred;
        private int _stalledTicks;

        public FtpClient(FtpService service, TcpConnection control, uint controlToken, string remote, SocketAddress peer)
        {
            _service = service;
            _control = control;
            _controlToken = controlToken;
            _remote = remote;
            // The machine a data connection has to come from. The passive port is advertised in the
            // clear, so anything else that reaches it is another machine racing for the transfer.
            _peer = peer;
            _requireLogin = service._settings.FtpRequireLogin;
            _root = FtpService.RealRoot(service._settings.FtpRoot);
            _authenticated = !_requireLogin;
        }

        /// <summary>True once the client has been closed and can be dropped from the service.</summary>
        public bool IsClosed { get; private set; }

        /// <summary>Sends the opening line the client waits for before it says anything.</summary>
        public void Greet()
        {
            Reply(220, "Prospero Explorer file service ready.");
            FlushOutput();
            UpdateWatch();
        }

        /// <summary>Advances this client by one frame.</summary>
        public void Tick(Dictionary<uint, PollEvents> ready)
        {
            if (IsClosed)
                return;

            PollEvents control = ready.TryGetValue(_controlToken, out PollEvents events) ? events : PollEvents.None;
            if ((control & PollEvents.Error) != 0)
            {
                Close("the connection failed");
                return;
            }

            ReadCommands(control);
            if (IsClosed)
                return;

            AcceptDataConnection(ready);
            StepTransfer(ready);
            FlushOutput();
            UpdateWatch();

            if ((control & PollEvents.HangUp) != 0)
            {
                Close("the client hung up");
                return;
            }

            if (_closeWhenFlushed && _pending.Count == 0)
                Close("");
        }

        /// <summary>
        /// Closes everything this client holds. <paramref name="reason"/> is recorded when it says
        /// something the user would want to see; an ordinary goodbye passes an empty string.
        /// </summary>
        public void Close(string reason)
        {
            if (IsClosed)
                return;
            IsClosed = true;

            AbandonTransfer();
            CloseDataChannel();
            Unwatch(_control.Handle);
            try
            {
                _control.Shutdown();
            }
            catch (ProsperoException)
            {
                // The connection may already be gone; it is being closed either way.
            }
            _control.Dispose();

            _service.Record(reason.Length == 0
                ? $"{_remote} disconnected."
                : $"{_remote} disconnected: {reason}");
        }

        // Reading and writing the control connection.

        private void ReadCommands(PollEvents events)
        {
            if ((events & (PollEvents.Read | PollEvents.HangUp)) != 0)
                FillInput();

            RunBufferedCommands();

            // A client that has closed its sending side is finished once the commands it already sent
            // have run. Anything still buffered is a partial line it will never finish.
            if (_peerFinished && !IsClosed && !HasCompleteCommand())
                Close("");
        }

        // Moves what has arrived into the buffer the commands are parsed out of. Reading and running
        // are separate so that a client which sends more commands than a frame can run does not decide
        // how long the frame takes.
        private void FillInput()
        {
            byte[] scratch = _service._scratch;
            for (int pass = 0; pass < CommandReadsPerTick; pass++)
            {
                if (_peerFinished || _input.Count >= MaxBufferedInputBytes)
                    return;

                int read;
                try
                {
                    read = _control.Receive(scratch);
                }
                catch (ProsperoException)
                {
                    // Nothing more is waiting. A connection that returns at once says so by failing.
                    return;
                }

                if (read == 0)
                {
                    _peerFinished = true;
                    return;
                }

                _input.AddRange(scratch.AsSpan(0, read));
                if (read < scratch.Length)
                    return;
            }
        }

        private bool HasCompleteCommand()
            => CollectionsMarshal.AsSpan(_input).IndexOf((byte)'\n') >= 0;

        // Runs the complete lines that are waiting, up to this frame's share. Every line taken out of
        // the buffer counts against that share, empty ones included, so a client cannot spend the frame
        // by sending nothing but line endings.
        private void RunBufferedCommands()
        {
            for (int executed = 0; executed < CommandsPerTick && !IsClosed; executed++)
            {
                int newline = CollectionsMarshal.AsSpan(_input).IndexOf((byte)'\n');
                if (newline < 0)
                {
                    // A run this long with no line ending is not a command, and keeping it would let
                    // one client decide how much memory we hold.
                    if (_input.Count > MaxCommandBytes)
                    {
                        _input.Clear();
                        _discarding = true;
                    }
                    return;
                }

                bool overlong = _discarding || newline > MaxCommandBytes;
                string line = overlong
                    ? ""
                    : Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(_input)[..newline]).Trim();
                _input.RemoveRange(0, newline + 1);
                _discarding = false;

                if (overlong)
                {
                    // The rest of an over-long line is thrown away, and it is answered once at its end
                    // rather than once per buffer it filled.
                    Reply(500, "The command was too long.");
                    continue;
                }

                if (line.Length > 0)
                    Execute(line);
            }
        }

        private void Reply(int code, string text) => Append($"{code} {OneLine(text)}\r\n");

        private void ReplyLines(int code, string opening, IReadOnlyList<string> lines, string closing)
        {
            Append($"{code}-{OneLine(opening)}\r\n");
            foreach (string line in lines)
                Append(" " + OneLine(line) + "\r\n");
            Append($"{code} {OneLine(closing)}\r\n");
        }

        // A client that never reads its replies would otherwise make us hold them for ever.
        private void Append(string text)
        {
            if (_pending.Count > MaxPendingReplyBytes)
            {
                Close("it stopped reading its replies");
                return;
            }
            _pending.AddRange(Encoding.UTF8.GetBytes(text));
        }

        private void FlushOutput()
        {
            while (!IsClosed && _pending.Count > 0)
            {
                int sent;
                try
                {
                    sent = _control.Send(CollectionsMarshal.AsSpan(_pending));
                }
                catch (ProsperoException)
                {
                    // The connection cannot take more this frame; the rest goes out on a later one.
                    return;
                }
                if (sent <= 0)
                    return;
                _pending.RemoveRange(0, sent);
            }
        }

        // The control connection is watched for writability only while a reply is waiting, so an idle
        // client costs one readiness check a frame and nothing else.
        private void UpdateWatch()
        {
            if (IsClosed)
                return;
            PollEvents desired = PollEvents.Read;
            if (_pending.Count > 0)
                desired |= PollEvents.Write;
            if (desired == _controlWatch)
                return;
            try
            {
                _service._poller!.Modify(_control.Handle, desired, _controlToken);
                _controlWatch = desired;
            }
            catch (ProsperoException)
            {
                // The socket is on its way out; the next read closes the client.
            }
        }

        private void Unwatch(int handle)
        {
            try
            {
                _service._poller?.Remove(handle);
            }
            catch (ProsperoException)
            {
                // A socket the poller no longer knows about needs nothing removed.
            }
        }

        // The commands.

        private void Execute(string line)
        {
            int space = line.IndexOf(' ');
            string command = (space < 0 ? line : line[..space]).ToUpperInvariant();
            string argument = space < 0 ? "" : line[(space + 1)..].Trim();

            if (!_authenticated && !IsOpenBeforeLogin(command))
            {
                Reply(530, "Log in first.");
                return;
            }

            if (NeedsWriting(command) && !_service._settings.FtpAllowWrite)
            {
                Reply(550, "This service is read-only.");
                return;
            }

            switch (command)
            {
                case "USER": HandleUser(argument); break;
                case "PASS": HandlePassword(argument); break;
                case "ACCT": Reply(202, "No account is needed."); break;
                case "QUIT": Reply(221, "Goodbye."); _closeWhenFlushed = true; break;
                case "NOOP": Reply(200, "Still here."); break;
                case "SYST": Reply(215, "UNIX Type: L8"); break;
                case "FEAT": HandleFeatures(); break;
                case "OPTS": HandleOptions(argument); break;
                case "TYPE": HandleType(argument); break;
                case "MODE": Reply(argument.ToUpperInvariant() == "S" ? 200 : 504, "Stream mode is the only mode."); break;
                case "STRU": Reply(argument.ToUpperInvariant() == "F" ? 200 : 504, "File structure is the only structure."); break;
                case "ALLO": Reply(202, "No space needs setting aside."); break;
                case "PWD":
                case "XPWD": Reply(257, $"\"{Quote(_directory)}\" is the current directory."); break;
                case "CWD":
                case "XCWD": HandleChangeDirectory(argument); break;
                case "CDUP":
                case "XCUP": HandleChangeDirectory(".."); break;
                case "PASV": HandlePassive(extended: false); break;
                case "EPSV": HandleExtendedPassive(argument); break;
                case "PORT":
                case "EPRT": Reply(502, "Active transfers are not supported; use PASV or EPSV."); break;
                case "LIST": HandleList(argument, namesOnly: false); break;
                case "NLST": HandleList(argument, namesOnly: true); break;
                case "RETR": HandleRetrieve(argument); break;
                case "STOR": HandleStore(argument, append: false); break;
                case "APPE": HandleStore(argument, append: true); break;
                case "DELE": HandleDelete(argument); break;
                case "RMD":
                case "XRMD": HandleRemoveDirectory(argument); break;
                case "MKD":
                case "XMKD": HandleMakeDirectory(argument); break;
                case "RNFR": HandleRenameFrom(argument); break;
                case "RNTO": HandleRenameTo(argument); break;
                case "SIZE": HandleSize(argument); break;
                case "MDTM": HandleModifiedTime(argument); break;
                case "REST": HandleRestart(argument); break;
                case "STAT": HandleStatus(argument); break;
                case "ABOR": HandleAbort(); break;
                default: Reply(500, $"'{command}' is not a command this service knows."); break;
            }
        }

        private static bool IsOpenBeforeLogin(string command)
            => command is "USER" or "PASS" or "ACCT" or "QUIT" or "NOOP" or "FEAT" or "SYST" or "OPTS";

        private static bool NeedsWriting(string command)
            => command is "STOR" or "APPE" or "DELE" or "RMD" or "XRMD" or "MKD" or "XMKD" or "RNFR" or "RNTO";

        private void HandleUser(string argument)
        {
            if (!_requireLogin)
            {
                _authenticated = true;
                _named = argument.Length == 0 ? "anonymous" : argument;
                Reply(230, "Logged in; no password is needed.");
                return;
            }
            _named = argument;
            _authenticated = false;
            Reply(331, "Password required.");
        }

        private void HandlePassword(string argument)
        {
            if (!_requireLogin)
            {
                _authenticated = true;
                Reply(230, "Logged in; no password is needed.");
                return;
            }

            AppSettings settings = _service._settings;
            if (string.Equals(_named, settings.FtpUser, StringComparison.Ordinal)
                && string.Equals(argument, settings.FtpPassword, StringComparison.Ordinal))
            {
                _authenticated = true;
                Reply(230, $"Logged in as {_named}.");
                _service.Record($"{_remote} logged in as {_named}.");
                return;
            }

            _authenticated = false;
            Reply(530, "The name or the password is wrong.");
            _service.Record($"{_remote} failed to log in.");
        }

        private void HandleFeatures()
        {
            ReplyLines(211, "Features:", ["SIZE", "MDTM", "REST STREAM", "TVFS", "UTF8", "EPSV", "PASV"], "End");
        }

        private void HandleOptions(string argument)
        {
            string value = argument.ToUpperInvariant();
            if (value is "UTF8 ON" or "UTF8")
            {
                Reply(200, "Names are always UTF-8.");
                return;
            }
            Reply(501, "That option cannot be set.");
        }

        private void HandleType(string argument)
        {
            string value = argument.Length == 0 ? "" : argument.ToUpperInvariant();
            int space = value.IndexOf(' ');
            string kind = space < 0 ? value : value[..space];
            switch (kind)
            {
                case "I":
                case "L":
                    _binary = true;
                    Reply(200, "Type set to binary.");
                    break;
                case "A":
                    // The bytes go out exactly as the file holds them whichever type is asked for. A
                    // file explorer's whole purpose is that a file arrives as it was, and rewriting
                    // line endings would also break restarting a transfer at an offset.
                    _binary = false;
                    Reply(200, "Type set to text; bytes are still transferred unchanged.");
                    break;
                default:
                    Reply(504, "That type is not supported.");
                    break;
            }
        }

        // Every command that names something refuses an empty name rather than acting on the folder
        // the client happens to be sitting in.
        private bool NamesSomething(string command, string argument)
        {
            if (argument.Length > 0)
                return true;
            Reply(501, $"{command} needs a path.");
            return false;
        }

        private void HandleChangeDirectory(string argument)
        {
            if (!NamesSomething("CWD", argument))
                return;

            string target = FtpService.NormalizeVirtual(_directory, argument);
            string real = ToReal(target);
            if (!FileSystem.IsDirectory(real))
            {
                Reply(550, $"'{target}' is not a folder.");
                return;
            }
            _directory = target;
            Reply(250, $"Now in '{target}'.");
        }

        // The data channel.

        private void HandlePassive(bool extended)
        {
            // Opening a new data connection would pull the one a running transfer is using out from
            // under it, so a transfer in flight is reported rather than broken.
            if (!IsIdle())
                return;

            int port = OpenDataListener();
            if (port < 0)
                return;

            if (extended)
            {
                Reply(229, $"Entering Extended Passive Mode (|||{port}|)");
                return;
            }

            if (_service._address.Length == 0)
                _service._address = ReadOwnAddress();

            if (!SocketAddress.TryParse(_service._address, port, out SocketAddress own))
            {
                CloseDataChannel();
                Reply(425, "The console's own address is not known; use EPSV instead.");
                return;
            }
            Reply(227, $"Entering Passive Mode ({own.A},{own.B},{own.C},{own.D},{port >> 8},{port & 0xFF})");
        }

        private void HandleExtendedPassive(string argument)
        {
            if (argument.ToUpperInvariant() == "ALL")
            {
                Reply(200, "Extended passive mode only.");
                return;
            }
            HandlePassive(extended: true);
        }

        // Returns the port the data connection will arrive on, or -1 after replying with the failure.
        private int OpenDataListener()
        {
            CloseDataChannel();

            TcpListener listener;
            try
            {
                listener = TcpListener.Listen(SocketAddress.Any(0), backlog: 1);
            }
            catch (ProsperoException error)
            {
                Reply(425, $"A data connection could not be opened: {ExplorerShell.Describe(error)}");
                return -1;
            }

            int port;
            uint token = _service._nextToken++;
            try
            {
                listener.Blocking = false;
                port = listener.LocalAddress.Port;
                _service._poller!.Add(listener.Handle, PollEvents.Read, token);
            }
            catch (ProsperoException error)
            {
                listener.Dispose();
                Reply(425, $"A data connection could not be opened: {ExplorerShell.Describe(error)}");
                return -1;
            }

            _dataListener = listener;
            _dataListenToken = token;
            _reportedForeignData = false;
            return port;
        }

        private void AcceptDataConnection(Dictionary<uint, PollEvents> ready)
        {
            if (_data is not null || _dataListener is null)
                return;
            if (!ready.TryGetValue(_dataListenToken, out PollEvents events) || (events & PollEvents.Read) == 0)
                return;

            // A connection that is turned away is taken off the queue in the same frame, so a stray one
            // ahead of the client's own does not cost the transfer a frame each.
            for (int attempt = 0; attempt < DataAcceptsPerTick; attempt++)
            {
                TcpConnection connection;
                try
                {
                    connection = _dataListener.Accept();
                }
                catch (ProsperoException)
                {
                    // Nothing is waiting after all; the next frame looks again.
                    return;
                }

                if (!IsFromControlPeer(connection))
                {
                    // Another machine reached the advertised port first. Taking it would hand it the
                    // file the client asked for, or let it write the file the client is uploading. The
                    // listener stays open so the client's own connection is still accepted.
                    connection.Dispose();
                    if (!_reportedForeignData)
                    {
                        _reportedForeignData = true;
                        _service.Record($"Refused a data connection to {_remote}'s transfer from another address.");
                    }
                    continue;
                }

                uint token = _service._nextToken++;
                try
                {
                    connection.Blocking = false;
                    // Both directions are watched from the start: which one a transfer uses is settled
                    // by the command that follows, and swapping the registration later buys nothing.
                    _service._poller!.Add(connection.Handle, PollEvents.Read | PollEvents.Write, token);
                }
                catch (ProsperoException)
                {
                    connection.Dispose();
                    return;
                }

                Unwatch(_dataListener.Handle);
                _dataListener.Dispose();
                _dataListener = null;

                _data = connection;
                _dataToken = token;
                return;
            }
        }

        // The data connection carries no credentials of its own, so the only thing tying it to the
        // session is where it came from. A control connection whose own address could not be read
        // leaves nothing to compare against, and refusing every transfer in that case would take the
        // service down rather than protect it.
        private bool IsFromControlPeer(TcpConnection connection)
            => IsUnknownAddress(_peer) || IsSameHost(_peer, PeerAddress(connection));

        private void CloseDataChannel()
        {
            if (_dataListener is not null)
            {
                Unwatch(_dataListener.Handle);
                _dataListener.Dispose();
                _dataListener = null;
            }

            if (_data is null)
                return;

            Unwatch(_data.Handle);
            try
            {
                _data.Shutdown();
            }
            catch (ProsperoException)
            {
                // The other end may have gone already; the socket is closed either way.
            }
            _data.Dispose();
            _data = null;
        }

        private bool HasDataChannel()
        {
            if (_dataListener is not null || _data is not null)
                return true;
            Reply(425, "Open a data connection first with PASV or EPSV.");
            return false;
        }

        private bool IsIdle()
        {
            if (_transfer == TransferKind.None)
                return true;
            Reply(450, "A transfer is already running on this connection.");
            return false;
        }

        // Listings.

        private void HandleList(string argument, bool namesOnly)
        {
            if (!HasDataChannel() || !IsIdle())
                return;

            string target = FtpService.NormalizeVirtual(_directory, StripListOptions(argument));
            string real = ToReal(target);

            var entries = new List<DirectoryEntry>();
            int directory = -1;
            if (FileSystem.IsDirectory(real))
            {
                try
                {
                    // The folder is held open and read across frames. Reading it here would cost this
                    // frame as much time as the folder has entries, and a folder has no upper bound.
                    directory = FtpFile.Open(real, KernelFile.ReadOnly | KernelFile.Directory);
                }
                catch (Exception error)
                {
                    Reply(550, $"'{target}' could not be listed: {ExplorerShell.Describe(error)}");
                    return;
                }
                _listDirectory = real;
            }
            else if (FileSystem.Exists(real))
            {
                // A client that names one file expects that file's own line back, not its folder's.
                entries.Add(new DirectoryEntry { Name = NameOf(target), Type = FileEntryType.File });
                _listDirectory = ParentOf(real);
            }
            else
            {
                Reply(550, $"'{target}' does not exist.");
                return;
            }

            _file = directory;
            _listEntries = entries;
            _listPhase = directory >= 0 ? ListPhase.Scanning : ListPhase.Sending;
            _listOrdering = [];
            _listIndex = 0;
            _listNamesOnly = namesOnly;
            _transfer = TransferKind.Listing;
            _transferName = target;
            _transferred = 0;
            _stalledTicks = 0;
            _chunk = [];
            _chunkLength = 0;
            _chunkSent = 0;
            Reply(150, $"Opening a data connection for the listing of '{target}'.");
        }

        // A client may decorate the command with the switches a shell listing takes. They carry no
        // meaning here, and whatever follows them is the path.
        private static string StripListOptions(string argument)
        {
            string rest = argument.Trim();
            while (rest.StartsWith('-'))
            {
                int space = rest.IndexOf(' ');
                if (space < 0)
                    return "";
                rest = rest[(space + 1)..].Trim();
            }
            return rest;
        }

        // Transfers.

        private void HandleRetrieve(string argument)
        {
            // A restart offset applies to the next transfer command and no later one, so it is spent as
            // soon as a transfer command is seen, whether or not that command then goes ahead; a client
            // turned away for a recoverable reason re-sends REST before retrying. Leaving it armed would
            // let it misapply to some unrelated later transfer.
            long restart = TakeRestartOffset();
            if (!HasDataChannel() || !IsIdle())
                return;
            if (argument.Length == 0)
            {
                Reply(501, "RETR needs a file name.");
                return;
            }

            string target = FtpService.NormalizeVirtual(_directory, argument);
            string real = ToReal(target);
            if (!FtpFile.TryStatus(real, out FtpFileStatus status) || !status.IsRegularFile)
            {
                // Only an ordinary file answers a read straight away. A pipe, a socket or a device
                // answers when something at the other end is ready, and this frame cannot wait for it.
                Reply(550, $"'{target}' is not a file.");
                return;
            }

            int descriptor = -1;
            try
            {
                descriptor = FtpFile.Open(real, KernelFile.ReadOnly);
                if (restart > 0)
                    FtpFile.Seek(descriptor, restart, KernelFile.SeekSet);
            }
            catch (Exception error)
            {
                // The seek fails after the open has succeeded, and the descriptor is ours from that
                // point on. Nothing else would ever close it.
                FtpFile.Close(descriptor);
                Reply(550, $"'{target}' could not be opened: {ExplorerShell.Describe(error)}");
                return;
            }

            _file = descriptor;
            _transfer = TransferKind.Retrieve;
            _transferName = target;
            _transferred = 0;
            _stalledTicks = 0;
            _chunk = new byte[SliceBytes];
            _chunkLength = 0;
            _chunkSent = 0;
            long remaining = Math.Max(0, status.Size - restart);
            Reply(150, $"Opening a data connection for '{NameOf(target)}' ({remaining} bytes).");
        }

        private void HandleStore(string argument, bool append)
        {
            // The restart offset is spent as soon as a transfer command is seen (see HandleRetrieve),
            // so it can never carry over to an unrelated later transfer; a client turned away re-sends
            // REST before retrying.
            long restart = TakeRestartOffset();
            if (!HasDataChannel() || !IsIdle())
                return;
            if (argument.Length == 0)
            {
                Reply(501, "A file name is needed.");
                return;
            }

            string target = FtpService.NormalizeVirtual(_directory, argument);
            string real = ToReal(target);
            if (FtpFile.TryStatus(real, out FtpFileStatus existing) && !existing.IsRegularFile)
            {
                // A path that is already there has to be an ordinary file. Writing into a pipe, a
                // socket or a device would wait for whatever is at the other end to take the bytes.
                Reply(550, existing.IsDirectory ? $"'{target}' is a folder." : $"'{target}' is not a file.");
                return;
            }

            // Continuing an interrupted upload writes into the file that is already there, so the
            // truncate that a fresh upload wants would throw away exactly what is being resumed.
            bool resuming = !append && restart > 0;
            int flags = KernelFile.WriteOnly | KernelFile.Create
                | (append ? KernelFile.Append : resuming ? 0 : KernelFile.Truncate);
            int descriptor = -1;
            try
            {
                descriptor = FtpFile.Open(real, flags, mode: 0x1B6);
                if (resuming)
                    FtpFile.Seek(descriptor, restart, KernelFile.SeekSet);
            }
            catch (Exception error)
            {
                // The open may have succeeded and the seek failed, which leaves the descriptor ours.
                FtpFile.Close(descriptor);
                Reply(550, $"'{target}' could not be written: {ExplorerShell.Describe(error)}");
                return;
            }

            _file = descriptor;
            _transfer = TransferKind.Store;
            _transferName = target;
            _transferred = 0;
            _stalledTicks = 0;
            Reply(150, $"Ready for '{NameOf(target)}'.");
        }

        private void HandleAbort()
        {
            if (_transfer == TransferKind.None)
            {
                Reply(226, "Nothing to stop.");
                return;
            }
            AbandonTransfer();
            CloseDataChannel();
            Reply(426, "Transfer stopped.");
            Reply(226, "Stopped as asked.");
        }

        private void StepTransfer(Dictionary<uint, PollEvents> ready)
        {
            if (_transfer == TransferKind.None)
                return;

            if (_data is null)
            {
                // The client has been told to connect but has not yet. Give it a bounded wait.
                if (++_stalledTicks < StallTicksBeforeAbort)
                    return;
                FinishTransfer(425, "The client never opened the data connection.");
                return;
            }

            PollEvents events = ready.TryGetValue(_dataToken, out PollEvents found) ? found : PollEvents.None;
            bool progressed = _transfer == TransferKind.Store ? StepStore(events) : StepSend(events);

            if (_transfer == TransferKind.None)
                return;

            if (progressed)
            {
                _stalledTicks = 0;
                return;
            }

            // A file being stored ends when the client closes the connection, and that arrives as a
            // read of zero bytes. Reaching here with the connection down and nothing read means it
            // broke rather than finished.
            if ((events & (PollEvents.Error | PollEvents.HangUp)) != 0)
            {
                FinishTransfer(426, "The data connection closed before the transfer finished.");
                return;
            }

            if (++_stalledTicks >= StallTicksBeforeAbort)
                FinishTransfer(426, "The data connection stopped responding.");
        }

        private bool StepSend(PollEvents events)
        {
            if ((events & (PollEvents.Write | PollEvents.HangUp | PollEvents.Error)) == 0)
                return false;

            bool progressed = false;
            for (int slice = 0; slice < SlicesPerTick; slice++)
            {
                if (_chunkSent >= _chunkLength)
                {
                    bool more;
                    try
                    {
                        more = Refill();
                    }
                    catch (Exception error)
                    {
                        FinishTransfer(451, $"The file could not be read: {ExplorerShell.Describe(error)}");
                        return true;
                    }

                    if (!more)
                    {
                        Complete();
                        return true;
                    }
                    if (_chunkLength == 0)
                    {
                        // A listing that is still being gathered has nothing to send yet. That is work
                        // done, not a stall, so the frame ends here without counting against the wait.
                        return true;
                    }
                }

                int offered = _chunkLength - _chunkSent;
                int sent;
                try
                {
                    sent = _data!.Send(_chunk.AsSpan(_chunkSent, offered));
                }
                catch (ProsperoException)
                {
                    // The connection will not take more right now. What is left goes out next frame.
                    return progressed;
                }

                if (sent <= 0)
                    return progressed;
                _chunkSent += sent;
                _transferred += sent;
                progressed = true;

                // A short send means the connection is full. Trying again in this frame would only
                // fail, so the rest waits for the poller to say there is room.
                if (sent < offered)
                    return progressed;
            }
            return progressed;
        }

        private bool StepStore(PollEvents events)
        {
            if ((events & (PollEvents.Read | PollEvents.HangUp | PollEvents.Error)) == 0)
                return false;

            byte[] scratch = _service._scratch;
            bool progressed = false;
            for (int slice = 0; slice < SlicesPerTick; slice++)
            {
                int read;
                try
                {
                    read = _data!.Receive(scratch);
                }
                catch (ProsperoException)
                {
                    // Nothing has arrived yet; the rest comes on a later frame.
                    return progressed;
                }

                if (read == 0)
                {
                    // The client closing the data connection is how the end of a stored file is marked.
                    Complete();
                    return true;
                }

                try
                {
                    FtpFile.Write(_file, scratch, read);
                }
                catch (Exception error)
                {
                    FinishTransfer(452, $"The file could not be written: {ExplorerShell.Describe(error)}");
                    return true;
                }

                _transferred += read;
                progressed = true;
                if (read < scratch.Length)
                    return progressed;
            }
            return progressed;
        }

        // Fills the outgoing slice, returning false once the source has nothing left. An empty slice
        // with true means the listing is still being gathered and the next frame carries it further.
        private bool Refill()
        {
            if (_transfer == TransferKind.Retrieve)
            {
                int read = FtpFile.Read(_file, _chunk, _chunk.Length);
                _chunkLength = read;
                _chunkSent = 0;
                return read > 0;
            }

            if (_listPhase == ListPhase.Sending)
                return FormatListing();

            if (_listPhase == ListPhase.Scanning)
                ScanDirectory();
            else
                SortListing();

            _chunk = [];
            _chunkLength = 0;
            _chunkSent = 0;
            return true;
        }

        // Reads a bounded number of batches out of the open folder.
        private void ScanDirectory()
        {
            List<DirectoryEntry> entries = _listEntries!;
            for (int batch = 0; batch < ListBatchesPerTick; batch++)
            {
                if (entries.Count >= MaxListEntries)
                {
                    // A ceiling on one listing. Without it a folder decides how much memory the
                    // application holds, and nothing in a folder says how large it will be.
                    _service.Record($"{_transferName} holds more than {MaxListEntries} entries; the listing stops there.");
                    BeginSorting();
                    return;
                }

                if (!FtpFile.ReadEntries(_file, _service._scratch, ListBatchBytes, entries))
                {
                    BeginSorting();
                    return;
                }
            }
        }

        private void BeginSorting()
        {
            // The folder has been read. Holding the descriptor open for the rest of the transfer would
            // keep it busy for nothing.
            FtpFile.Close(_file);
            _file = -1;

            int count = _listEntries!.Count;
            if (count < 2)
            {
                _listPhase = ListPhase.Sending;
                return;
            }

            _listOrdering = new DirectoryEntry[count];
            _mergeWidth = 1;
            _mergeStart = 0;
            _mergeFlipped = false;
            StartMergeRun(count);
            _listPhase = ListPhase.Sorting;
        }

        // Orders the entries by merging ever longer runs, a bounded number of placements per frame.
        // A sort that runs to completion in one call costs the frame in proportion to the folder, and
        // the run being merged when the frame's share is spent resumes from where it stopped.
        private void SortListing()
        {
            Span<DirectoryEntry> gathered = CollectionsMarshal.AsSpan(_listEntries!);
            int count = gathered.Length;

            for (int budget = ListMergesPerTick; budget > 0;)
            {
                if (_mergeWidth >= count)
                {
                    // Every run is now one run. Whichever half holds it is the answer.
                    if (_mergeFlipped)
                        _listOrdering.AsSpan(0, count).CopyTo(gathered);
                    _listOrdering = [];
                    _listPhase = ListPhase.Sending;
                    return;
                }

                ReadOnlySpan<DirectoryEntry> source = _mergeFlipped ? _listOrdering.AsSpan(0, count) : gathered;
                Span<DirectoryEntry> target = _mergeFlipped ? gathered : _listOrdering.AsSpan(0, count);

                while (budget > 0 && _mergeOut < _mergeEnd)
                {
                    bool takeLeft = _mergeLeft < _mergeMiddle
                        && (_mergeRight >= _mergeEnd
                            || TextFormat.CompareNatural(source[_mergeLeft].Name, source[_mergeRight].Name) <= 0);
                    target[_mergeOut++] = takeLeft ? source[_mergeLeft++] : source[_mergeRight++];
                    budget--;
                }

                if (_mergeOut < _mergeEnd)
                    return;

                _mergeStart = _mergeEnd;
                if (_mergeStart >= count)
                {
                    _mergeStart = 0;
                    _mergeWidth *= 2;
                    _mergeFlipped = !_mergeFlipped;
                }
                StartMergeRun(count);
            }
        }

        private void StartMergeRun(int count)
        {
            _mergeMiddle = Math.Min(_mergeStart + _mergeWidth, count);
            _mergeEnd = Math.Min(_mergeStart + (2 * _mergeWidth), count);
            _mergeLeft = _mergeStart;
            _mergeRight = _mergeMiddle;
            _mergeOut = _mergeStart;
        }

        private bool FormatListing()
        {
            List<DirectoryEntry> entries = _listEntries ?? [];
            if (_listIndex >= entries.Count)
                return false;

            DateTime now = NowOrDefault();
            var text = new StringBuilder(4096);
            int end = Math.Min(entries.Count, _listIndex + ListEntriesPerTick);
            for (; _listIndex < end; _listIndex++)
            {
                DirectoryEntry entry = entries[_listIndex];
                text.Append(_listNamesOnly ? entry.Name : FormatListLine(_listDirectory, entry, now));
                text.Append("\r\n");
            }

            _chunk = Encoding.UTF8.GetBytes(text.ToString());
            _chunkLength = _chunk.Length;
            _chunkSent = 0;
            return true;
        }

        private void Complete()
        {
            string what = _transfer switch
            {
                TransferKind.Retrieve => $"{_remote} fetched {_transferName} ({TextFormat.ByteSize(_transferred)})",
                TransferKind.Store => $"{_remote} stored {_transferName} ({TextFormat.ByteSize(_transferred)})",
                _ => $"{_remote} listed {_transferName}",
            };
            FinishTransfer(226, "Transfer complete.");
            _service.Record(what + ".");
        }

        private void FinishTransfer(int code, string text)
        {
            bool failed = code >= 400;
            string name = _transferName;
            AbandonTransfer();
            CloseDataChannel();
            Reply(code, text);
            if (failed)
                _service.Record($"{_remote} failed on {name}: {text}");
        }

        // A listing holds the folder open in _file, a fetch the file it is sending and a store the file
        // it is writing, so one close covers all three.
        private void AbandonTransfer()
        {
            FtpFile.Close(_file);
            _file = -1;
            _chunk = [];
            _chunkLength = 0;
            _chunkSent = 0;
            _listEntries = null;
            _listOrdering = [];
            _listPhase = ListPhase.Scanning;
            _listIndex = 0;
            _listDirectory = "";
            _transfer = TransferKind.None;
            _stalledTicks = 0;
            _transferred = 0;
        }

        // Single-path commands.

        private void HandleDelete(string argument)
        {
            if (!NamesSomething("DELE", argument))
                return;

            string target = FtpService.NormalizeVirtual(_directory, argument);
            string real = ToReal(target);
            if (FileSystem.IsDirectory(real))
            {
                Reply(550, $"'{target}' is a folder; use RMD.");
                return;
            }

            try
            {
                FileSystem.DeleteFile(real);
            }
            catch (Exception error)
            {
                Reply(550, $"'{target}' could not be deleted: {ExplorerShell.Describe(error)}");
                return;
            }
            Reply(250, $"'{target}' deleted.");
            _service.Record($"{_remote} deleted {target}.");
        }

        private void HandleRemoveDirectory(string argument)
        {
            if (!NamesSomething("RMD", argument))
                return;

            string target = FtpService.NormalizeVirtual(_directory, argument);
            if (target == "/")
            {
                Reply(550, "The top folder cannot be removed.");
                return;
            }

            try
            {
                FileSystem.DeleteDirectory(ToReal(target));
            }
            catch (Exception error)
            {
                Reply(550, $"'{target}' could not be removed: {ExplorerShell.Describe(error)}");
                return;
            }
            Reply(250, $"'{target}' removed.");
            _service.Record($"{_remote} removed the folder {target}.");
        }

        private void HandleMakeDirectory(string argument)
        {
            if (argument.Length == 0)
            {
                Reply(501, "MKD needs a name.");
                return;
            }

            string target = FtpService.NormalizeVirtual(_directory, argument);
            try
            {
                FileSystem.CreateDirectory(ToReal(target));
            }
            catch (Exception error)
            {
                Reply(550, $"'{target}' could not be created: {ExplorerShell.Describe(error)}");
                return;
            }
            Reply(257, $"\"{Quote(target)}\" created.");
            _service.Record($"{_remote} created the folder {target}.");
        }

        private void HandleRenameFrom(string argument)
        {
            if (!NamesSomething("RNFR", argument))
                return;

            string target = FtpService.NormalizeVirtual(_directory, argument);
            string real = ToReal(target);
            if (!FileSystem.Exists(real))
            {
                Reply(550, $"'{target}' does not exist.");
                return;
            }
            _renameFrom = real;
            Reply(350, "Send RNTO with the new name.");
        }

        private void HandleRenameTo(string argument)
        {
            if (_renameFrom is null)
            {
                Reply(503, "Send RNFR first.");
                return;
            }
            if (!NamesSomething("RNTO", argument))
                return;

            string from = _renameFrom;
            _renameFrom = null;
            string target = FtpService.NormalizeVirtual(_directory, argument);
            try
            {
                FileSystem.Move(from, ToReal(target));
            }
            catch (Exception error)
            {
                Reply(550, $"The rename failed: {ExplorerShell.Describe(error)}");
                return;
            }
            Reply(250, $"Renamed to '{target}'.");
            _service.Record($"{_remote} renamed a file to {target}.");
        }

        private void HandleSize(string argument)
        {
            if (!NamesSomething("SIZE", argument))
                return;

            string target = FtpService.NormalizeVirtual(_directory, argument);
            if (!FtpFile.TryStatus(ToReal(target), out FtpFileStatus status))
            {
                Reply(550, $"'{target}' does not exist.");
                return;
            }
            if (status.IsDirectory)
            {
                Reply(550, $"'{target}' is a folder.");
                return;
            }
            Reply(213, status.Size.ToString());
        }

        private void HandleModifiedTime(string argument)
        {
            if (!NamesSomething("MDTM", argument))
                return;

            string target = FtpService.NormalizeVirtual(_directory, argument);
            if (!FtpFile.TryStatus(ToReal(target), out FtpFileStatus status))
            {
                Reply(550, $"'{target}' does not exist.");
                return;
            }
            DateTime when = FromUnixSeconds(status.ModifiedSeconds);
            Reply(213, $"{when.Year:0000}{Pad2(when.Month)}{Pad2(when.Day)}{Pad2(when.Hour)}{Pad2(when.Minute)}{Pad2(when.Second)}");
        }

        private void HandleRestart(string argument)
        {
            if (!long.TryParse(argument, out long offset) || offset < 0)
            {
                Reply(501, "REST needs a byte offset.");
                return;
            }
            _restartOffset = offset;
            Reply(350, $"Restarting at {offset}; send RETR, STOR or APPE next.");
        }

        /// <summary>Takes the offset REST left for the next transfer and disarms it.</summary>
        /// <remarks>
        /// A transfer reaches this only once it has passed the checks that would turn it away for a
        /// recoverable reason: no data connection is open, one is already running, or no name was given.
        /// A command refused for one of those never gets here, so the offset stays armed and a client
        /// that set it before opening the data connection can open one and try the transfer again
        /// without setting it afresh. The offset is spent by the first transfer that does go ahead - the
        /// one REST is meant for - so it is never applied twice.
        /// </remarks>
        private long TakeRestartOffset()
        {
            long offset = _restartOffset;
            _restartOffset = 0;
            return offset;
        }

        private void HandleStatus(string argument)
        {
            if (argument.Length == 0)
            {
                AppSettings settings = _service._settings;
                ReplyLines(211, "Prospero Explorer file service",
                [
                    $"Connected from {_remote}",
                    _authenticated ? $"Logged in as {(_named.Length == 0 ? "anonymous" : _named)}" : "Not logged in",
                    $"Current folder {_directory}",
                    $"Type {(_binary ? "binary" : "text")}",
                    settings.FtpAllowWrite ? "Writing allowed" : "Read-only",
                    _transfer == TransferKind.None
                        ? "No transfer running"
                        : $"Transferring {_transferName} ({TextFormat.ByteSize(_transferred)} so far)",
                ], "End of status");
                return;
            }

            string target = FtpService.NormalizeVirtual(_directory, StripListOptions(argument));
            string real = ToReal(target);
            var lines = new List<string>();
            DateTime now = NowOrDefault();
            try
            {
                if (FileSystem.IsDirectory(real))
                {
                    var entries = new List<DirectoryEntry>();
                    bool whole = ReadStatusEntries(real, entries);
                    entries.Sort(static (left, right) => TextFormat.CompareNatural(left.Name, right.Name));
                    int shown = Math.Min(entries.Count, StatusLineLimit);
                    for (int i = 0; i < shown; i++)
                        lines.Add(FormatListLine(real, entries[i], now));
                    if (!whole)
                        lines.Add("... more follow; use LIST for all of them");
                    else if (entries.Count > shown)
                        lines.Add($"... {entries.Count - shown} more; use LIST for all of them");
                }
                else if (FileSystem.Exists(real))
                {
                    lines.Add(FormatListLine(ParentOf(real), new DirectoryEntry { Name = NameOf(target), Type = FileEntryType.File }, now));
                }
                else
                {
                    Reply(550, $"'{target}' does not exist.");
                    return;
                }
            }
            catch (Exception error)
            {
                Reply(550, $"'{target}' could not be read: {ExplorerShell.Describe(error)}");
                return;
            }

            ReplyLines(213, $"Status of '{target}':", lines, "End of status");
        }

        /// <summary>How many lines a status listing shows before it points the client at LIST.</summary>
        private const int StatusLineLimit = 200;

        /// <summary>
        /// Reads only as much of a folder as a status reply can show, returning false when the folder
        /// holds more. STAT answers on the control connection inside the frame that asked for it, so it
        /// cannot walk a folder of any size; LIST is the command that does that, and it does it across
        /// frames.
        /// </summary>
        private bool ReadStatusEntries(string directory, List<DirectoryEntry> entries)
        {
            int descriptor = FtpFile.Open(directory, KernelFile.ReadOnly | KernelFile.Directory);
            try
            {
                while (entries.Count <= StatusLineLimit)
                {
                    if (!FtpFile.ReadEntries(descriptor, _service._scratch, ListBatchBytes, entries))
                        return true;
                }
                return false;
            }
            finally
            {
                FtpFile.Close(descriptor);
            }
        }

        // Paths and formatting.

        private string ToReal(string virtualPath)
        {
            if (_root.Length == 0)
                return virtualPath;
            // The top of the client's space is the root folder itself, not the root with a separator
            // hung off it.
            return virtualPath == "/" ? _root : _root + virtualPath;
        }

        private static string NameOf(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash < 0 ? path : path[(slash + 1)..];
        }

        private static string ParentOf(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash <= 0 ? "/" : path[..slash];
        }

        private static string Quote(string path) => path.Replace("\"", "\"\"");

        // A reply is one line, so anything a failure message carries that would break the line out is
        // flattened before it goes on the wire.
        private static string OneLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ');

        private static DateTime NowOrDefault()
        {
            try
            {
                return SystemClock.UtcNow;
            }
            catch (ProsperoException)
            {
                return DateTime.UnixEpoch;
            }
        }

        private static DateTime FromUnixSeconds(long seconds)
            => DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(seconds, 0, 253402300799L)).UtcDateTime;

        private static string FormatListLine(string directory, DirectoryEntry entry, DateTime now)
        {
            bool isDirectory = entry.IsDirectory;
            long size = 0;
            long modified = 0;
            ushort mode = 0;
            if (FtpFile.TryStatus(FtpService.JoinPath(directory, entry.Name), out FtpFileStatus status))
            {
                isDirectory = status.IsDirectory;
                size = status.Size;
                modified = status.ModifiedSeconds;
                mode = status.Mode;
            }

            string permissions = Permissions(mode, isDirectory);
            int links = isDirectory ? 2 : 1;
            return $"{permissions} {links,3} {"user",-8} {"group",-8} {size,12} {ListDate(modified, now)} {entry.Name}";
        }

        // The line a client parses is the long form a shell listing produces: kind and permissions,
        // link count, owner, group, length, date and name.
        private static string Permissions(ushort mode, bool isDirectory)
        {
            // A status that could not be read leaves the bits at zero; the usual defaults read better
            // in a client than a row of dashes and mislead nobody, since the service decides access.
            if ((mode & 0x1FF) == 0)
                mode = (ushort)(isDirectory ? 0x1ED : 0x1A4);

            Span<char> text = stackalloc char[10];
            text[0] = isDirectory ? 'd' : '-';
            const string Letters = "rwx";
            for (int group = 0; group < 3; group++)
            {
                for (int bit = 0; bit < 3; bit++)
                {
                    int mask = 1 << (8 - ((group * 3) + bit));
                    text[1 + (group * 3) + bit] = (mode & mask) != 0 ? Letters[bit] : '-';
                }
            }
            return new string(text);
        }

        private static string ListDate(long unixSeconds, DateTime now)
        {
            DateTime when = FromUnixSeconds(unixSeconds);
            string month = MonthNames[when.Month - 1];
            string day = when.Day < 10 ? " " + when.Day.ToString() : when.Day.ToString();

            // Within the last half year a client expects the time of day; older than that, the year.
            bool recent = when > now.AddDays(-180) && when <= now.AddDays(1);
            return recent
                ? $"{month} {day} {Pad2(when.Hour)}:{Pad2(when.Minute)}"
                : $"{month} {day}  {when.Year:0000}";
        }
    }
}

// Prospero Explorer
// Copyright (C) 2026 SvenGDK

using SharpProspero.Application;
using SharpProspero.Platform;
using System;
using System.Buffers.Binary;

namespace ProsperoExplorer.Shell;

/// <summary>
/// A single call to the companion daemon that asks it to widen this process's filesystem view.
/// </summary>
/// <remarks>
/// The daemon accepts a connection on a fixed loopback port, reads one twenty-five-hundred and
/// seventy-six byte request, applies the credential and filesystem rewrite the request names,
/// and replies with the same struct carrying an outcome slot at offset twelve. The wire format
/// is deliberately fixed and reserves two large trailing buffers whose presence the daemon's
/// reader relies on. Nothing about the transport touches the sandbox filesystem; the daemon
/// dropping the connection or refusing the request is reported as a failure without touching
/// the module's state.
/// </remarks>
internal static class UnjailRequest
{
    private const int DaemonPort = 9069;
    private const int CommandSize = 0xA10;
    private const uint Magic = 0xDEADBEEF;
    private const int EscalationCommand = 5;
    private const int ReceiveTimeoutMicroseconds = 3_000_000;

    /// <summary>Sends the request and returns true when the daemon reports success.</summary>
    /// <param name="failure">On failure, a short reason for the caller to surface.</param>
    public static bool TryRequest(out string failure)
    {
        try
        {
            using var conn = TcpConnection.Connect(SocketAddress.Loopback(DaemonPort));

            Span<byte> request = stackalloc byte[CommandSize];
            request.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(request, Magic);
            BinaryPrimitives.WriteInt32LittleEndian(request.Slice(4), EscalationCommand);
            BinaryPrimitives.WriteInt32LittleEndian(request.Slice(8), ProcessInfo.Id);

            conn.SendAll(request);

            conn.SetReceiveTimeout(ReceiveTimeoutMicroseconds);

            Span<byte> reply = stackalloc byte[CommandSize];
            int total = 0;
            while (total < CommandSize)
            {
                int n = conn.Receive(reply.Slice(total));
                if (n <= 0)
                    break;
                total += n;
            }

            if (total < 16)
            {
                failure = "The daemon closed the connection before answering.";
                return false;
            }

            int outcome = BinaryPrimitives.ReadInt32LittleEndian(reply.Slice(0x0C));
            if (outcome != 0)
            {
                failure = "The daemon refused the request.";
                return false;
            }

            failure = string.Empty;
            return true;
        }
        catch (Exception error)
        {
            failure = error.Message;
            return false;
        }
    }
}

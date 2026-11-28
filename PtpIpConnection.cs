using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CameraInspector;

internal sealed class PtpIpConnection : IDisposable
{
    internal const ushort Ok = 0x2001;
    internal const int Port = 15740;
    private const int MaxData = 1024 * 1024;
    private const uint InitCommand = 1, CommandAck = 2, InitEvent = 3, EventAck = 4;
    private const uint Operation = 6, Response = 7, Event = 8, StartData = 9, Data = 10, EndData = 12;
    private const uint Probe = 13, ProbeReply = 14;

    private readonly TcpClient _command = new(AddressFamily.InterNetwork) { NoDelay = true };
    private readonly TcpClient _event = new(AddressFamily.InterNetwork) { NoDelay = true };
    private uint _transaction;
    private bool _usable;

    internal async Task InitializeAsync(IPEndPoint endpoint, Guid clientId, CancellationToken cancellationToken)
    {
        using (var connect = Deadline(cancellationToken, 5))
            await _command.ConnectAsync(endpoint, connect.Token).ConfigureAwait(false);

        // Canon can wait for confirmation on the camera before acknowledging the first connection.
        using var pairing = Deadline(cancellationToken, 40);
        byte[] name = Encoding.Unicode.GetBytes("Camera Inspector\0");
        var init = new byte[16 + name.Length + 4];
        clientId.TryWriteBytes(init, bigEndian: true, out _);
        name.CopyTo(init, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(init.AsSpan(init.Length - 4), 0x00010000);
        var command = _command.GetStream();
        await WriteAsync(command, InitCommand, init, pairing.Token).ConfigureAwait(false);
        var (type, reply) = await ReadAsync(command, pairing.Token).ConfigureAwait(false);
        if (type != CommandAck || reply.Length < 26)
            throw new IOException("The camera did not accept the PTP/IP command connection.");

        using var events = Deadline(cancellationToken, 5);
        await _event.ConnectAsync(endpoint, events.Token).ConfigureAwait(false);
        var eventStream = _event.GetStream();
        await WriteAsync(eventStream, InitEvent, reply.AsMemory(0, 4), events.Token).ConfigureAwait(false);
        (type, reply) = await ReadAsync(eventStream, events.Token).ConfigureAwait(false);
        if (type != EventAck || reply.Length != 0)
            throw new IOException("The camera did not accept the PTP/IP event connection.");
        _usable = true;
    }

    internal async Task ListenForEventsAsync(CancellationToken cancellationToken)
    {
        var stream = _event.GetStream();
        try
        {
            while (true)
            {
                var (type, _) = await ReadAsync(stream, cancellationToken).ConfigureAwait(false);
                if (type == Probe)
                    await WriteAsync(stream, ProbeReply, default, cancellationToken).ConfigureAwait(false);
                else if (type != Event)
                    return;
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or SocketException)
        {
            Debug.WriteLine(error.Message);
        }
    }

    internal async Task<(byte[]? Data, ushort Code)> ExecuteAsync(ushort operation, CancellationToken cancellationToken,
        params uint[] arguments)
    {
        if (!_usable)
            throw new IOException("The PTP/IP command connection is unavailable.");

        using var deadline = Deadline(cancellationToken, 5);
        var token = deadline.Token;
        // The pre-session GetDeviceInfo and OpenSession both use transaction zero.
        uint transaction = operation == 0x1002 ? 0 : _transaction++;
        if (operation == 0x1002)
            _transaction = 1;
        var request = new byte[10 + arguments.Length * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(request, 1); // No outbound data phase.
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(4), operation);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(6), transaction);
        for (int index = 0; index < arguments.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(10 + index * 4), arguments[index]);

        var stream = _command.GetStream();
        try
        {
            await WriteAsync(stream, Operation, request, token).ConfigureAwait(false);
            var (type, payload) = await ReadCommandAsync(stream, token).ConfigureAwait(false);
            byte[]? result = null;
            if (type == StartData)
            {
                if (payload.Length != 12 || BinaryPrimitives.ReadUInt32LittleEndian(payload) != transaction ||
                    BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(4)) > MaxData)
                    throw new IOException("Invalid PTP/IP data header.");
                result = new byte[(int)BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(4))];
                int received = 0;
                do
                {
                    (type, payload) = await ReadCommandAsync(stream, token).ConfigureAwait(false);
                    if (type is not (Data or EndData) || payload.Length < 4 ||
                        BinaryPrimitives.ReadUInt32LittleEndian(payload) != transaction || payload.Length - 4 > result.Length - received)
                        throw new IOException("Invalid PTP/IP data packet.");
                    payload.AsSpan(4).CopyTo(result.AsSpan(received));
                    received += payload.Length - 4;
                } while (type != EndData);
                if (received != result.Length)
                    throw new IOException("The camera sent incomplete PTP/IP data.");
                (type, payload) = await ReadCommandAsync(stream, token).ConfigureAwait(false);
            }

            if (type != Response || payload.Length is < 6 or > 26 || (payload.Length - 6) % 4 != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(2)) != transaction)
                throw new IOException("Invalid PTP/IP operation response.");
            ushort code = BinaryPrimitives.ReadUInt16LittleEndian(payload);
            return (code == Ok ? result : null, code);
        }
        catch
        {
            _usable = false;
            throw;
        }
    }

    internal async Task TryCleanupAsync(ushort operation, params uint[] arguments)
    {
        if (!_usable)
            return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { await ExecuteAsync(operation, deadline.Token, arguments).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or OperationCanceledException or SocketException)
        {
            Debug.WriteLine(error.Message);
        }
    }

    private static CancellationTokenSource Deadline(CancellationToken token, int seconds)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        return deadline;
    }

    private static async Task<(uint Type, byte[] Payload)> ReadCommandAsync(NetworkStream stream, CancellationToken token)
    {
        var packet = await ReadAsync(stream, token).ConfigureAwait(false);
        while (packet.Type == Probe)
        {
            await WriteAsync(stream, ProbeReply, default, token).ConfigureAwait(false);
            packet = await ReadAsync(stream, token).ConfigureAwait(false);
        }
        return packet;
    }

    private static async Task<(uint Type, byte[] Payload)> ReadAsync(NetworkStream stream, CancellationToken token)
    {
        var header = new byte[8];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is < 8 or > MaxData + 12)
            throw new IOException("Invalid PTP/IP packet length.");
        var payload = new byte[(int)length - header.Length];
        await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        return (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)), payload);
    }

    private static Task WriteAsync(NetworkStream stream, uint type, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        var packet = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(packet, packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), type);
        payload.CopyTo(packet.AsMemory(8));
        return stream.WriteAsync(packet, token).AsTask();
    }

    public void Dispose()
    {
        _event.Dispose();
        _command.Dispose();
    }
}

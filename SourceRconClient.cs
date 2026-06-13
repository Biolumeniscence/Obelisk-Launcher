using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace ObeliskLauncher;

public sealed class SourceRconClient
{
    private const int AuthPacketType = 3;
    private const int ExecPacketType = 2;
    private const int ResponsePacketType = 0;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(3);
    private static readonly Encoding PacketEncoding = Encoding.UTF8;

    public async Task<string> SendCommandAsync(
        string host,
        int port,
        string password,
        string command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("RCON host is empty.", nameof(host));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "RCON port must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("RCON password is empty.", nameof(password));
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("RCON command is empty.", nameof(command));
        }

        using var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken)
            .AsTask()
            .WaitAsync(ConnectTimeout, cancellationToken);

        await using var stream = client.GetStream();
        await WritePacketAsync(stream, 1, AuthPacketType, password, cancellationToken);
        await AuthenticateAsync(stream, cancellationToken);

        await WritePacketAsync(stream, 2, ExecPacketType, command.Trim(), cancellationToken);
        await WritePacketAsync(stream, 3, ResponsePacketType, string.Empty, cancellationToken);

        return await ReadCommandResponseAsync(stream, cancellationToken);
    }

    private static async Task AuthenticateAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + ReadTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var packet = await ReadPacketAsync(stream, cancellationToken);
            if (packet.Id == -1)
            {
                throw new InvalidOperationException("RCON password was rejected.");
            }

            if (packet.Id == 1 && packet.Type != ResponsePacketType)
            {
                return;
            }
        }

        throw new TimeoutException("RCON authentication did not answer in time.");
    }

    private static async Task<string> ReadCommandResponseAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var response = new StringBuilder();
        var deadline = DateTime.UtcNow + ReadTimeout;

        while (DateTime.UtcNow < deadline)
        {
            RconPacket packet;
            try
            {
                packet = await ReadPacketAsync(stream, cancellationToken);
            }
            catch (TimeoutException)
            {
                break;
            }

            if (packet.Id == 3)
            {
                break;
            }

            if (packet.Id == 2 && packet.Body.Length > 0)
            {
                response.Append(packet.Body);
            }
        }

        return response.Length == 0 ? "Команда отправлена. Сервер не вернул текстовый ответ." : response.ToString();
    }

    private static async Task WritePacketAsync(
        NetworkStream stream,
        int id,
        int type,
        string body,
        CancellationToken cancellationToken)
    {
        var bodyBytes = PacketEncoding.GetBytes(body);
        var packet = new byte[4 + 4 + 4 + bodyBytes.Length + 2];
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), packet.Length - 4);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), type);
        bodyBytes.CopyTo(packet.AsSpan(12));

        await stream.WriteAsync(packet.AsMemory(), cancellationToken)
            .AsTask()
            .WaitAsync(ReadTimeout, cancellationToken);
    }

    private static async Task<RconPacket> ReadPacketAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var sizeBuffer = new byte[4];
        await ReadExactAsync(stream, sizeBuffer, cancellationToken);
        var packetSize = BinaryPrimitives.ReadInt32LittleEndian(sizeBuffer);
        if (packetSize is < 10 or > 4096)
        {
            throw new InvalidOperationException($"RCON returned invalid packet size: {packetSize}.");
        }

        var payload = new byte[packetSize];
        await ReadExactAsync(stream, payload, cancellationToken);

        var id = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4));
        var type = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4, 4));
        var bodyLength = Math.Max(0, payload.Length - 10);
        var body = PacketEncoding.GetString(payload, 8, bodyLength);
        return new RconPacket(id, type, body);
    }

    private static async Task ReadExactAsync(
        NetworkStream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken)
                .AsTask()
                .WaitAsync(ReadTimeout, cancellationToken);

            if (read == 0)
            {
                throw new IOException("RCON connection was closed.");
            }

            offset += read;
        }
    }

    private sealed record RconPacket(int Id, int Type, string Body);
}

using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace ObeliskLauncher;

public sealed record SteamServerInfo(string Name, string Map, int Players, int MaxPlayers, string Endpoint);

public sealed class SteamServerQueryClient
{
    private static readonly byte[] A2sInfoPrefix =
    [
        0xFF, 0xFF, 0xFF, 0xFF, 0x54
    ];

    private static readonly byte[] A2sInfoPayload = Encoding.ASCII.GetBytes("Source Engine Query\0");

    public async Task<SteamServerInfo?> QueryInfoAsync(int queryPort, CancellationToken cancellationToken)
    {
        var hosts = await Task.Run(EnumerateLocalQueryHostsSafe, cancellationToken);
        foreach (var host in hosts)
        {
            var info = await QueryInfoAsync(host, queryPort, cancellationToken);
            if (info is not null)
            {
                return info;
            }
        }

        return null;
    }

    public async Task<SteamServerInfo?> QueryInfoAsync(string host, int queryPort, CancellationToken cancellationToken)
    {
        if (queryPort is < 1 or > 65535 || !IPAddress.TryParse(host, out var address))
        {
            return null;
        }

        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Connect(address, queryPort);

            await SendInfoRequestAsync(udp, null, cancellationToken);
            var response = await ReceiveAsync(udp, cancellationToken);
            if (response is null)
            {
                return null;
            }

            if (IsChallengeResponse(response, out var challenge))
            {
                await SendInfoRequestAsync(udp, challenge, cancellationToken);
                response = await ReceiveAsync(udp, cancellationToken);
            }

            return response is null ? null : TryParseInfo(response, $"{host}:{queryPort}");
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task SendInfoRequestAsync(UdpClient udp, int? challenge, CancellationToken cancellationToken)
    {
        var length = A2sInfoPrefix.Length + A2sInfoPayload.Length + (challenge.HasValue ? 4 : 0);
        var request = new byte[length];
        A2sInfoPrefix.CopyTo(request, 0);
        A2sInfoPayload.CopyTo(request, A2sInfoPrefix.Length);

        if (challenge.HasValue)
        {
            BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(^4), challenge.Value);
        }

        await udp.SendAsync(request, cancellationToken);
    }

    private static async Task<byte[]?> ReceiveAsync(UdpClient udp, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            var result = await udp.ReceiveAsync(timeout.Token);
            return result.Buffer;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static bool IsChallengeResponse(byte[] response, out int challenge)
    {
        challenge = 0;

        if (response.Length < 9 || response[4] != 0x41)
        {
            return false;
        }

        challenge = BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(5, 4));
        return true;
    }

    private static SteamServerInfo? TryParseInfo(byte[] response, string endpoint)
    {
        if (response.Length < 6 || response[4] != 0x49)
        {
            return null;
        }

        var offset = 5;
        offset++; // protocol

        var name = ReadNullTerminatedString(response, ref offset);
        var map = ReadNullTerminatedString(response, ref offset);
        _ = ReadNullTerminatedString(response, ref offset); // folder
        _ = ReadNullTerminatedString(response, ref offset); // game

        if (offset + 7 > response.Length)
        {
            return null;
        }

        offset += 2; // app id
        var players = response[offset++];
        var maxPlayers = response[offset++];

        return new SteamServerInfo(name, map, players, maxPlayers, endpoint);
    }

    private static IEnumerable<string> EnumerateLocalQueryHosts()
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "127.0.0.1",
            "localhost"
        };

        foreach (var address in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
        {
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                hosts.Add(address.ToString());
            }
        }

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (var address in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    hosts.Add(address.Address.ToString());
                }
            }
        }

        foreach (var host in hosts)
        {
            yield return host;
        }
    }

    private static IReadOnlyList<string> EnumerateLocalQueryHostsSafe()
    {
        try
        {
            return EnumerateLocalQueryHosts().ToList();
        }
        catch
        {
            return ["127.0.0.1"];
        }
    }

    private static string ReadNullTerminatedString(byte[] buffer, ref int offset)
    {
        var start = offset;
        while (offset < buffer.Length && buffer[offset] != 0)
        {
            offset++;
        }

        var value = Encoding.UTF8.GetString(buffer, start, offset - start);

        if (offset < buffer.Length && buffer[offset] == 0)
        {
            offset++;
        }

        return value;
    }
}

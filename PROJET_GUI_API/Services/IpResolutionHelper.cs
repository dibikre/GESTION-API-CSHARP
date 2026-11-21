using System;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ApiTester.Services;

internal static class IpResolutionHelper
{
    private static readonly HttpClient ProbeClient = new()
    {
        Timeout = TimeSpan.FromMilliseconds(2500)
    };

    private static readonly string[] PublicIpEndpoints =
    [
        "https://api.ipify.org",
        "https://icanhazip.com"
    ];

    private static readonly Lock CacheLock = new();
    private static string? _cachedIp;
    private static DateTimeOffset _cacheExpiresAtUtc = DateTimeOffset.MinValue;

    public static bool ReturnExpressionMayUseIp(string? returnExpression)
    {
        if (string.IsNullOrWhiteSpace(returnExpression))
        {
            return false;
        }

        return returnExpression.Contains("ip", StringComparison.Ordinal);
    }

    public static async Task<string> ResolveIpAsync(CancellationToken ct = default)
    {
        lock (CacheLock)
        {
            if (_cachedIp is not null && DateTimeOffset.UtcNow < _cacheExpiresAtUtc)
            {
                return _cachedIp;
            }
        }

        string? resolvedPublic = null;
        foreach (var endpoint in PublicIpEndpoints)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(2500));

                var raw = await ProbeClient.GetStringAsync(endpoint, timeoutCts.Token).ConfigureAwait(false);
                var trimmed = raw.Trim();
                if (IPAddress.TryParse(trimmed, out var parsed))
                {
                    resolvedPublic = parsed.ToString();
                    break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Repli automatique sur l'endpoint suivant puis sur l'IP réseau locale.
            }
        }

        var finalIp = resolvedPublic ?? GetLocalOutboundIp();
        var ttl = resolvedPublic is not null
            ? TimeSpan.FromSeconds(30)
            : TimeSpan.FromSeconds(10);

        lock (CacheLock)
        {
            _cachedIp = finalIp;
            _cacheExpiresAtUtc = DateTimeOffset.UtcNow + ttl;
        }

        return finalIp;
    }

    public static string ResolveIpSync()
    {
        lock (CacheLock)
        {
            if (_cachedIp is not null && DateTimeOffset.UtcNow < _cacheExpiresAtUtc)
            {
                return _cachedIp;
            }
        }

        return Task.Run(() => ResolveIpAsync(CancellationToken.None)).GetAwaiter().GetResult();
    }

    internal static string GetLocalOutboundIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            if (socket.LocalEndPoint is IPEndPoint endPoint && !IPAddress.IsLoopback(endPoint.Address))
            {
                return endPoint.Address.ToString();
            }
        }
        catch
        {
            // Ignoré si aucune route sortante UDP n'est disponible.
        }

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(unicast.Address))
                    {
                        return unicast.Address.ToString();
                    }
                }
            }
        }
        catch
        {
            // Repli final sur loopback.
        }

        return "127.0.0.1";
    }
}

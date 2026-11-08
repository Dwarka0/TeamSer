using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TeamSpeakManager.Core.Services;

public sealed record LocalAddressInfo(string? Address, string? InterfaceName, bool IsPrivate);

public sealed record PublicAddressInfo(string? Address, bool IsCarrierGradeNat, string? Error);

/// <summary>
/// Read-only network diagnostics. Never mutates firewall or router state.
/// Public reachability is explicitly NOT inferred from having a public IP.
/// </summary>
public sealed class NetworkService
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    /// <summary>Pick a usable IPv4 address, preferring a non-virtual adapter with a gateway.</summary>
    public LocalAddressInfo GetLocalIPv4()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                            or NetworkInterfaceType.Wireless80211)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => new { n, a }))
                .Where(x => !IPAddress.IsLoopback(x.a.Address))
                // Exclude APIPA 169.254.0.0/16 (no DHCP lease), not just the .0.0 suffix.
                .Where(x => !(x.a.Address.GetAddressBytes()[0] == 169 && x.a.Address.GetAddressBytes()[1] == 254))
                .ToList();

            var best = candidates
                .OrderByDescending(x => x.n.GetIPProperties().GatewayAddresses.Count > 0)
                .ThenByDescending(x => x.a.Address.ToString().StartsWith("192.168."))
                .FirstOrDefault();

            if (best is null) return new LocalAddressInfo(null, null, false);

            var ip = best.a.Address.ToString();
            return new LocalAddressInfo(ip, best.n.Description, IsPrivate(ip));
        }
        catch (NetworkInformationException)
        {
            return new LocalAddressInfo(null, null, false);
        }
    }

    /// <summary>Public IPv4 via a configurable HTTPS endpoint. Failure is reported, never guessed.</summary>
    public async Task<PublicAddressInfo> GetPublicIPv4Async(string endpoint, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return new PublicAddressInfo(null, false, "No public IP endpoint configured.");

        try
        {
            var text = (await Http.GetStringAsync(endpoint, ct).ConfigureAwait(false)).Trim();
            if (!IPAddress.TryParse(text, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                return new PublicAddressInfo(null, false, "Endpoint did not return a valid IPv4 address.");

            var value = ip.ToString();
            return new PublicAddressInfo(value, IsCarrierGradeNat(value), null);
        }
        catch (HttpRequestException)
        {
            return new PublicAddressInfo(null, false, "Could not reach the public IP service.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new PublicAddressInfo(null, false, "Public IP lookup timed out.");
        }
        catch (InvalidOperationException)
        {
            return new PublicAddressInfo(null, false, "Malformed public IP endpoint URI.");
        }
    }

    /// <summary>RFC1918 / loopback / link-local.</summary>
    public static bool IsPrivate(string address) =>
        IPAddress.TryParse(address, out var ip) && IsPrivate(ip);

    public static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        var b = ip.GetAddressBytes();
        if (b.Length != 4) return false;
        return b[0] == 10
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>RFC 6598 shared address space 100.64.0.0/10 means the ISP is NATing (CGNAT).</summary>
    public static bool IsCarrierGradeNat(string address)
    {
        if (!IPAddress.TryParse(address, out var ip)) return false;
        var b = ip.GetAddressBytes();
        if (b.Length != 4) return false;
        return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }

    public static string FormatConnectionAddress(string host, int port) =>
        string.IsNullOrWhiteSpace(host) ? $"<your-ip>:{port}" : $"{host}:{port}";
}

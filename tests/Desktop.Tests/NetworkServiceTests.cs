using TeamSpeakManager.Core.Services;

namespace LTS.Tests;

public class NetworkServiceTests
{
    [Theory]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("192.168.1.10", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("172.15.0.1", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    public void IsPrivate_classifies_rfc1918(string address, bool expected)
    {
        Assert.Equal(expected, NetworkService.IsPrivate(address));
    }

    [Theory]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.255", true)]
    [InlineData("100.63.255.255", false)]
    [InlineData("100.128.0.1", false)]
    [InlineData("8.8.8.8", false)]
    public void IsCarrierGradeNat_classifies_rfc6598(string address, bool expected)
    {
        Assert.Equal(expected, NetworkService.IsCarrierGradeNat(address));
    }

    [Fact]
    public void Invalid_ip_is_never_private_or_cgnat()
    {
        Assert.False(NetworkService.IsPrivate("not-an-ip"));
        Assert.False(NetworkService.IsCarrierGradeNat("not-an-ip"));
    }

    [Fact]
    public void Public_ip_endpoint_failure_does_not_claim_a_value()
    {
        var net = new NetworkService();
        // Deliberately unreachable endpoint: result must carry an error, not an address.
        var result = net.GetPublicIPv4Async("https://nonexistent.invalid/ip").GetAwaiter().GetResult();
        Assert.Null(result.Address);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Empty_endpoint_is_reported_not_guessed()
    {
        var net = new NetworkService();
        var result = net.GetPublicIPv4Async("").GetAwaiter().GetResult();
        Assert.Null(result.Address);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void FormatConnectionAddress_includes_port()
    {
        Assert.Equal("192.168.1.5:9987", NetworkService.FormatConnectionAddress("192.168.1.5", 9987));
    }

    [Fact]
    public void Local_ip_is_reachable_or_absent_not_thrown()
    {
        var net = new NetworkService();
        var info = net.GetLocalIPv4();
        if (info.Address is not null)
        {
            Assert.True(System.Net.IPAddress.TryParse(info.Address, out var ip));
            Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, ip.AddressFamily);
        }
    }
}

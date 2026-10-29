using System.Net;
using Crawler.Worker.Crawling;
using FluentAssertions;

namespace Crawler.Worker.Tests;

public class SafeAddressPolicyTests
{
    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("2606:4700::1")]
    public void IsAllowed_PublicAddress_IsAllowed(string address)
        => SafeAddressPolicy.IsAllowed(IPAddress.Parse(address)).Should().BeTrue();

    [Theory]
    [InlineData("127.0.0.1")]          // loopback
    [InlineData("10.1.2.3")]           // private
    [InlineData("172.16.5.5")]         // private
    [InlineData("192.168.1.1")]        // private
    [InlineData("169.254.169.254")]    // link-local / cloud metadata
    [InlineData("100.64.0.1")]         // CGNAT
    [InlineData("0.0.0.0")]            // unspecified
    [InlineData("224.0.0.1")]          // multicast
    [InlineData("::1")]                // IPv6 loopback
    [InlineData("::")]                 // IPv6 unspecified
    [InlineData("fe80::1")]            // IPv6 link-local
    [InlineData("fc00::1")]            // IPv6 unique local
    [InlineData("ff02::1")]            // IPv6 multicast
    [InlineData("::ffff:127.0.0.1")]   // IPv4-mapped loopback
    [InlineData("::ffff:10.0.0.1")]    // IPv4-mapped private
    public void IsAllowed_NonPublicAddress_IsBlocked(string address)
        => SafeAddressPolicy.IsAllowed(IPAddress.Parse(address)).Should().BeFalse();

    [Theory]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    public void IsAllowed_JustOutsideBlockedRanges_IsAllowed(string address)
        => SafeAddressPolicy.IsAllowed(IPAddress.Parse(address)).Should().BeTrue();
}

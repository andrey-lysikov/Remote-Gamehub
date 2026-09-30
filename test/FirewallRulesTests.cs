//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using RemoteGameHub.App;
using Xunit;

namespace RemoteGameHub.Tests;

public class FirewallRulesTests
{
    [Fact]
    public void Rules_open_the_page_the_listeners_and_the_stream_ports()
    {
        var (tcp, udp) = FirewallRules.Ports(new AppConfig());

        Assert.Equal(new[] { 80, 47984, 47989, 48010 }, tcp);
        Assert.Equal(new[] { 47998, 47999, 48000 }, udp);
    }

    [Fact]
    public void Rules_follow_a_moved_port_base_and_page()
    {
        var (tcp, udp) = FirewallRules.Ports(new AppConfig { PortBase = 50000, WebPort = 8080 });

        Assert.Equal(new[] { 8080, 49995, 50000, 50021 }, tcp);
        Assert.Equal(new[] { 50009, 50010, 50011 }, udp);
    }

    [Fact]
    public void The_script_names_every_port()
    {
        var script = FirewallRules.EnsureScript(new[] { 80, 47989 }, new[] { 47998 });

        Assert.Contains("@('80','47989')", script);
        Assert.Contains("@('47998')", script);
        Assert.Contains("-Profile Domain,Private", script);
    }
}

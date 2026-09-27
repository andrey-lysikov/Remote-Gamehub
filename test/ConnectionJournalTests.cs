//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using RemoteGameHub.Protocol;
using Xunit;

namespace RemoteGameHub.Tests;

// When a device counts as connected, and when its requests are told as a new connection.
public class ConnectionJournalTests
{
    [Fact]
    public void A_device_connects_once_per_quiet_minute_and_is_connected_for_half_a_minute()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var journal = new ConnectionJournal { Clock = () => now };

        journal.Seen("abc", "192.168.1.5", "TV (192.168.1.5)");
        now = now.AddSeconds(5);
        journal.Seen("abc", "192.168.1.5", "TV (192.168.1.5)");

        Assert.True(journal.SeenRecently("abc"));
        Assert.Equal("192.168.1.5", journal.AddressOf("abc"));
        Assert.Single(journal.Contents().Split('\n'));
        Assert.EndsWith("TV (192.168.1.5) connected", journal.Contents());

        now = now.AddSeconds(31);
        Assert.False(journal.SeenRecently("abc"));

        now = now.AddMinutes(1);
        journal.Seen("abc", "192.168.1.5", "TV (192.168.1.5)");
        Assert.Equal(2, journal.Contents().Split('\n').Length);

        Assert.False(journal.SeenRecently("unknown"));
        Assert.Null(journal.AddressOf("unknown"));
    }
}

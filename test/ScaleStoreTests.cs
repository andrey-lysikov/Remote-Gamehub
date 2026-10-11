//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using RemoteGameHub.Library;
using RemoteGameHub.Media;
using Xunit;

namespace RemoteGameHub.Tests;

// What a stream changed on a screen and still owes it, kept where a shutdown cannot lose it.
public class ScaleStoreTests
{
    [Fact]
    public void The_first_hdr_switch_remembered_is_the_one_kept_until_it_is_forgotten()
    {
        using var folder = new TestFolder();
        using var database = Database.Open(folder.Path);
        var store = new ScaleStore(database);

        Assert.True(store.RememberHdr(@"\\.\DISPLAY1", true));

        // A later stream finds the screen as the first one left it; that is not the person's own.
        Assert.True(store.RememberHdr(@"\\.\DISPLAY1", false));
        Assert.Equal((@"\\.\DISPLAY1", true), Assert.Single(store.AllHdr()));

        store.ForgetHdr(@"\\.\DISPLAY1");
        Assert.Empty(store.AllHdr());
    }

    [Fact]
    public void A_scale_and_an_hdr_switch_of_one_screen_are_kept_apart()
    {
        using var folder = new TestFolder();
        using var database = Database.Open(folder.Path);
        var store = new ScaleStore(database);

        Assert.Equal(150, store.Remember(@"\\.\DISPLAY1", 150));
        Assert.False(store.RememberHdr(@"\\.\DISPLAY1", false));

        store.Forget(@"\\.\DISPLAY1");
        Assert.Empty(store.All());
        Assert.Single(store.AllHdr());
    }
}

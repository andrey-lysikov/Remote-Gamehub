//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using RemoteGameHub.Library;
using RemoteGameHub.Protocol;
using Xunit;

namespace RemoteGameHub.Tests;

// The backup of paired devices: what one host saved is what another, or the same one, gets back.
public class ClientBackupTests
{
    private sealed class Host : IDisposable
    {
        internal readonly TestFolder Folder = new();
        internal readonly Database Database;
        internal readonly ClientStore Clients;
        internal readonly HostIdentity Identity;

        internal Host()
        {
            Database = Database.Open(Folder.Path);
            Clients = new ClientStore(Database);
            Identity = HostIdentity.Load(Folder.Path, "Test host");
        }

        public void Dispose()
        {
            Database.Dispose();
            Folder.Dispose();
        }
    }

    [Fact]
    public void The_archive_holds_the_host_certificate_and_the_devices()
    {
        using var host = new Host();
        using var certificate = ClientStoreTests.ClientCertificate();
        host.Clients.Admit("a", "Phone", certificate);

        using var zip = new ZipArchive(new MemoryStream(ClientBackup.Pack(host.Identity, host.Clients)));

        Assert.Equal(new[] { ClientBackup.DevicesEntry, ClientBackup.HostEntry },
                     zip.Entries.Select(entry => entry.FullName).Order());
    }

    [Fact]
    public void A_new_host_takes_the_devices_and_the_certificate_of_the_old_one()
    {
        using var old = new Host();
        using var phone = ClientStoreTests.ClientCertificate();
        using var laptop = ClientStoreTests.ClientCertificate();
        old.Clients.Admit("a", "Phone", phone);
        old.Clients.Admit("b", "Laptop", laptop);

        using var fresh = new Host();
        var restored = ClientBackup.Restore(ClientBackup.Pack(old.Identity, old.Clients),
                                            fresh.Identity, fresh.Clients);

        Assert.Equal(new ClientBackup.Restored(2, true), restored);
        Assert.Equal("Phone", fresh.Clients.Find(phone)!.Name);
        Assert.Equal("Laptop", fresh.Clients.Find(laptop)!.Name);

        // The certificate is on disk for the next start, which then answers as the old host did.
        Assert.Equal(old.Identity.UniqueId, HostIdentity.Load(fresh.Folder.Path, "Test host").UniqueId);
    }

    [Fact]
    public void Restoring_a_hosts_own_backup_adds_only_what_was_forgotten()
    {
        using var host = new Host();
        using var kept = ClientStoreTests.ClientCertificate();
        using var forgotten = ClientStoreTests.ClientCertificate();
        host.Clients.Admit("a", "Kept", kept);
        host.Clients.Admit("b", "Forgotten", forgotten);

        var backup = ClientBackup.Pack(host.Identity, host.Clients);
        host.Clients.Forget(host.Clients.Find(forgotten)!.Id);

        Assert.Equal(new ClientBackup.Restored(1, false), ClientBackup.Restore(backup, host.Identity, host.Clients));
        Assert.Equal(2, host.Clients.Count());
    }

    [Fact]
    public void A_file_that_is_not_a_backup_changes_nothing()
    {
        using var host = new Host();
        var before = host.Identity.ReadPem();

        Assert.Null(ClientBackup.Restore(new byte[] { 1, 2, 3 }, host.Identity, host.Clients));

        using var packed = new MemoryStream();
        using (var zip = new ZipArchive(packed, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var entry = new StreamWriter(zip.CreateEntry(ClientBackup.HostEntry).Open()))
                entry.Write("not a certificate");
            using (var entry = new StreamWriter(zip.CreateEntry(ClientBackup.DevicesEntry).Open()))
                entry.Write("[]");
        }

        Assert.Null(ClientBackup.Restore(packed.ToArray(), host.Identity, host.Clients));
        Assert.Equal(before, host.Identity.ReadPem());
        Assert.Equal(0, host.Clients.Count());
    }
}

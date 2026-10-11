//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using RemoteGameHub.App;

namespace RemoteGameHub.Protocol;

// The paired devices and the certificate they know this host by, as one zip archive. Either
// half alone restores nothing: a client pairs with a certificate, not with a machine.
internal static class ClientBackup
{
    internal const string HostEntry = "host.pem";
    internal const string DevicesEntry = "devices.json";

    // Far more than either file can be; a larger entry is not one this server wrote.
    private const int MaxEntryBytes = 4 * 1024 * 1024;

    internal sealed record Restored(int Added, bool HostReplaced);

    internal static byte[] Pack(HostIdentity identity, ClientStore clients)
    {
        using var devices = new MemoryStream();
        using (var json = new Utf8JsonWriter(devices, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartArray();
            foreach (var device in clients.Export())
            {
                json.WriteStartObject();
                json.WriteString("unique_id", device.UniqueId);
                json.WriteString("name", device.Name);
                json.WriteString("certificate", device.Certificate);
                json.WriteString("paired_at", device.PairedAt);
                json.WriteEndObject();
            }

            json.WriteEndArray();
        }

        using var packed = new MemoryStream();
        using (var zip = new ZipArchive(packed, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, HostEntry, Encoding.UTF8.GetBytes(identity.ReadPem()));
            Write(zip, DevicesEntry, devices.ToArray());
        }

        return packed.ToArray();
    }

    // Null when the file is not such an archive; nothing is changed then.
    internal static Restored? Restore(byte[] archive, HostIdentity identity, ClientStore clients)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);

            var host = Read(zip, HostEntry);
            var devices = Read(zip, DevicesEntry);
            if (host is null || devices is null) return null;

            var listed = new List<ClientStore.Saved>();
            using (var json = JsonDocument.Parse(devices))
            {
                foreach (var device in json.RootElement.EnumerateArray())
                {
                    listed.Add(new ClientStore.Saved(
                        device.GetProperty("unique_id").GetString() ?? string.Empty,
                        device.GetProperty("name").GetString() ?? string.Empty,
                        device.GetProperty("certificate").GetString() ?? string.Empty,
                        device.GetProperty("paired_at").GetString() ?? string.Empty));
                }
            }

            // Checked before anything is written: half a backup restored is worse than none.
            if (!HostIdentity.IsUsable(host)) return null;

            var added = clients.Restore(listed);
            return new Restored(added, identity.Replace(host));
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or KeyNotFoundException
                                          or InvalidOperationException or IOException)
        {
            Log.Warn($"a backup of paired devices was not restored: {error.Message}");
            return null;
        }
    }

    private static void Write(ZipArchive zip, string name, byte[] content)
    {
        using var entry = zip.CreateEntry(name).Open();
        entry.Write(content);
    }

    private static string? Read(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry is null || entry.Length > MaxEntryBytes) return null;

        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

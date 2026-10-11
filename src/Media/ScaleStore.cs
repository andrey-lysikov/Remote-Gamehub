//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using RemoteGameHub.App;
using RemoteGameHub.Library;

namespace RemoteGameHub.Media;

// The desktop scale and the HDR switch a stream replaced, which Windows keeps past this process. A
// row lives until it is put back; one still there at a start is a restore that never happened.
internal sealed class ScaleStore
{
    private readonly Database _database;

    internal ScaleStore(Database database) => _database = database;

    // Every screen left scaled, as its \\.\DISPLAYn name and the percentage to go back to.
    internal IReadOnlyList<(string Device, int Percent)> All()
    {
        var scales = new List<(string, int)>();

        try
        {
            lock (_database.Gate)
            {
                using var command = _database.Command("SELECT device, percent FROM display_scales;");

                using var reader = command.ExecuteReader();
                while (reader.Read()) scales.Add((reader.GetString(0), reader.GetInt32(1)));
            }
        }
        catch (Exception error)
        {
            Log.Warn($"the desktop scales left by earlier streams could not be read ({error.Message})");
        }

        return scales;
    }

    // Records the scale to go back to unless one is already kept (never restored, so the person's).
    // Answers with whichever is kept, or the one given when nothing could be written.
    internal int Remember(string device, int percent)
    {
        try
        {
            lock (_database.Gate)
            {
                using (var insert = _database.Command(
                    "INSERT INTO display_scales (device, percent) VALUES ($device, $percent) " +
                    "ON CONFLICT (device) DO NOTHING;"))
                {
                    insert.Parameters.AddWithValue("$device", device);
                    insert.Parameters.AddWithValue("$percent", percent);
                    insert.ExecuteNonQuery();
                }

                using var select = _database.Command(
                    "SELECT percent FROM display_scales WHERE device = $device;");
                select.Parameters.AddWithValue("$device", device);

                return Convert.ToInt32(select.ExecuteScalar() ?? percent);
            }
        }
        catch (Exception error)
        {
            // The stream still restores it when it ends; only a shutdown in the middle loses it.
            Log.Info($"the desktop scale to go back to could not be written to the database: " +
                     error.Message);
            return percent;
        }
    }

    // Every screen whose HDR switch a stream left changed, and what it goes back to.
    internal IReadOnlyList<(string Device, bool Enabled)> AllHdr()
    {
        var states = new List<(string, bool)>();

        try
        {
            lock (_database.Gate)
            {
                using var command = _database.Command("SELECT device, enabled FROM display_hdr;");

                using var reader = command.ExecuteReader();
                while (reader.Read()) states.Add((reader.GetString(0), reader.GetInt32(1) != 0));
            }
        }
        catch (Exception error)
        {
            Log.Warn($"the HDR switches left by earlier streams could not be read ({error.Message})");
        }

        return states;
    }

    // Like Remember, for the HDR switch: the first one kept is the person's, and is the answer.
    internal bool RememberHdr(string device, bool enabled)
    {
        try
        {
            lock (_database.Gate)
            {
                using (var insert = _database.Command(
                    "INSERT INTO display_hdr (device, enabled) VALUES ($device, $enabled) " +
                    "ON CONFLICT (device) DO NOTHING;"))
                {
                    insert.Parameters.AddWithValue("$device", device);
                    insert.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
                    insert.ExecuteNonQuery();
                }

                using var select = _database.Command(
                    "SELECT enabled FROM display_hdr WHERE device = $device;");
                select.Parameters.AddWithValue("$device", device);

                return Convert.ToInt32(select.ExecuteScalar() ?? (enabled ? 1 : 0)) != 0;
            }
        }
        catch (Exception error)
        {
            Log.Info($"the HDR switch to go back to could not be written to the database: {error.Message}");
            return enabled;
        }
    }

    internal void ForgetHdr(string device)
    {
        try
        {
            lock (_database.Gate)
            {
                using var command = _database.Command("DELETE FROM display_hdr WHERE device = $device;");
                command.Parameters.AddWithValue("$device", device);
                command.ExecuteNonQuery();
            }
        }
        catch (Exception error)
        {
            Log.Info($"the HDR switch of {device} could not be removed from the database " +
                     $"({error.Message}); the next start finds it already in place");
        }
    }

    // The scale is back, or was never changed. A screen with no row is not a fault.
    internal void Forget(string device)
    {
        try
        {
            lock (_database.Gate)
            {
                using var command = _database.Command("DELETE FROM display_scales WHERE device = $device;");
                command.Parameters.AddWithValue("$device", device);
                command.ExecuteNonQuery();
            }
        }
        catch (Exception error)
        {
            Log.Info($"the desktop scale of {device} could not be removed from the database " +
                     $"({error.Message}); the next start finds it already in place");
        }
    }
}

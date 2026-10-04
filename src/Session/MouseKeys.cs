//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using RemoteGameHub.App;
using RemoteGameHub.Library;
using RemoteGameHub.Native;

namespace RemoteGameHub.Session;

// MouseKeys, on for a stream on a machine with no mouse: Windows then counts a pointing device,
// draws the pointer and hides it when a game does, and the duplication reports exactly that.
internal sealed class MouseKeys
{
    private readonly Database _database;

    internal MouseKeys(Database database) => _database = database;

    // Turns MouseKeys on when this machine has no mouse and it is off, keeping the flags it had.
    // Answers whether it did, so that the stream puts them back. Never throws.
    internal bool TurnOnForStream()
    {
        if (User32.GetSystemMetrics(User32.SM_MOUSEPRESENT) != 0) return false;

        if (!Read(out var keys))
        {
            Log.Warn("MouseKeys could not be read, so this machine with no mouse shows no pointer " +
                     $"in the stream (Win32 {Marshal.GetLastWin32Error()}).");
            return false;
        }

        if ((keys.Flags & User32.MKF_MOUSEKEYSON) != 0) return false;

        var original = keys.Flags;
        Remember(original);

        // The keypad moves the pointer only with NumLock off, its digits stay digits; no tray icon.
        keys.Flags = (original | User32.MKF_MOUSEKEYSON | User32.MKF_AVAILABLE) &
                     ~(User32.MKF_REPLACENUMBERS | User32.MKF_INDICATOR);

        if (!Write(keys))
        {
            Forget();
            Log.Warn("This machine has no mouse, and MouseKeys could not be switched on to make " +
                     $"Windows show a pointer (Win32 {Marshal.GetLastWin32Error()}). The stream " +
                     "carries no pointer; plugging in any mouse brings it back.");
            return false;
        }

        Log.Info("this machine has no mouse, so MouseKeys is on for the stream: Windows draws the " +
                 "pointer and hides it when a game does; the keypad moves it while NumLock is off");
        return true;
    }

    // Puts back the flags TurnOnForStream replaced. Never throws.
    internal void PutBack()
    {
        var original = Remembered();
        if (original is null) return;

        // Kept on failure, for the next start to try again.
        if (!Read(out var keys) || !Write(keys with { Flags = original.Value }))
        {
            Log.Warn("MouseKeys could not be switched back off after the stream " +
                     $"(Win32 {Marshal.GetLastWin32Error()}); signing out turns it off.");
            return;
        }

        Forget();
        Log.Info("MouseKeys is back as it was before the stream");
    }

    // At startup: a stream that never ended — the server stopped in the middle — left MouseKeys on.
    internal void RestoreLeftover()
    {
        if (Remembered() is null) return;

        Log.Info("a stream that did not end left MouseKeys on; it is put back");
        PutBack();
    }

    // Not written to the profile: the change lasts until sign-out at most, whatever happens here.
    private static bool Read(out User32.MouseKeysInfo keys)
    {
        keys = new User32.MouseKeysInfo { Size = (uint)Marshal.SizeOf<User32.MouseKeysInfo>() };
        return User32.SystemParametersInfo(User32.SPI_GETMOUSEKEYS, keys.Size, ref keys, 0);
    }

    private static bool Write(User32.MouseKeysInfo keys) =>
        User32.SystemParametersInfo(User32.SPI_SETMOUSEKEYS, keys.Size, ref keys, 0);

    private void Remember(uint flags)
    {
        try
        {
            lock (_database.Gate)
            {
                using var delete = _database.Command("DELETE FROM mouse_keys;");
                delete.ExecuteNonQuery();

                using var insert = _database.Command("INSERT INTO mouse_keys (flags) VALUES ($flags);");
                insert.Parameters.AddWithValue("$flags", (long)flags);
                insert.ExecuteNonQuery();
            }
        }
        catch (Exception error)
        {
            Log.Info($"the MouseKeys state to put back could not be written ({error.Message}); " +
                     "if the server stops mid-stream, signing out turns MouseKeys off");
        }
    }

    private uint? Remembered()
    {
        try
        {
            lock (_database.Gate)
            {
                using var select = _database.Command("SELECT flags FROM mouse_keys LIMIT 1;");
                return select.ExecuteScalar() is long flags ? (uint)flags : null;
            }
        }
        catch (Exception error)
        {
            Log.Info($"the MouseKeys state to put back could not be read ({error.Message})");
            return null;
        }
    }

    private void Forget()
    {
        try
        {
            lock (_database.Gate)
            {
                using var delete = _database.Command("DELETE FROM mouse_keys;");
                delete.ExecuteNonQuery();
            }
        }
        catch (Exception error)
        {
            Log.Info($"the MouseKeys state could not be cleared ({error.Message})");
        }
    }
}

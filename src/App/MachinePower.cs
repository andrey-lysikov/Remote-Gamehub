//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using RemoteGameHub.Native;

namespace RemoteGameHub.App;

// Restarting or switching off this machine, asked for from the page. The stream is ended by the
// usual shutdown notice (SessionWatch), not here.
internal static class MachinePower
{
    // True once Windows has accepted the request; a refusal is logged with what to do instead.
    internal static bool Initiate(bool restart)
    {
        var what = restart ? "restart" : "shut down";

        if (!Advapi32.EnablePrivilege(Advapi32.SE_SHUTDOWN_NAME))
        {
            Log.Warn($"this machine could not be {(restart ? "restarted" : "shut down")} from the page: " +
                     "the server does not hold the shutdown privilege. Run it as the service, " +
                     "or do it at the machine.");
            return false;
        }

        var flags = Advapi32.SHUTDOWN_FORCE_OTHERS | Advapi32.SHUTDOWN_FORCE_SELF |
                    (restart ? Advapi32.SHUTDOWN_RESTART : Advapi32.SHUTDOWN_POWEROFF);

        var error = Advapi32.InitiateShutdown(null, null, 0, flags, Advapi32.SHTDN_REASON_FLAG_PLANNED);
        if (error != 0)
        {
            Log.Warn($"Windows refused to {what} this machine: {new Win32Exception((int)error).Message} " +
                     $"(error {error}). Another shutdown may already be under way; otherwise do it at the machine.");
            return false;
        }

        Log.Event($"this machine is going to {what}, as asked for from the page");
        return true;
    }
}

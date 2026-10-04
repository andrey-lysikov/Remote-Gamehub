//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

namespace RemoteGameHub;

// Constants several classes share. One used by a single class lives in that class; anything a user
// may reasonably want to change belongs in the configuration file instead.
internal static class AppParameters
{
    internal static class Identity
    {
        // The product's one name, everywhere: the window, the files, the service, the task. Taken
        // from the assembly, which is named after the project file, so it is written down once.
        internal static readonly string Name = typeof(AppParameters).Assembly.GetName().Name!;

        // One file for the launcher, the worker and the service alike; the service's lines are
        // marked. Appends are atomic (see Log.Append), so the three do not lose each other's lines.
        internal static readonly string LogFile = Name + ".log";
        internal static readonly string ConfFile = Name + ".conf";

        // Folder created under %LOCALAPPDATA% for an installed copy, and the one the installer makes.
        internal static readonly string DataFolder = Name;

        // Single-instance mutex. Global, because the ports are machine-wide.
        internal static readonly string Mutex = $@"Global\{Name}.SingleInstance";

        // Set by the service to ask its worker to close the way Quit does. Global, because the
        // service is in session 0 and the worker is in the session it serves.
        internal static readonly string WorkerStopEvent = $@"Global\{Name}.WorkerStop";
    }

    // Where the project lives: shown on the page and checked for newer releases (UpdateChecker).
    internal static class Links
    {
        internal const string Project = "https://github.com/andrey-lysikov/Remote-Gamehub";
    }

    // The GameStream port layout Moonlight expects. The client is given one base port and derives
    // every other from it with these offsets, so the offsets are protocol, not preference.
    internal static class Ports
    {
        internal const int DefaultBase = 47989;

        internal const int HttpsOffset = -5;   // 47984, pairing and the paired queries
        internal const int HttpOffset = 0;     // 47989, unpaired discovery
        internal const int VideoOffset = 9;    // 47998/udp, RTP video
        internal const int ControlOffset = 10; // 47999/udp, ENet control channel
        internal const int AudioOffset = 11;   // 48000/udp, RTP audio
        internal const int RtspOffset = 21;    // 48010/tcp, session negotiation
    }

    // GameStream values more than one part of the server has to agree on.
    internal static class Protocol
    {
        // The identifier of the one thing this server offers: the desktop itself.
        internal const int DesktopAppId = 1;

        // A game's identifier over the protocol is not its row id but games.client_id, worked out
        // from its title and cover (GameLibrary.List), which never takes the desktop's number.
    }
}

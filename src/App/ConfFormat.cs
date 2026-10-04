//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

namespace RemoteGameHub.App;

// The one place that knows the name, the meaning and the wording of every setting. Adding one:
// a default on AppConfig, a line in Read, a note and a line in Write, the same text in sample.conf.
internal static class ConfFormat
{
    // Bounds for configured values. Out-of-range numbers are clamped and logged, never thrown:
    // a typo in a port must not keep the server from starting.
    internal const int MinPortBase = 1024;
    internal const int MaxPortBase = 65000;

    // The page's port may be one of the reserved ones — 80 is the point of it — which is why
    // this range starts lower than the block above.
    internal const int MinWebPort = 1;
    internal const int MaxWebPort = 65535;

    // Failed pairing attempts from one address outside this network before it is refused, and
    // how long the refusal lasts. Zero attempts is the switch that turns the whole thing off.
    internal const int MinBlockAfterFailures = 0;
    internal const int MaxBlockAfterFailures = 100;
    internal const int MinBlockMinutes = 1;
    internal const int MaxBlockMinutes = 24 * 60;

    // How deep the optional games folder may be walked. Past eight levels a mistyped path
    // pointed at a drive root would turn the scan into a disk crawl.
    internal const int MinGamesFolderDepth = 1;
    internal const int MaxGamesFolderDepth = 8;

    internal static AppConfig Read(ConfFile file, Action<string> warn)
    {
        var config = new AppConfig();

        config.Debug = file.Bool("General", "Debug", config.Debug);
        config.HostName = file.Text("General", "HostName", config.HostName);
        config.VirtualDisplay = file.Bool("General", "VirtualDisplay", config.VirtualDisplay);

        // [Display] Output until 2.2: read from there while [General] has none, so the file is
        // written again in the new shape with the screen the person chose, not "auto".
        var oldOutput = file.Has("Display", "Output")
            ? file.Text("Display", "Output", config.Output)
            : config.Output;
        config.Output = file.Text("General", "Output", oldOutput);

        config.PortBase = file.Number("Network", "PortBase", config.PortBase,
            MinPortBase, MaxPortBase, warn);
        config.BindAddress = file.Text("Network", "BindAddress", config.BindAddress);
        config.WebPort = file.Number("Network", "WebPort", config.WebPort,
            MinWebPort, MaxWebPort, warn);
        config.Upnp = file.Bool("Network", "Upnp", config.Upnp);
        config.Firewall = file.Bool("Network", "Firewall", config.Firewall);
        config.BlockAfterFailures = file.Number("Network", "BlockAfterFailures",
            config.BlockAfterFailures, MinBlockAfterFailures,
            MaxBlockAfterFailures, warn);
        config.BlockMinutes = file.Number("Network", "BlockMinutes", config.BlockMinutes,
            MinBlockMinutes, MaxBlockMinutes, warn);

        config.Steam = file.Bool("Games", "Steam", config.Steam);
        config.Xbox = file.Bool("Games", "Xbox", config.Xbox);
        config.Epic = file.Bool("Games", "Epic", config.Epic);
        config.Gog = file.Bool("Games", "Gog", config.Gog);
        config.Ea = file.Bool("Games", "Ea", config.Ea);
        config.BattleNet = file.Bool("Games", "BattleNet", config.BattleNet);

        config.GamesFolders = file.List("Games", "Folders", config.GamesFolders);
        config.GamesDepth = file.Number("Games", "Depth", config.GamesDepth,
            MinGamesFolderDepth, MaxGamesFolderDepth, warn);

        return config;
    }

    internal static string Write(AppConfig config)
    {
        var writer = new ConfFile.Writer();

        writer.Section("General");
        writer.Note("Write every step to the log, not only warnings and errors.");
        writer.Key("Debug", config.Debug);
        writer.Blank();
        writer.Note("The name Moonlight shows for this machine. \"auto\" uses the computer name.");
        writer.Key("HostName", config.HostName);
        writer.Blank();
        writer.Note("Prefer a virtual display driver over a real screen, when one is found. This server never\n" +
                    "installs one itself; the installer's own checkbox does, if you asked it to.");
        writer.Key("VirtualDisplay", config.VirtualDisplay);
        writer.Blank();
        writer.Note("Which screen to stream: \"auto\" for the one attached to the desktop, a screen's own\n" +
                    "number (shown in the log at startup) or a piece of its name for another.");
        writer.Key("Output", config.Output);

        writer.Section("Network");
        writer.Note("First port of the block Moonlight expects. Every other port is derived from it.");
        writer.Key("PortBase", config.PortBase);
        writer.Blank();
        writer.Note("Address to listen on. \"any\" accepts connections on every interface.");
        writer.Key("BindAddress", config.BindAddress);
        writer.Blank();
        writer.Note("The page that shows what this server is doing and pairs a client. Local network only.");
        writer.Key("WebPort", config.WebPort);
        writer.Blank();
        writer.Note("Ask the router, over UPnP, to forward the streaming ports from the internet.");
        writer.Key("Upnp", config.Upnp);
        writer.Blank();
        writer.Note("Add rules to Windows Firewall at every start that let clients reach the ports above,\n" +
                    "on private and domain networks. false leaves the firewall to you.");
        writer.Key("Firewall", config.Firewall);
        writer.Blank();
        writer.Note("While the ports above are forwarded, an address outside this network that fails to pair\n" +
                    "this many times is refused for the minutes below, to ban untrusted IP addresses.");
        writer.Key("BlockAfterFailures", config.BlockAfterFailures);
        writer.Key("BlockMinutes", config.BlockMinutes);

        writer.Section("Games");
        writer.Note("Which stores to look in for installed games.");
        writer.Key("Steam", config.Steam);
        writer.Key("Xbox", config.Xbox);
        writer.Key("Epic", config.Epic);
        writer.Key("Gog", config.Gog);
        writer.Key("Ea", config.Ea);
        writer.Key("BattleNet", config.BattleNet);
        writer.Blank();
        writer.Note("Folders of your own, separated by commas; a name with a comma goes in quotes or brackets:\n" +
                    "    Folders = D:\\Games, \"E:\\Discs, old\", [F:\\Emulators (2004)]");
        writer.Key("Folders", config.GamesFolders);
        writer.Blank();
        writer.Note("How many folder levels below each of those to look into. 1 is the folder itself.");
        writer.Key("Depth", config.GamesDepth);

        return writer.ToString();
    }
}

//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RemoteGameHub.App;
using RemoteGameHub.Media;
using RemoteGameHub.Library;
using RemoteGameHub.Session;

namespace RemoteGameHub.Protocol;

// One page at the machine's own address, showing what this server is doing and taking the four
// digits a pairing client shows. Its own port, private addresses only, and never forwarded.
internal sealed class WebConsole : IAsyncDisposable
{
    // How much of the log the page shows. Enough to cover a stream starting and failing, which is
    // what anyone opening this page is usually looking at, and little enough to send in one go.
    private const int LogTailBytes = 64 * 1024;

    private readonly AppConfig _config;
    private readonly HostIdentity _identity;
    private readonly PairingManager _pairing;
    private readonly ClientStore _clients;
    private readonly AccessGuard _guard;
    private readonly GameLibrary _games;
    private readonly SessionManager _sessions;
    private readonly GamepadHub _gamepads;
    private readonly EncoderCapabilities _encoder;
    private readonly UpdateChecker _updates;
    private readonly string _directory;
    private readonly CancellationTokenSource _stopping = new();

    private TcpListener? _listener;
    private Task? _accepting;

    // What the Rescan button asks for, set after construction because the page is opened before
    // the scan exists. Takes the reason for the log line and returns at once.
    internal Action<string>? Rescan { get; set; }

    internal WebConsole(AppConfig config, HostIdentity identity, PairingManager pairing,
                        ClientStore clients, AccessGuard guard, GameLibrary games,
                        SessionManager sessions, EncoderCapabilities encoder,
                        UpdateChecker updates, GamepadHub gamepads, string directory)
    {
        _config = config;
        _gamepads = gamepads;
        _identity = identity;
        _pairing = pairing;
        _clients = clients;
        _guard = guard;
        _games = games;
        _sessions = sessions;
        _encoder = encoder;
        _updates = updates;
        _directory = directory;
    }

    internal void Start()
    {
        try
        {
            var listener = new TcpListener(IPAddress.IPv6Any, _config.WebPort);
            listener.Server.DualMode = true;
            listener.Start();

            _listener = listener;
        }
        catch (Exception error)
        {
            // Never fatal: port 80 is the setting most likely to be taken by something else, and
            // without the page a client can still be paired and the log still opened.
            Log.Warn(
                $"The page on port {_config.WebPort} could not be opened: {error.Message}\n" +
                "Something else is probably using that port. Set [Network] WebPort to a free one —\n" +
                $"for example {_config.PortBase + AppParameters.Ports.WebOffset} — and restart.\n" +
                "Everything else works; only the page is missing.");
            return;
        }

        _accepting = Task.Run(AcceptLoop);
        Log.Info($"the page is at http://{LocalAddress(_config)}" +
                 (_config.WebPort == 80 ? string.Empty : $":{_config.WebPort}") + "/");
    }

    private async Task AcceptLoop()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error)
            {
                if (_stopping.IsCancellationRequested) return;
                Log.Warn($"the page stopped accepting connections: {error.Message}");
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                var peer = Peer.Plain((client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None);

                await using var stream = client.GetStream();

                // Refused before the request is read: this page pairs devices and shows the log,
                // and a refusal before parsing is one that cannot be talked around.
                if (!IsPrivate(peer))
                {
                    Log.Warn($"the page refused {Peer.Describe(peer)}: it is " +
                             "not on a private network. Only the streaming ports are meant to be " +
                             "reachable from outside.");

                    await WriteAsync(stream, 403, "text/plain",
                        Text.T("This page answers only on the local network."));
                    return;
                }

                var request = await HttpRequest.ReadAsync(stream, _stopping.Token);
                if (request is null) return;

                await RespondAsync(stream, request);
            }
        }
        catch (Exception error)
        {
            Log.Info($"a page request ended early ({error.GetType().Name}: {error.Message})");
        }
    }

    private async Task RespondAsync(Stream stream, HttpRequest request)
    {
        // Everything is at "/", asked for through the query string: one address to remember, and
        // one to keep off the internet.
        if (request.Query("pin") is { } pin)
        {
            var digits = pin.Length == 4 && pin.All(char.IsAsciiDigit);

            // The name is the person's to choose and is kept as typed, within reason: a name is
            // one line, and the list it appears in has room for about this much of one.
            var name = (request.Query("name") ?? string.Empty).Trim();
            if (name.Length > 48) name = name[..48];

            var answer = !digits
                ? Text.T("A code is four digits.")
                : _pairing.SupplyPin(pin, name)
                    ? Text.T("Sent. The client should finish pairing in a moment.")
                    : Text.T("Nothing is waiting for a code. Press Pair on the client and type the " +
                      "digits it shows then; the ones before are of no use now.");

            await WriteAsync(stream, 200, "text/plain", answer);
            return;
        }

        // Cancel, from the box that asks for the digits: the client waits five minutes for a PIN,
        // and there is no other way to end an attempt started by accident.
        if (request.Query("cancelpair") is not null)
        {
            await WriteAsync(stream, 200, "text/plain",
                _pairing.CancelWaiting()
                    ? Text.T("Pairing cancelled.")
                    : Text.T("Nothing is waiting to pair."));
            return;
        }

        // The same scan the tray menu asks for; it re-reads [Games] first, so a folder added to
        // the file is scanned without a restart. Returns at once.
        if (request.Query("rescan") is not null)
        {
            var rescan = Rescan;
            rescan?.Invoke("asked for from the page");

            await WriteAsync(stream, 200, "text/plain",
                rescan is null ? Text.T("The scan is not ready yet.") : Text.T("Scanning…"));
            return;
        }

        // Restart or switch off the machine. POST only, so a link or an image on another page the
        // browser has open cannot do it. Windows takes seconds to close things; the answer fits in them.
        if (request.Query("power") is "restart" or "shutdown" && request.Method == "POST")
        {
            var restart = request.Query("power") == "restart";
            var accepted = MachinePower.Initiate(restart);
            await WriteAsync(stream, 200, "text/plain", !accepted
                ? Text.T("Windows refused; the log says why.")
                : restart ? Text.T("Restarting…") : Text.T("Shutting down…"));
            return;
        }

        // Windows' own sign-in settings, opened on the machine's screen; and the state, for the
        // page to notice the switch being made there. The password never comes this way.
        if (request.Query("autologon") is { } ask)
        {
            await WriteAsync(stream, 200, "text/plain",
                ask == "setup" ? AutoLogon.OpenWindowsSettings() : AutoLogonState());
            return;
        }

        // The daily update check, asked for now from the header. A newer version found this way
        // also lands on the host line and in the tray, exactly as the daily one would put it.
        if (request.Query("checkupdate") is not null)
        {
            await WriteAsync(stream, 200, "text/plain", await _updates.CheckAsync() switch
            {
                UpdateChecker.Outcome.Newer => Text.T("v{0} is out", _updates.Newer),
                UpdateChecker.Outcome.Current => Text.T("Up to date"),
                _ => Text.T("Check failed"),
            });
            return;
        }

        if (request.Query("waiting") is not null)
        {
            await WriteAsync(stream, 200, "text/plain", _pairing.WaitingFor ?? string.Empty);
            return;
        }

        // Which of the two palettes the page wears: this machine's application theme rather than
        // the browser's, so it changes when that is flipped.
        if (request.Query("theme") is not null)
        {
            await WriteAsync(stream, 200, "text/plain", ThemeName());
            return;
        }

        // The header's two lines and which row is running, in one answer rather than three
        // requests a second: none of the three can contain a newline of its own.
        if (request.Query("status") is not null)
        {
            var runningId = RunningGameId();
            await WriteAsync(stream, 200, "text/html; charset=utf-8",
                HostLine() + "\n" + Status() + "\n" +
                (runningId != 0 ? runningId.ToString(CultureInfo.InvariantCulture) : string.Empty) + "\n" +
                Available());
            return;
        }

        // The screen beside the log, only while something runs: a game, a program or a stream.
        if (request.Query("screen") is not null)
        {
            var running = _sessions.CurrentAppId != 0;
            var shot = running ? ScreenSnapshot.Take(_config) : null;

            if (shot is null)
            {
                await WriteAsync(stream, 200, "text/plain", running
                    ? Text.T("The screen cannot be captured right now.")
                    : Text.T("Nothing is running."));
                return;
            }

            await HttpResponse.WriteImageAsync(stream, shot, "image/jpeg", _stopping.Token);
            return;
        }

        // What the last program without a stream wrote, from memory; a header line says which.
        if (request.Query("output") is not null)
        {
            var output = _sessions.ProgramOutput;
            await WriteAsync(stream, 200, "text/plain", output is not { } program
                ? Text.T("No program without a stream has been started yet.")
                : (program.Running ? Text.T("{0} — running", program.Title) : Text.T("{0} — stopped", program.Title)) +
                  "\n\n" +
                  (program.Text.Length > 0 ? program.Text : Text.T("It has written nothing yet.")));
            return;
        }

        if (request.Query("connections") is not null)
        {
            var journal = _guard.Journal.Contents();
            await WriteAsync(stream, 200, "text/plain",
                journal.Length > 0 ? journal : Text.T("Nothing has happened yet."));
            return;
        }

        if (request.Query("log") is not null)
        {
            await WriteAsync(stream, 200, "text/plain", ReadLogTail());
            return;
        }

        if (request.Query("games") is not null)
        {
            await WriteAsync(stream, 200, "text/html; charset=utf-8", GamesList());
            return;
        }

        // The picture itself, for the list. Served from here rather than from the protocol's own
        // box-art endpoint so that the page never has to know the offset the protocol adds.
        if (request.Query("cover") is { } coverId && long.TryParse(coverId, out var forCover))
        {
            var path = _games.BoxArtPath(forCover);
            if (path is null || !File.Exists(path))
            {
                await WriteAsync(stream, 404, "text/plain", "no cover");
                return;
            }

            var kind = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)
                ? "image/png"
                : "image/jpeg";

            await HttpResponse.WriteImageAsync(stream, await File.ReadAllBytesAsync(path, _stopping.Token),
                                               kind, _stopping.Token);
            return;
        }

        // The application's own icon, from inside the executable: the page has no folder of files
        // beside it, and one exe was promised.
        if (request.Query("icon") is not null)
        {
            await using var icon = System.Reflection.Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("RemoteGameHub.Icons.RemoteGameHub.ico");

            if (icon is null)
            {
                await WriteAsync(stream, 404, "text/plain", "no icon");
                return;
            }

            using var bytes = new MemoryStream();
            await icon.CopyToAsync(bytes, _stopping.Token);

            await HttpResponse.WriteImageAsync(stream, bytes.ToArray(), "image/x-icon",
                                               _stopping.Token);
            return;
        }

        if (request.Query("stop") == "1")
        {
            _sessions.Cancel();
            await WriteAsync(stream, 200, "text/plain", Text.T("Stopped."));
            return;
        }

        // A program without a stream, started on the host from here; nothing is streamed.
        if (request.Query("start") is { } startId && long.TryParse(startId, out var toStart))
        {
            await WriteAsync(stream, 200, "text/plain",
                _sessions.StartProgram(toStart, out var refusal) ? Text.T("Started.") : Text.T("Not started: {0}", refusal));
            return;
        }

        if (request.Query("remove") is { } removeId && long.TryParse(removeId, out var toRemove))
        {
            _games.Remove(toRemove);
            await WriteAsync(stream, 200, "text/plain", Text.T("Removed."));
            return;
        }

        // Back to what the store found, then the scan that writes it so. Returns at once, like
        // Rescan: the page fetches the list again when the scan has had time to finish.
        if (request.Query("reset") is { } resetId && long.TryParse(resetId, out var toReset))
        {
            var said = _games.Reset(toReset);
            if (said.StartsWith("Reset.", StringComparison.Ordinal)) Rescan?.Invoke("a game was reset");

            await WriteAsync(stream, 200, "text/plain", Text.T(said));
            return;
        }

        if (request.Query("save") is { } saveId && long.TryParse(saveId, out var toSave))
        {
            var title = (request.Query("title") ?? string.Empty).Trim();
            var command = (request.Query("command") ?? string.Empty).Trim();
            var folder = (request.Query("folder") ?? string.Empty).Trim();

            if (title.Length == 0 || command.Length == 0)
            {
                await WriteAsync(stream, 200, "text/plain",
                    Text.T("A game needs a name and something to start."));
                return;
            }

            if (_games.TitleTaken(toSave, title))
            {
                await WriteAsync(stream, 200, "text/plain",
                    Text.T("There is already a game named \"{0}\". Choose another name.", title));
                return;
            }

            // Save answers with the row's identifier, which is the new one when a game is being
            // added: the pointer switch belongs to that row and is written straight after.
            var saved = _games.Save(toSave, title, command, folder,
                (request.Query("args") ?? string.Empty).Trim(), request.Query("nostream") == "1");
            _games.RecordPointer(saved, request.Query("pointer") == "1");

            if (int.TryParse(request.Query("card"), out var splash) && Enum.IsDefined(typeof(SplashMode), splash))
                _games.RecordSplash(saved, (SplashMode)splash);

            if (int.TryParse(request.Query("quality"), out var level) &&
                Enum.IsDefined(typeof(StreamQuality), level))
            {
                _games.RecordQuality(saved, (StreamQuality)level);
            }

            // The row's number on a second line: a cover chosen while adding is sent to it next.
            await WriteAsync(stream, 200, "text/plain", Text.T("Saved.") + "\n" + saved);
            return;
        }

        // What the catalogue answers for a name, as data rather than drawn here: the page lays the
        // pictures out and fetches them from the catalogue's address, not through this server.
        if (request.Query("artlist") is not null)
        {
            var title = (request.Query("title") ?? string.Empty).Trim();
            var found = await CoverArt.SearchAllAsync(title, _stopping.Token);

            var json = "[" + string.Join(",", found.Select(c =>
                $"{{\"name\":{JsonText(c.Name)},\"src\":{JsonText(c.Address)}}}")) + "]";

            await WriteAsync(stream, 200, "application/json", json);
            return;
        }

        if (request.Query("upload") is { } uploadId && long.TryParse(uploadId, out var forUpload))
        {
            if (request.Body.Length == 0)
            {
                await WriteAsync(stream, 200, "text/plain", Text.T("Nothing arrived."));
                return;
            }

            var stored = CoverArt.Store(_games, forUpload, _directory, request.Body);
            await WriteAsync(stream, 200, "text/plain",
                stored ? Text.T("Uploaded.") : Text.T("That file is not a picture this machine can read."));
            return;
        }

        // A picture found in a browser, given as its address. The one answer to a game the search
        // does not know, which is most games that were never sold on Steam.
        if (request.Query("arturl") is { } urlId && long.TryParse(urlId, out var forUrl))
        {
            var address = (request.Query("url") ?? string.Empty).Trim();
            if (address.Length == 0)
            {
                await WriteAsync(stream, 200, "text/plain", Text.T("Paste the address of a picture."));
                return;
            }

            var said = await CoverArt.FetchFromUrlAsync(_games, forUrl, address, _directory,
                                                        _stopping.Token);
            await WriteAsync(stream, 200, "text/plain", said);
            return;
        }

        if (request.Query("clients") is not null)
        {
            await WriteAsync(stream, 200, "text/html; charset=utf-8", ClientsList());
            return;
        }

        if (request.Query("forget") is { } forgetId && long.TryParse(forgetId, out var toForget))
        {
            var gone = _clients.Forget(toForget);
            await WriteAsync(stream, 200, "text/plain",
                gone ? Text.T("Forgotten.") : Text.T("There is no such device."));
            return;
        }

        if (request.Query("blocked") is not null)
        {
            await WriteAsync(stream, 200, "text/html; charset=utf-8", BlockedList());
            return;
        }

        // The button beside a refused address. It takes the address rather than a row number:
        // there is no table of these that outlives the run, only the count itself.
        if (request.Query("unblock") is { } toRelease)
        {
            await WriteAsync(stream, 200, "text/plain",
                _guard.Release(toRelease)
                    ? Text.T("Let back in.")
                    : Text.T("That address is not being refused."));
            return;
        }

        await WriteAsync(stream, 200, "text/html; charset=utf-8", Page());
    }

    // The diagnostics tabs that have something to show, for the page to hide the rest.
    private string Available() => string.Join(",", new[]
    {
        _sessions.CurrentAppId != 0 ? "screen" : null,
        _sessions.ProgramOutputAvailable ? "output" : null,
    }.Where(name => name is not null));

    // Which row is streaming right now, found from the number the client started it by. Zero is
    // nothing or the desktop, and no tile carries that, so zero means none.
    private long RunningGameId()
    {
        var appId = _sessions.CurrentAppId;
        return appId is 0 or AppParameters.Protocol.DesktopAppId ? 0 : _games.GameIdForClient(appId);
    }

    // The games, as the page shows them. Rendered here rather than sent as data: the server
    // already has the list in that shape.
    private string GamesList()
    {
        var html = new StringBuilder();
        var games = _games.Details();
        var running = RunningGameId();

        foreach (var game in games)
        {
            // The title and the command travel with the tile, so that opening the editor needs no
            // second request: the page already has everything the window asks about.
            html.Append($"<article class=\"game{(game.Id == running ? " running" : string.Empty)}" +
                        $"{(game.NoStream ? " nostream" : string.Empty)}\" " +
                        $"data-id={game.Id} " +
                        $"data-title=\"{Escape(game.Title)}\" " +
                        $"data-command=\"{Escape(game.LaunchCommand)}\" " +
                        $"data-args=\"{Escape(game.Arguments ?? string.Empty)}\" " +
                        $"data-nostream={(game.NoStream ? 1 : 0)} " +
                        $"data-folder=\"{Escape(game.InstallPath ?? string.Empty)}\" " +
                        $"data-pointer={(game.Pointer ? 1 : 0)} " +
                        $"data-quality={(int)game.Quality} " +
                        $"data-card={(int)game.Splash}>");

            // A container of its own, so the overlays below position against the poster alone,
            // not against the whole tile — taller by the title and source line under it.
            html.Append("<div class=poster>");

            // The stamp is not read by the server: it is there so that a cover just replaced is
            // fetched again instead of being taken from the browser's cache under the same address.
            html.Append(game.ArtStamp != 0
                ? $"<img class=art src=\"/?cover={game.Id}&amp;v={game.ArtStamp}\" alt=\"\" loading=lazy>"
                : $"<div class=\"art none\"><span>{Text.T("no cover")}</span></div>");

            // Always in the markup; the poll that follows the running one toggles the article's
            // class, and CSS alone decides whether this badge is seen.
            html.Append($"<span class=running>{Text.T("Running")}</span>");

            // Centred on the poster rather than among the small tools below: stopping the one
            // game that is running is the one action here worth not having to aim for.
            html.Append($"<button data-do=stop title=\"{Text.T("Stop")}\" class=stop>{StopIcon}</button>");

            // Only a program without a stream is started from here; a game needs a client.
            if (game.NoStream)
                html.Append($"<button data-do=start title=\"{Text.T("Start")}\" class=start>{PlayIcon}</button>");

            // Reset only where there is something to reset: a store's game changed in any way on
            // this page. A game added by hand has no found state to go back to.
            var changedHere = game.Manual || game.ArtManual || game.Pointer ||
                              game.Quality != StreamQuality.High || game.Splash != SplashMode.Auto;
            var reset = game.Source != "by hand" && changedHere
                ? $"<button data-do=reset title=\"{Text.T("Reset to what was found")}\">{ResetIcon}</button>"
                : string.Empty;

            html.Append("<div class=tools>" + reset +
                        $"<button data-do=edit title=\"{Text.T("Edit")}\">{PencilIcon}</button>" +
                        $"<button data-do=remove title=\"{Text.T("Remove")}\" class=danger>{TrashIcon}</button>" +
                        "</div>");

            html.Append("</div>");

            html.Append($"<h3>{Escape(game.Title)}</h3>");
            // Where the game came from, and whether anybody has touched it since: "xbox (changed)"
            // says more than "by hand", which loses how the game is started.
            var changed = game.Manual || game.ArtManual;
            html.Append($"<p class=q>{Escape(Text.T(game.Source))}" +
                        (changed && game.Source != "by hand" ? Text.T(" (changed)") : string.Empty) +
                        (game.NoStream ? Text.T(" · no stream") : string.Empty) +
                        "</p>");
            html.Append("</article>");
        }

        // Last in the grid, and the same shape as the tiles beside it: a game is added where the
        // games are, not from a form somewhere else on the page.
        html.Append($"<button class=\"game add\" id=addtile>{PlusIcon}<span>{Text.T("Add a game")}</span></button>");

        return html.ToString();
    }

    // The devices that have paired, and nothing at all when none have. Eight characters of the
    // SHA-256 fingerprint are enough to tell two devices of the same name apart by eye.
    private string ClientsList()
    {
        var clients = _clients.All();

        // The heading is part of the list rather than of the section around it, so that it comes
        // and goes with the rows when the page replaces them.
        var html = new StringBuilder($"<h2>{Text.T("Paired devices")}</h2>");
        if (clients.Count == 0) html.Append($"<p class=q>{Text.T("No device has paired yet.")}</p>");

        var streamingTo = _sessions.Status is { Streaming: true } stream && IPAddress.TryParse(stream.Client, out var to)
            ? Peer.Plain(to).ToString()
            : null;

        foreach (var client in clients)
        {
            // Connected: heard from in the last half minute, or streaming to where it was heard from.
            var connected = _guard.Journal.SeenRecently(client.Fingerprint) ||
                            (streamingTo is not null && _guard.Journal.AddressOf(client.Fingerprint) == streamingTo);

            html.Append($"<div class=client data-id={client.Id} " +
                        $"data-name=\"{Escape(client.Name)}\">");
            html.Append($"<b>{Escape(client.Name)}</b>" +
                        (connected ? $"<span class=tag>{Text.T("connected")}</span>" : string.Empty));
            html.Append("<span class=q>" + Text.T("last seen {0} · paired {1}",
                            client.LastSeenAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                            client.PairedAt.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) +
                        $" · {client.Fingerprint[..8].ToLowerInvariant()}</span>");
            html.Append($"<button data-do=forget title=\"{Text.T("Forget")}\" class=danger>{TrashIcon}</button>");
            html.Append("</div>");
        }

        return html.ToString();
    }

    // The addresses refused right now on one line, busiest first, each with its attempts and a
    // button to let it back in. Empty while the ports are not forwarded: nothing outside can knock.
    private string BlockedList()
    {
        if (!_guard.IsOn) return string.Empty;

        var blocked = _guard.Blocked();
        if (blocked.Count == 0) return $"<span class=q>{Text.T("No address is blocked.")}</span>";

        var html = new StringBuilder($"<span class=q>{Text.T("Blocked:")}</span>");
        foreach (var peer in blocked)
        {
            html.Append($"<span class=ban data-ip=\"{Escape(peer.Address)}\">{Escape(peer.Address)} " +
                        $"({peer.Attempts})<button data-do=unblock title=\"{Text.T("Let back in")}\">×</button></span>");
        }

        return html.ToString();
    }

    // ------------------------------------------------------------------ the page

    private string Page()
    {
        var waiting = _pairing.WaitingFor;

        // One pass over Web/page.html. Lists are drawn here rather than in the browser, so the first
        // paint is the whole page.
        return WebAssets.Fill(WebAssets.Part("page"),
            ("lang", Text.Language),
            ("theme", ThemeName()),
            ("name", AppParameters.Identity.DisplayName),
            ("version", Program.Version),
            ("project", AppParameters.Links.Project),
            ("style", WebAssets.Style),
            ("script", WebAssets.Script),
            ("host", HostLine()),
            ("status", Status()),

            // The pairing card is written into every page and hidden, because a client can start
            // pairing a second after the page was drawn.
            ("pairclass", waiting is null ? " off" : string.Empty),
            ("who", waiting is null ? string.Empty : WhoIsPairing(waiting)),

            ("autologon", AutoLogonCard()),
            ("games", GamesList()),
            ("clients", ClientsList()),

            ("blocked", BlockedList()),
            ("available", Available()),

            ("pointer", WebAssets.Part("pointer")),

            ("log", Escape(ReadLogTail())));
    }

    // Only when the start-up check found the host unreachable after a restart: what that means,
    // what it costs to change, and the button that opens Windows' own window for it.
    private static string AutoLogonCard() =>
        AutoLogon.Last is { State: AutoLogon.State.Off } off && AutoLogonState() == "off"
            ? WebAssets.Fill(WebAssets.Part("autologon"), ("account", Escape(off.Account)))
            : string.Empty;

    // "on" or "off" from the registry alone, which is cheap enough to answer a poll with; a
    // refusal to read counts as off, so the offer stays on the page rather than vanishing.
    private static string AutoLogonState()
    {
        try
        {
            return AutoLogon.IsOn(out _) ? "on" : "off";
        }
        catch (Exception)
        {
            return "off";
        }
    }

    // The palette the page wears, from this machine's theme for applications. Read on every
    // request, because the point is to follow the registry value when it changes.
    private static string ThemeName()
    {
        try
        {
            return ThemeIcons.AppsAreDark() ? "dark" : "light";
        }
        catch (Exception)
        {
            return "dark";
        }
    }

    // The line above the two fields. The client's own name for itself is quoted rather than
    // used, because on Moonlight it is the same word on every device.
    private static string WhoIsPairing(string clientName) =>
        Text.T("A device calling itself <b>{0}</b> wants to pair with this machine.", Escape(clientName));

    // What is happening on this machine, which is the whole of the line under the heading: one
    // client at a time, and its stream's own numbers while there is one.
    private string Status()
    {
        var stream = _sessions.Status;

        return stream.Streaming
            ? Text.T("<span class=live>streaming</span> to {0}", stream.Client) + " " +
              $"<span class=dot>·</span> {stream.Detail}"
            : Text.T("waiting for a client");
    }

    // What this machine is, on the heading's line: host name, encoder and games. Off or
    // unavailable is left out, so the line is only what this machine can do right now.
    private string HostLine()
    {
        var machine = new List<string> { Escape(_identity.HostName) };

        if (_encoder.Refusal is null)
        {
            var codecs = string.Join("/", new[]
            {
                _encoder.H264 ? "H.264" : null,
                _encoder.Hevc ? "HEVC" : null,
                _encoder.Av1 ? "AV1" : null,
            }.Where(codec => codec is not null));

            machine.Add($"{Escape(_encoder.Encoder.ToString())} {Escape(codecs)}");

            // What the card can encode. Whether a stream really goes out in it depends on the
            // screen being in HDR at the time, which only a running stream knows; the log has why.
            if (_encoder.AnyHdr) machine.Add("HDR");
        }

        machine.Add(Text.T("{0} games", _games.Count()));

        // Which controller bus is presenting the pads, left out entirely when there is none: a
        // client whose controller does nothing has one question, and an absent line answers it.
        if (_gamepads.IsAvailable) machine.Add(Escape(_gamepads.Driver));

        if (_config.Upnp) machine.Add("uPnP");

        // A newer release, when the daily check has found one. Last on this line, because it is
        // news about the server rather than about the machine.
        if (_updates.Newer is { } newer)
        {
            machine.Add($"<a class=update href=\"{Escape(_updates.Link)}\" target=_blank " +
                        "rel=noopener>" + Text.T("version {0} is out — download", Escape(newer)) + "</a>");
        }

        return string.Join(" <span class=dot>·</span> ", machine);
    }

    // The end of the log file, read while the server is still writing to it — hence the sharing
    // flags. Cut at the first line break, so the page never opens on half a line.
    private static string ReadLogTail()
    {
        var path = Log.Path;
        if (path is null) return Text.T("There is no log file.");

        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                                            FileShare.ReadWrite | FileShare.Delete);

            var from = Math.Max(0, file.Length - LogTailBytes);
            file.Seek(from, SeekOrigin.Begin);

            using var reader = new StreamReader(file, Encoding.UTF8);
            var text = reader.ReadToEnd();

            if (from > 0)
            {
                var firstBreak = text.IndexOf('\n');
                if (firstBreak >= 0) text = text[(firstBreak + 1)..];
            }

            return text;
        }
        catch (Exception error)
        {
            return $"The log could not be read: {error.Message}";
        }
    }

    // ------------------------------------------------------------------ who may ask

    // Whether an address is on a private network — the test that decides who may see this page.
    // Written out rather than left to a library so that anything unrecognised is refused.
    internal static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;

        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var octets = address.GetAddressBytes();

            return octets[0] switch
            {
                10 => true,                                        // 10.0.0.0/8
                127 => true,                                       // loopback, again after mapping
                172 => octets[1] >= 16 && octets[1] <= 31,          // 172.16.0.0/12
                192 => octets[1] == 168,                            // 192.168.0.0/16
                169 => octets[1] == 254,                            // link-local
                100 => octets[1] >= 64 && octets[1] <= 127,         // carrier-grade NAT
                _ => false,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal) return true;

            // Unique local addresses, fc00::/7 — the IPv6 equivalent of the ranges above.
            return (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
        }

        return false;
    }

    private static string LocalAddress(AppConfig config)
    {
        // The address a person types is the one bound to, when there is one: no guessing which of
        // several the client will reach.
        if (!string.IsNullOrWhiteSpace(config.BindAddress) &&
            !string.Equals(config.BindAddress, "any", StringComparison.OrdinalIgnoreCase) &&
            config.BindAddress != IPAddress.Any.ToString())
        {
            return config.BindAddress;
        }

        return Peer.FirstLocalAddress()?.ToString() ?? "this machine";
    }

    // ------------------------------------------------------------------ plumbing

    private async Task WriteAsync(Stream stream, int status, string contentType, string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Forbidden")}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n" +
            "\r\n");

        await stream.WriteAsync(head, _stopping.Token);
        await stream.WriteAsync(body, _stopping.Token);
        await stream.FlushAsync(_stopping.Token);
    }

    private static string Escape(string text) => WebUtility.HtmlEncode(text);

    // A string as a JSON literal, quotes included.
    private static string JsonText(string text) => System.Text.Json.JsonSerializer.Serialize(text);

    // The marks on the tiles, drawn rather than written: a glyph from a font would be a
    // different shape on every machine, and none of these need a language.
    private const string PencilIcon =
        "<svg viewBox=\"0 0 24 24\" aria-hidden=true><path d=\"M4 20h4L19 9l-4-4L4 16v4z\"/>" +
        "<path d=\"M14 6l4 4\"/></svg>";

    private const string TrashIcon =
        "<svg viewBox=\"0 0 24 24\" aria-hidden=true><path d=\"M5 7h14M10 7V5h4v2M6 7l1 13h10l1-13\"/>" +
        "<path d=\"M10 11v6M14 11v6\"/></svg>";

    private const string ResetIcon =
        "<svg viewBox=\"0 0 24 24\" aria-hidden=true><path d=\"M4 12a8 8 0 1 0 2.3-5.6\"/>" +
        "<path d=\"M4 4v5h5\"/></svg>";

    private const string StopIcon =
        "<svg viewBox=\"0 0 24 24\" aria-hidden=true><rect x=6 y=6 width=12 height=12 rx=2/></svg>";

    private const string PlayIcon =
        "<svg viewBox=\"0 0 24 24\" aria-hidden=true><path d=\"M8 5v14l11-7z\"/></svg>";

    private const string PlusIcon =
        "<svg viewBox=\"0 0 24 24\" class=big aria-hidden=true><path d=\"M12 5v14M5 12h14\"/></svg>";

    // The stylesheet and script live in Web/page.css and Web/page.js, embedded and read through
    // WebAssets.

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener?.Stop();

        try
        {
            if (_accepting is not null) await _accepting.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
        }

        _stopping.Dispose();
    }
}

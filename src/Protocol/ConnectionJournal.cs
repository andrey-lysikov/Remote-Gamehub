//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using RemoteGameHub.App;

namespace RemoteGameHub.Protocol;

// Who connected and what they did, for the page's diagnostics: the newest events, in memory only,
// in the host's language. Also knows which devices were heard from just now.
internal sealed class ConnectionJournal
{
    private const int Lines = 500;

    // A device heard from again after this long is noted as connecting once more.
    private static readonly TimeSpan Quiet = TimeSpan.FromMinutes(1);

    // Moonlight asks every few seconds while it shows this host, so this long means it is there.
    private static readonly TimeSpan Recent = TimeSpan.FromSeconds(30);

    private readonly Queue<string> _lines = new();
    private readonly Dictionary<string, (DateTime At, string Address)> _seen = new(StringComparer.OrdinalIgnoreCase);

    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    internal void Note(string text)
    {
        var line = $"{DateTime.Now:dd.MM HH:mm:ss}  {text}";
        lock (_lines)
        {
            _lines.Enqueue(line);
            while (_lines.Count > Lines) _lines.Dequeue();
        }
    }

    internal string Contents()
    {
        lock (_lines) return string.Join("\n", _lines);
    }

    // A request from a paired device (by fingerprint) or an unpaired address (by the address).
    internal void Seen(string key, string address, string who)
    {
        bool connecting;
        lock (_seen)
        {
            var now = Clock();
            connecting = !_seen.TryGetValue(key, out var last) || now - last.At > Quiet;
            _seen[key] = (now, address);
        }

        if (connecting) Note(Text.T("{0} connected", who));
    }

    internal bool SeenRecently(string key)
    {
        lock (_seen) return _seen.TryGetValue(key, out var last) && Clock() - last.At < Recent;
    }

    internal string? AddressOf(string key)
    {
        lock (_seen) return _seen.TryGetValue(key, out var last) ? last.Address : null;
    }
}

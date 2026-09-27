//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace RemoteGameHub.Session;

// What a program without a stream writes, kept in memory only: the newest Keep characters.
internal sealed class ProgramOutput
{
    internal const int Keep = 256 * 1024;

    private readonly StringBuilder _text = new();
    private readonly object _gate = new();

    internal void Append(string text)
    {
        lock (_gate)
        {
            _text.Append(text);
            if (_text.Length <= Keep) return;

            // Cut at a line break, so the page never opens on half a line.
            var cut = _text.Length - Keep;
            var lineEnd = _text.ToString(cut, Math.Min(_text.Length - cut, 4096)).IndexOf('\n');
            _text.Remove(0, lineEnd >= 0 ? cut + lineEnd + 1 : cut);
        }
    }

    public override string ToString()
    {
        lock (_gate) return _text.ToString();
    }

    // Reads until the program closes its end; on a thread of its own, since a read blocks.
    internal void ReadFrom(Stream stream) =>
        new Thread(() =>
        {
            try
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var buffer = new char[4096];
                int read;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                    Append(new string(buffer, 0, read));
            }
            catch (Exception)
            {
                // The pipe closing under the reader is the end of the output, nothing more.
            }
        }) { IsBackground = true, Name = "program output" }.Start();
}

//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using RemoteGameHub.App;
using RemoteGameHub.Library;
using RemoteGameHub.Native;

namespace RemoteGameHub.Session;

// A program run on the host without a stream (llama-server, a script). Started hidden as the
// signed-in person; a console one has its output written to a log file beside the configuration.
internal sealed class HostProgram : IDisposable
{
    private readonly Process _process;
    private readonly Action<long> _finished;
    private int _gone;

    internal long GameId { get; }
    internal int AppId { get; }
    internal string Title { get; }
    internal int ProcessId => _process.Id;
    internal DateTime StartedAt { get; }

    internal bool IsRunning
    {
        get
        {
            try
            {
                return !_process.HasExited;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private HostProgram(Process process, long gameId, int appId, string title, DateTime startedAt,
                        Action<long> finished)
    {
        _process = process;
        GameId = gameId;
        AppId = appId;
        Title = title;
        StartedAt = startedAt;
        _finished = finished;

        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => Finished();
        if (_process.HasExited) Finished();
    }

    // Null when nothing could be started; the reason is in the log.
    internal static HostProgram? Start(long gameId, int appId, LaunchTarget target, string logFolder,
                                       Action<long> finished)
    {
        var (file, arguments) = SessionLauncher.SplitCommand(target.FullCommand);
        var folder = new[] { target.InstallPath, target.WorkingDirectory }
            .FirstOrDefault(f => !string.IsNullOrWhiteSpace(f) && Directory.Exists(f));

        // CreateProcess looks for a bare name in this server's folder, not in the one it starts in.
        if (folder is not null && !Path.IsPathRooted(file) && File.Exists(Path.Combine(folder, file)))
            file = Path.Combine(folder, file);

        folder ??= Path.IsPathRooted(file) ? Path.GetDirectoryName(file) : null;

        string? application;
        string commandLine;
        string? logPath = null;

        if (IsWindowed(file))
        {
            application = file;
            commandLine = $"\"{file}\"" + (arguments.Length > 0 ? " " + arguments : string.Empty);
        }
        else
        {
            // Through cmd for the redirection; /s strips the outer quotes, so the inner ones survive.
            Directory.CreateDirectory(logFolder);
            logPath = Path.Combine(logFolder, $"program-{gameId}.log");
            application = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            commandLine = $"\"{application}\" /d /s /c \"\"{file}\"" +
                          (arguments.Length > 0 ? " " + arguments : string.Empty) +
                          $" > \"{logPath}\" 2>&1\"";
        }

        Process? process;
        try
        {
            process = PlatformGuard.IsSystem
                ? StartAsConsoleUser(application, commandLine, folder, target.Title)
                : StartHere(application, commandLine, folder);
        }
        catch (Exception error)
        {
            Log.Warn($"\"{target.Title}\" could not be started: {error.Message}");
            return null;
        }

        if (process is null) return null;

        Log.Event($"\"{target.Title}\" is running without a stream (pid {process.Id}): {target.FullCommand}" +
                  (logPath is null ? string.Empty : $"; its output goes to {logPath}"));

        return new HostProgram(process, gameId, appId, target.Title, StartTime(process), finished);
    }

    // Takes back the program a previous worker started, if that very process is still running.
    internal static HostProgram? Adopt(RunningProgramRecord record, int appId, string title,
                                       Action<long> finished)
    {
        try
        {
            var process = Process.GetProcessById(record.ProcessId);
            var started = StartTime(process);

            if (process.HasExited ||
                Math.Abs((started - record.StartedAt.ToUniversalTime()).TotalSeconds) > 1)
            {
                process.Dispose();
                return null;
            }

            Log.Info($"\"{title}\" is still running without a stream (pid {record.ProcessId}); " +
                     "it is watched again");
            return new HostProgram(process, record.GameId, appId, title, started, finished);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Ends the program with everything it started: a script's children are the program itself.
    internal void Stop()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }

            Log.Event($"\"{Title}\" was stopped");
        }
        catch (Exception error)
        {
            Log.Warn($"\"{Title}\" could not be stopped: {error.Message}");
        }

        Finished();
    }

    private void Finished()
    {
        if (Interlocked.Exchange(ref _gone, 1) != 0) return;
        _finished(GameId);
    }

    private static DateTime StartTime(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception)
        {
            return DateTime.UtcNow;
        }
    }

    private static Process? StartHere(string? application, string commandLine, string? folder)
    {
        var (file, arguments) = SessionLauncher.SplitCommand(commandLine);
        return Process.Start(new ProcessStartInfo(application ?? file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = folder ?? string.Empty,
        });
    }

    private static Process? StartAsConsoleUser(string? application, string commandLine, string? folder,
                                               string title)
    {
        var handle = SessionLauncher.StartProgramAsConsoleUser(application, commandLine, folder, title);
        if (handle == 0) return null;

        try
        {
            // Opened while the handle is held, so the number cannot belong to anything else yet.
            return Process.GetProcessById((int)Kernel32.GetProcessId(handle));
        }
        finally
        {
            Kernel32.CloseHandle(handle);
        }
    }

    // A GUI executable (PE subsystem 2) runs as it is; anything else is treated as a console one.
    internal static bool IsWindowed(string file)
    {
        if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(file)) return false;

        try
        {
            using var reader = new BinaryReader(File.OpenRead(file));
            if (reader.ReadUInt16() != 0x5A4D) return false;

            reader.BaseStream.Position = 0x3C;
            var header = reader.ReadInt32();

            // Subsystem sits 68 bytes into the optional header, which follows the 24-byte PE header.
            reader.BaseStream.Position = header;
            if (reader.ReadUInt32() != 0x00004550) return false;

            reader.BaseStream.Position = header + 24 + 68;
            return reader.ReadUInt16() == 2;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose() => _process.Dispose();
}

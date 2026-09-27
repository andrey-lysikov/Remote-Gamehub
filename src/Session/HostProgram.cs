//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.IO.Pipes;
using RemoteGameHub.App;
using RemoteGameHub.Library;
using RemoteGameHub.Native;

namespace RemoteGameHub.Session;

// A program run on the host without a stream (llama-server, a script). Started hidden as the
// signed-in person; its stdout and stderr are read into memory, never written to disk.
internal sealed class HostProgram : IDisposable
{
    private readonly Process _process;
    private readonly Action<long> _finished;
    private int _gone;

    // Empty for a program taken back after a restart: its pipe ended with the old worker.
    internal ProgramOutput Output { get; }

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
                        Action<long> finished, ProgramOutput output)
    {
        _process = process;
        Output = output;
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
    internal static HostProgram? Start(long gameId, int appId, LaunchTarget target, Action<long> finished)
    {
        var (file, arguments) = SessionLauncher.SplitCommand(target.FullCommand);
        var folder = new[] { target.InstallPath, target.WorkingDirectory }
            .FirstOrDefault(f => !string.IsNullOrWhiteSpace(f) && Directory.Exists(f));

        // CreateProcess looks for a bare name in this server's folder, not in the one it starts in.
        if (folder is not null && !Path.IsPathRooted(file) && File.Exists(Path.Combine(folder, file)))
            file = Path.Combine(folder, file);

        folder ??= Path.IsPathRooted(file) ? Path.GetDirectoryName(file) : null;

        string application;
        string commandLine;

        if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            application = file;
            commandLine = $"\"{file}\"" + (arguments.Length > 0 ? " " + arguments : string.Empty);
        }
        else
        {
            // A script and anything else through cmd; /s strips the outer quotes, the inner ones stay.
            application = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            commandLine = $"\"{application}\" /d /s /c \"\"{file}\"" +
                          (arguments.Length > 0 ? " " + arguments : string.Empty) + "\"";
        }

        var output = new ProgramOutput();
        Process? process;
        try
        {
            process = PlatformGuard.IsSystem
                ? StartAsConsoleUser(application, commandLine, folder, target.Title, output)
                : StartHere(application, commandLine, folder, output);
        }
        catch (Exception error)
        {
            Log.Warn($"\"{target.Title}\" could not be started: {error.Message}");
            return null;
        }

        if (process is null) return null;

        Log.Event($"\"{target.Title}\" is running without a stream (pid {process.Id}): {target.FullCommand}");

        return new HostProgram(process, gameId, appId, target.Title, StartTime(process), finished, output);
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
            return new HostProgram(process, record.GameId, appId, title, started, finished, new ProgramOutput());
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

    private static Process? StartHere(string application, string commandLine, string? folder,
                                      ProgramOutput output)
    {
        var (_, arguments) = SessionLauncher.SplitCommand(commandLine);
        var process = Process.Start(new ProcessStartInfo(application, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = folder ?? string.Empty,
        });

        if (process is null) return null;

        output.ReadFrom(process.StandardOutput.BaseStream);
        output.ReadFrom(process.StandardError.BaseStream);
        return process;
    }

    private static Process? StartAsConsoleUser(string application, string commandLine, string? folder,
                                               string title, ProgramOutput output)
    {
        // One pipe for both stdout and stderr; the child's end is inheritable, ours is not.
        var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        nint handle;

        try
        {
            handle = SessionLauncher.StartProgramAsConsoleUser(application, commandLine, folder, title,
                pipe.ClientSafePipeHandle.DangerousGetHandle());
        }
        finally
        {
            // Our copy of the child's end closed, or the read would never see the end of the output.
            pipe.DisposeLocalCopyOfClientHandle();
        }

        if (handle == 0)
        {
            pipe.Dispose();
            return null;
        }

        output.ReadFrom(pipe);

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

    public void Dispose() => _process.Dispose();
}

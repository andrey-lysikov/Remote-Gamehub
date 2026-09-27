//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using RemoteGameHub.Library;
using RemoteGameHub.Session;
using Xunit;

namespace RemoteGameHub.Tests;

// Programs run on the host without a stream: how they are stored, started, watched and stopped.
public class HostProgramTests
{
    private static string System32(string file) => Path.Combine(Environment.SystemDirectory, file);

    [Fact]
    public void Arguments_and_the_no_stream_switch_are_stored_and_read_back()
    {
        using var folder = new TestFolder();
        using var database = Database.Open(folder.Path);
        var library = new GameLibrary(database);

        var id = library.Save(0, "llama", @"C:\llama\llama-server.exe", @"C:\llama",
                              "--port 8080 -m model.gguf", noStream: true);

        var target = library.Target(id)!;
        Assert.True(target.NoStream);
        Assert.Equal(@"C:\llama\llama-server.exe --port 8080 -m model.gguf", target.FullCommand);

        var detail = library.Details().Single();
        Assert.True(detail.NoStream);
        Assert.Equal("--port 8080 -m model.gguf", detail.Arguments);

        library.Save(id, "llama", @"C:\llama\llama-server.exe", @"C:\llama", "  ", noStream: false);
        target = library.Target(id)!;
        Assert.False(target.NoStream);
        Assert.Equal(@"C:\llama\llama-server.exe", target.FullCommand);
    }

    [Fact]
    public void A_program_without_a_stream_is_not_searched_for_a_cover()
    {
        using var folder = new TestFolder();
        using var database = Database.Open(folder.Path);
        var library = new GameLibrary(database);

        library.Save(0, "Game", @"C:\game.exe", null);
        library.Save(0, "Server", @"C:\server.exe", null, noStream: true);

        Assert.Equal(new[] { "Game" }, library.NeedingArtwork(TimeSpan.Zero).Select(c => c.Title));
    }

    [Fact]
    public void The_running_program_is_recorded_for_the_next_worker()
    {
        using var folder = new TestFolder();
        using var database = Database.Open(folder.Path);
        var library = new GameLibrary(database);

        Assert.Null(library.RunningProgram());

        var started = new DateTime(2026, 9, 27, 10, 0, 0, DateTimeKind.Utc);
        library.RecordRunningProgram(new RunningProgramRecord(7, 1234, started));
        Assert.Equal(new RunningProgramRecord(7, 1234, started), library.RunningProgram());

        library.RecordRunningProgram(null);
        Assert.Null(library.RunningProgram());
    }

    [Fact]
    public void Windowed_and_console_executables_are_told_apart()
    {
        Assert.True(HostProgram.IsWindowed(System32("notepad.exe")));
        Assert.False(HostProgram.IsWindowed(System32("cmd.exe")));
        Assert.False(HostProgram.IsWindowed(@"C:\scripts\start.cmd"));
    }

    [Fact]
    public void A_console_program_runs_hidden_writes_its_log_and_stops_with_its_children()
    {
        using var folder = new TestFolder();
        var finished = new ManualResetEventSlim();

        var target = new LaunchTarget(System32("ping.exe"), null, "ping", false, default, default,
                                      Arguments: "-n 30 127.0.0.1", NoStream: true);

        var pingsBefore = Pings();

        using var program = HostProgram.Start(1, 42, target, folder.Path, _ => finished.Set());
        Assert.NotNull(program);
        Assert.True(program.IsRunning);
        Assert.Equal(42, program.AppId);

        var log = Path.Combine(folder.Path, "program-1.log");
        var until = DateTime.UtcNow.AddSeconds(5);
        while (Pings().Except(pingsBefore).Count() == 0 && DateTime.UtcNow < until) Thread.Sleep(50);
        Assert.True(File.Exists(log));
        Assert.NotEmpty(Pings().Except(pingsBefore));

        program.Stop();

        Assert.False(program.IsRunning);
        Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));
        Assert.Empty(Pings().Except(pingsBefore));
    }

    private static int[] Pings() =>
        System.Diagnostics.Process.GetProcessesByName("PING").Select(p => p.Id).ToArray();

    [Fact]
    public void A_program_that_ends_by_itself_is_reported()
    {
        using var folder = new TestFolder();
        var finished = new ManualResetEventSlim();

        var target = new LaunchTarget(System32("cmd.exe"), null, "echo", false, default, default,
                                      Arguments: "/c echo hello", NoStream: true);

        using var program = HostProgram.Start(2, 43, target, folder.Path, _ => finished.Set());
        Assert.NotNull(program);

        Assert.True(finished.Wait(TimeSpan.FromSeconds(10)));
        Assert.False(program.IsRunning);
        Assert.Contains("hello", File.ReadAllText(Path.Combine(folder.Path, "program-2.log")));
    }
}

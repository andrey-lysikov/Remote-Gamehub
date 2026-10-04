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
    public void A_program_runs_hidden_its_output_is_kept_in_memory_and_it_stops_with_its_children()
    {
        var finished = new ManualResetEventSlim();

        var target = new LaunchTarget(System32("ping.exe"), null, "ping", default, default,
                                      Arguments: "-n 30 127.0.0.1", NoStream: true);

        var pingsBefore = Pings();

        using var program = HostProgram.Start(1, 42, target, _ => finished.Set());
        Assert.NotNull(program);
        Assert.True(program.IsRunning);
        Assert.Equal(42, program.AppId);

        var until = DateTime.UtcNow.AddSeconds(5);
        while (program.Output.ToString().Length == 0 && DateTime.UtcNow < until) Thread.Sleep(50);
        Assert.Contains("127.0.0.1", program.Output.ToString());
        Assert.NotEmpty(Pings().Except(pingsBefore));

        program.Stop();

        Assert.False(program.IsRunning);
        Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));
        Assert.Empty(Pings().Except(pingsBefore));
    }

    private static int[] Pings() =>
        System.Diagnostics.Process.GetProcessesByName("PING").Select(p => p.Id).ToArray();

    [Fact]
    public void A_script_runs_through_cmd_and_its_output_outlives_it()
    {
        using var folder = new TestFolder();
        var script = folder.File("hello.cmd", "@echo hello from a script\r\n@echo to stderr 1>&2\r\n");
        var finished = new ManualResetEventSlim();

        var target = new LaunchTarget(script, null, "hello", default, default, NoStream: true);

        using var program = HostProgram.Start(2, 43, target, _ => finished.Set());
        Assert.NotNull(program);

        Assert.True(finished.Wait(TimeSpan.FromSeconds(10)));
        Assert.False(program.IsRunning);

        var until = DateTime.UtcNow.AddSeconds(5);
        while (!program.Output.ToString().Contains("to stderr") && DateTime.UtcNow < until) Thread.Sleep(50);
        Assert.Contains("hello from a script", program.Output.ToString());
        Assert.Contains("to stderr", program.Output.ToString());
    }

    [Fact]
    public void Output_keeps_only_the_newest_whole_lines()
    {
        var output = new ProgramOutput();
        for (var i = 0; i < 40_000; i++) output.Append($"line {i}\n");

        var text = output.ToString();
        Assert.True(text.Length <= ProgramOutput.Keep);
        Assert.StartsWith("line ", text);
        Assert.EndsWith("line 39999\n", text);
    }
}

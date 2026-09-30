//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text;

namespace RemoteGameHub.App;

// Inbound rules in Windows Firewall for this server's ports. The worker is a service's child, so
// Windows never asks the user to allow it: without these, clients find it but cannot connect.
internal static class FirewallRules
{
    private const string Group = AppParameters.Identity.DisplayName;
    private const string TcpId = "RemoteGameHub-TCP";
    private const string UdpId = "RemoteGameHub-UDP";

    // What the rules have to open: the page, the three TCP listeners and the three stream ports.
    internal static (int[] Tcp, int[] Udp) Ports(AppConfig config) =>
    (
        new[] { config.WebPort, config.HttpsPort, config.HttpPort, config.RtspPort }
            .Distinct().Order().ToArray(),
        new[] { config.VideoPort, config.ControlPort, config.AudioPort }
            .Distinct().Order().ToArray()
    );

    // Adds the rules when missing, rewrites them when they no longer match the ports or were
    // switched off, and says in the log when the network itself is one they do not cover.
    internal static void Ensure(AppConfig config)
    {
        if (!config.Firewall)
        {
            Log.Info("Windows Firewall rules are left alone: [Network] Firewall = false");
            return;
        }

        if (!PlatformGuard.IsElevated)
        {
            Log.Info("Windows Firewall rules were not checked: this copy runs without administrator rights");
            return;
        }

        var (tcp, udp) = Ports(config);
        var (code, output) = Run(EnsureScript(tcp, udp));
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (code != 0)
        {
            Log.Warn($"Windows Firewall rules could not be checked: {output}\n" +
                     $"If clients find this machine but cannot connect, allow inbound TCP {string.Join(", ", tcp)}\n" +
                     $"and UDP {string.Join(", ", udp)} in Windows Firewall by hand.");
            return;
        }

        var added = lines.Where(line => line.StartsWith("added ", StringComparison.Ordinal))
                         .Select(line => line["added ".Length..]).ToList();
        if (added.Count > 0)
            Log.Event($"Windows Firewall now lets clients in: {string.Join("; ", added)}");
        else
            Log.Info("Windows Firewall already lets clients reach the ports");

        foreach (var line in lines.Where(line => line.StartsWith("public ", StringComparison.Ordinal)))
        {
            Log.Warn($"The network \"{line["public ".Length..]}\" is Public in Windows, and the firewall rules of this\n" +
                     "server cover only private and domain networks: clients on it cannot connect. If it is\n" +
                     "your own network, make it Private in Settings > Network & internet > its properties.");
        }
    }

    // Taken away with the service, so an uninstalled copy leaves no ports open behind it.
    internal static void Remove()
    {
        var (code, output) = Run(
            $"Remove-NetFirewallRule -Name '{TcpId}','{UdpId}' -ErrorAction SilentlyContinue");

        if (code != 0) Log.Warn($"Windows Firewall rules could not be removed: {output}");
        else Log.Info("Windows Firewall rules of this server removed");
    }

    internal static string EnsureScript(int[] tcp, int[] udp)
    {
        static string List(int[] ports) => string.Join(",", ports.Select(port => $"'{port}'"));

        return $$"""
            $rules = @(
                @{ Id = '{{TcpId}}'; Name = '{{Group}} (TCP)'; Protocol = 'TCP'; Ports = @({{List(tcp)}}) },
                @{ Id = '{{UdpId}}'; Name = '{{Group}} (UDP)'; Protocol = 'UDP'; Ports = @({{List(udp)}}) }
            )
            foreach ($rule in $rules) {
                $wanted = ($rule.Ports | Sort-Object) -join ','
                $found = Get-NetFirewallRule -Name $rule.Id -ErrorAction SilentlyContinue
                $fits = $false
                if ($found) {
                    $filter = $found | Get-NetFirewallPortFilter
                    $fits = "$($found.Enabled)" -eq 'True' -and "$($found.Action)" -eq 'Allow' -and
                            "$($found.Direction)" -eq 'Inbound' -and "$($filter.Protocol)" -eq $rule.Protocol -and
                            ((@($filter.LocalPort) | Sort-Object) -join ',') -eq $wanted
                    if (-not $fits) { Remove-NetFirewallRule -Name $rule.Id }
                }
                if (-not $fits) {
                    New-NetFirewallRule -Name $rule.Id -DisplayName $rule.Name -Group '{{Group}}' `
                        -Direction Inbound -Action Allow -Protocol $rule.Protocol -LocalPort $rule.Ports `
                        -Profile Domain,Private | Out-Null
                    "added $($rule.Protocol) $($rule.Ports -join ', ')"
                }
            }
            Get-NetConnectionProfile | Where-Object { "$($_.NetworkCategory)" -eq 'Public' } |
                ForEach-Object { "public $($_.Name)" }
            """;
    }

    // Windows PowerShell, which every Windows 11 has with its firewall module. Errors are caught
    // inside and printed as text: redirected, powershell.exe would otherwise write them as XML.
    private static (int Code, string Output) Run(string script)
    {
        var wrapped = "$ErrorActionPreference = 'Stop'\n" +
                      "[Console]::OutputEncoding = [Text.Encoding]::UTF8\n" +
                      $"try {{\n{script}\n}} catch {{ $_.Exception.Message; exit 1 }}";

        var start = new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                                         "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped)) })
            start.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(start)
                                ?? throw new InvalidOperationException("powershell.exe did not start");

            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return (process.ExitCode, (output + error.Result).Trim());
        }
        catch (Exception error)
        {
            return (-1, error.Message);
        }
    }
}

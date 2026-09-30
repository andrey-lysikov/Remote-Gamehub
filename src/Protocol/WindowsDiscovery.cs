//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using RemoteGameHub.App;
using RemoteGameHub.Native;

namespace RemoteGameHub.Protocol;

// The service published by Windows' own mDNS responder (the DNS Client service). It owns port
// 5353 and its firewall rules, so its answers reach clients where a second socket's did not.
internal sealed unsafe class WindowsDiscovery : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    // Everything Windows holds a pointer to, freed only once it has let go of the registration.
    private readonly List<nint> _memory = new();
    private readonly nint _request;
    private GCHandle _self;
    private TaskCompletionSource<uint> _completion = NewCompletion();

    private WindowsDiscovery(string instanceName, string hostName, int port)
    {
        _self = GCHandle.Alloc(this);

        // One empty TXT string: with no properties Windows sends a TXT record holding none,
        // which is malformed and makes some resolvers drop the whole service.
        var empty = Keep(Marshal.StringToHGlobalUni(string.Empty));
        var keys = Keep(Marshal.AllocHGlobal(sizeof(nint)));
        Marshal.WriteIntPtr(keys, empty);

        var instance = Keep(Marshal.AllocHGlobal(sizeof(DnsServiceInstance)));
        *(DnsServiceInstance*)instance = new DnsServiceInstance
        {
            InstanceName = Keep(Marshal.StringToHGlobalUni(instanceName)),
            HostName = Keep(Marshal.StringToHGlobalUni(hostName)),
            Port = (ushort)port,
            PropertyCount = 1,
            Keys = keys,
            Values = keys,
        };

        _request = Keep(Marshal.AllocHGlobal(sizeof(DnsServiceRegisterRequest)));
        *(DnsServiceRegisterRequest*)_request = new DnsServiceRegisterRequest
        {
            Version = Dnsapi.QueryRequestVersion1,
            ServiceInstance = instance,
            CompletionCallback = (nint)(delegate* unmanaged<uint, nint, nint, void>)&Completed,
            Context = GCHandle.ToIntPtr(_self),
        };
    }

    // Registered and confirmed, or null with the reason logged, leaving discovery to the caller.
    internal static WindowsDiscovery? TryRegister(string instanceName, string hostName, int port)
    {
        WindowsDiscovery? registration = null;
        try
        {
            registration = new WindowsDiscovery(instanceName, hostName, port);
            var status = registration.Call(register: true);
            if (status == 0) return registration;

            // Still pending in Windows, which may yet write to it: left allocated, not freed.
            if (status == uint.MaxValue) registration = null;

            Log.Info($"Windows would not publish this server for discovery ({Describe(status)}); " +
                     "answering discovery itself instead");
        }
        catch (Exception error)
        {
            Log.Info($"Windows could not be asked to publish this server for discovery ({error.Message}); " +
                     "answering discovery itself instead");
        }

        registration?.Release();
        return null;
    }

    // Starts one call and waits for its callback. Anything but pending is the answer itself.
    private uint Call(bool register)
    {
        _completion = NewCompletion();

        var status = register
            ? Dnsapi.DnsServiceRegister(_request, 0)
            : Dnsapi.DnsServiceDeRegister(_request, 0);
        if (status != Dnsapi.RequestPending) return status;

        return _completion.Task.Wait(Patience) ? _completion.Task.Result : uint.MaxValue;
    }

    [UnmanagedCallersOnly]
    private static void Completed(uint status, nint context, nint instance)
    {
        // An exception escaping here would end the process, from a thread Windows owns.
        try
        {
            if (instance != 0) Dnsapi.DnsServiceFreeInstance(instance);

            if (GCHandle.FromIntPtr(context).Target is WindowsDiscovery registration)
                registration._completion.TrySetResult(status);
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        var status = Call(register: false);

        // Unanswered, Windows may still read the request later: leaked rather than freed under it.
        if (status == uint.MaxValue)
        {
            Log.Info("Windows did not confirm that this server is no longer published for discovery");
            return;
        }

        Release();
    }

    private void Release()
    {
        foreach (var block in _memory) Marshal.FreeHGlobal(block);
        _memory.Clear();
        if (_self.IsAllocated) _self.Free();
    }

    private nint Keep(nint block)
    {
        _memory.Add(block);
        return block;
    }

    private static TaskCompletionSource<uint> NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string Describe(uint status) => status == uint.MaxValue
        ? "no answer within five seconds"
        : $"error {status}: {new System.ComponentModel.Win32Exception((int)status).Message}";
}

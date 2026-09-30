//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;

namespace RemoteGameHub.Native;

// DNS_SERVICE_INSTANCE: one DNS-SD service as Windows' own mDNS responder publishes it.
[StructLayout(LayoutKind.Sequential)]
internal struct DnsServiceInstance
{
    internal nint InstanceName;
    internal nint HostName;
    internal nint Ip4Address;
    internal nint Ip6Address;
    internal ushort Port;
    internal ushort Priority;
    internal ushort Weight;
    internal uint PropertyCount;
    internal nint Keys;
    internal nint Values;
    internal uint InterfaceIndex;
}

// DNS_SERVICE_REGISTER_REQUEST. The same block is handed to register and to deregister, and
// lives in unmanaged memory: Windows keeps using it until the deregistration completes.
[StructLayout(LayoutKind.Sequential)]
internal struct DnsServiceRegisterRequest
{
    internal uint Version;
    internal uint InterfaceIndex;
    internal nint ServiceInstance;
    internal nint CompletionCallback;
    internal nint Context;
    internal nint Credentials;
    internal int UnicastEnabled;
}

// DNS-SD through the DNS Client service (Windows 10 1809 and later), which already owns port 5353.
internal static class Dnsapi
{
    internal const uint QueryRequestVersion1 = 1;

    // Returned by register and deregister when the answer will come to the callback.
    internal const uint RequestPending = 9506;

    [DllImport("dnsapi.dll")]
    internal static extern uint DnsServiceRegister(nint request, nint cancel);

    [DllImport("dnsapi.dll")]
    internal static extern uint DnsServiceDeRegister(nint request, nint cancel);

    [DllImport("dnsapi.dll")]
    internal static extern void DnsServiceFreeInstance(nint instance);
}

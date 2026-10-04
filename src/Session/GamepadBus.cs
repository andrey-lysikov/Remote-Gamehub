//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using RemoteGameHub.App;

namespace RemoteGameHub.Session;

// One controller bus GamepadHub can drive — ViGEmBus today. Plugs pads in and hands back a
// target to report state on and wait for feedback from.
internal interface IGamepadBus : IDisposable
{
    // Named for the page and the log, and to tell a report apart from the driver that sent it.
    string Name { get; }

    IGamepadTarget? PlugIn(int index, GamepadKind kind);
}

internal interface IGamepadTarget : IDisposable
{
    void Submit(in GamepadState state);

    // Blocks until the guest sets the rumble motors or the player light, or the timeout passes;
    // false either way, GamepadHub only reads the out parameters when this returns true.
    bool WaitForFeedback(int timeoutMs, out byte largeMotor, out byte smallMotor, out byte ledNumber);

    // The pad's own touchpad and motion sensors; an Xbox pad has neither and ignores both.
    void Touch(byte eventType, uint pointerId, float x, float y) { }

    void Motion(byte motionType, float x, float y, float z) { }
}

// Adapts the ViGEmBus client (ViGEmBus.cs) to the shape above: an Xbox 360 pad for most
// controllers, or a DualShock 4 pad when the client says a PlayStation one is behind that slot.
internal sealed class ViGEmGamepadBus : IGamepadBus
{
    private const ushort VendorMicrosoft = 0x045E;
    private const ushort ProductXbox360Wired = 0x028E;

    // Sony's real vendor ID and the original DualShock 4 (CUH-ZCT1x)'s product ID.
    private const ushort VendorSony = 0x054C;
    private const ushort ProductDualShock4 = 0x05C4;

    private readonly ViGEmBus _bus;

    private ViGEmGamepadBus(ViGEmBus bus) => _bus = bus;

    // Opens the bus, or returns null with a reason already written to the log by ViGEmBus.Open.
    internal static ViGEmGamepadBus? Open()
    {
        var bus = ViGEmBus.Open();
        return bus is null ? null : new ViGEmGamepadBus(bus);
    }

    public string Name => "ViGEmBus";

    public IGamepadTarget? PlugIn(int index, GamepadKind kind)
    {
        if (kind == GamepadKind.PlayStation)
        {
            var ds4Serial = _bus.PlugInDs4(VendorSony, ProductDualShock4);
            if (ds4Serial is null)
            {
                Log.Warn($"the virtual controller bus has no free slot for controller {index}. " +
                         "Another program using ViGEm is probably holding them.");
                return null;
            }

            return new Ds4Target(_bus, ds4Serial.Value);
        }

        var serial = _bus.PlugIn(VendorMicrosoft, ProductXbox360Wired);
        if (serial is null)
        {
            Log.Warn($"the virtual controller bus has no free slot for controller {index}. " +
                     "Another program using ViGEm is probably holding them.");
            return null;
        }

        return new Target(_bus, serial.Value);
    }

    private sealed class Target(ViGEmBus bus, uint serial) : IGamepadTarget
    {
        public void Submit(in GamepadState state) => bus.Submit(serial, state.ToReport());

        public bool WaitForFeedback(int timeoutMs, out byte largeMotor, out byte smallMotor,
                                    out byte ledNumber) =>
            bus.WaitForFeedback(serial, timeoutMs, out largeMotor, out smallMotor, out ledNumber);

        public void Dispose() => bus.Unplug(serial);
    }

    // DS4 feedback rides IOCTL_DS4_REQUEST_NOTIFICATION behind a struct this server could not
    // verify against any real header, so it is left unread; the feedback thread resends instead.
    private sealed class Ds4Target(ViGEmBus bus, uint serial) : IGamepadTarget
    {
        // The report's timestamp wraps every 350 ms, and a game that integrates the gyroscope
        // by it must see it move even while nothing else does (Sunshine resends as often).
        private const int ResendMs = 100;

        private readonly object _gate = new();
        private readonly DualShock4Report _report = new(Stopwatch.GetTimestamp());
        private long _sentAt;

        public void Submit(in GamepadState state)
        {
            lock (_gate)
            {
                _report.SetInput(state);
                Send();
            }
        }

        public void Touch(byte eventType, uint pointerId, float x, float y)
        {
            lock (_gate)
            {
                if (_report.Touch(eventType, pointerId, x, y)) Send();
            }
        }

        public void Motion(byte motionType, float x, float y, float z)
        {
            lock (_gate)
            {
                if (_report.Motion(motionType, x, y, z)) Send();
            }
        }

        public bool WaitForFeedback(int timeoutMs, out byte largeMotor, out byte smallMotor,
                                    out byte ledNumber)
        {
            largeMotor = smallMotor = ledNumber = 0;

            var end = Environment.TickCount64 + timeoutMs;
            for (var now = Environment.TickCount64; now < end; now = Environment.TickCount64)
            {
                lock (_gate)
                {
                    if (now - _sentAt >= ResendMs) Send();
                }

                Thread.Sleep((int)Math.Min(ResendMs, end - now));
            }

            return false;
        }

        private void Send()
        {
            bus.SubmitDs4(serial, _report.Stamped(Stopwatch.GetTimestamp()));
            _sentAt = Environment.TickCount64;
        }

        public void Dispose() => bus.Unplug(serial);
    }

    public void Dispose() => _bus.Dispose();
}

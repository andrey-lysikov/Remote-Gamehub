//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

namespace RemoteGameHub.Session;

// The full report of one virtual DualShock 4. Buttons, touchpad and motion arrive in separate
// packets and each changes only its own part, so the report is kept between them.
internal sealed class DualShock4Report
{
    // Limelight.h's LI_TOUCH_EVENT_* and LI_MOTION_TYPE_*.
    internal const byte TouchDown = 0x01;
    internal const byte TouchUp = 0x02;
    internal const byte TouchMove = 0x03;
    internal const byte TouchCancel = 0x04;
    internal const byte TouchCancelAll = 0x07;
    internal const byte MotionAccelerometer = 0x01;
    internal const byte MotionGyroscope = 0x02;

    // The touchpad in report units, as ViGEmBus describes it to Windows.
    private const int TouchpadWidth = 1920;
    private const int TouchpadHeight = 943;

    // Bit 7 of a finger's first byte: not touching.
    private const byte FingerUp = 0x80;

    // The report's timestamp counts in units of 16/3 microseconds and wraps every 350 ms.
    private const double TimestampUnitNs = 5333;

    private const float EarthGravity = 9.80665f;

    // The DS4's scales: 8192 per g, 16 per degree a second.
    private const float AccelPerG = 8192;
    private const float GyroPerDegree = 16;

    private ViGEmBus.Ds4ReportEx _report;

    // The client's pointer id behind each of the two fingers, null when that finger is up.
    private readonly uint?[] _fingers = new uint?[2];

    private readonly long _start;

    internal DualShock4Report(long startTimestamp)
    {
        _start = startTimestamp;

        // What ViGEmBus itself starts a pad with (Ds4Pdo.cpp): centred, nothing pressed, wired and
        // full, no finger down, and lying still: gravity straight down the Y axis.
        SetInput(GamepadState.Released);
        _report.BatteryLevel = 0xFF;
        _report.BatteryLevelSpecial = 0x1A;
        _report.TouchPacketCount = 1;
        _report.CurrentTouch = Untouched();
        _report.PreviousTouch1 = Untouched();
        _report.PreviousTouch2 = Untouched();
        Motion(MotionAccelerometer, 0, EarthGravity, 0);
        Motion(MotionGyroscope, 0, 0, 0);
    }

    internal void SetInput(in GamepadState state)
    {
        var input = state.ToDs4Report();
        _report.ThumbLX = input.ThumbLX;
        _report.ThumbLY = input.ThumbLY;
        _report.ThumbRX = input.ThumbRX;
        _report.ThumbRY = input.ThumbRY;
        _report.Buttons = input.Buttons;
        _report.Special = input.Special;
        _report.TriggerL = input.TriggerL;
        _report.TriggerR = input.TriggerR;
    }

    // One finger event, x and y from 0 to 1. False when it changed nothing and need not be sent:
    // hover and the like, a third finger, or one whose press was never seen.
    internal bool Touch(byte eventType, uint pointerId, float x, float y)
    {
        int finger;

        if (eventType == TouchCancelAll)
        {
            _fingers[0] = _fingers[1] = null;
            _report.CurrentTouch.Finger1 |= FingerUp;
            _report.CurrentTouch.Finger2 |= FingerUp;
            _report.CurrentTouch.PacketCounter++;
            return true;
        }

        if (eventType == TouchDown)
        {
            finger = Array.IndexOf(_fingers, null);
            if (finger < 0) return false;

            _fingers[finger] = pointerId;

            // Down, with a new tracking number: a game tells a second tap from a held finger by it.
            ref var state = ref FingerState(finger);
            state = (byte)((state + 1) & 0x7F);
        }
        else
        {
            finger = Array.IndexOf(_fingers, pointerId);
            if (finger < 0) return false;

            if (eventType is TouchUp or TouchCancel)
            {
                _fingers[finger] = null;
                FingerState(finger) |= FingerUp;
            }
            else if (eventType != TouchMove)
            {
                return false;
            }
        }

        var touchX = Math.Min((int)(Unit(x) * TouchpadWidth), TouchpadWidth - 1);
        var touchY = Math.Min((int)(Unit(y) * TouchpadHeight), TouchpadHeight - 1);
        var data0 = (byte)(touchX & 0xFF);
        var data1 = (byte)(((touchX >> 8) & 0x0F) | ((touchY & 0x0F) << 4));
        var data2 = (byte)((touchY >> 4) & 0xFF);

        if (finger == 0)
        {
            _report.CurrentTouch.Finger1Data0 = data0;
            _report.CurrentTouch.Finger1Data1 = data1;
            _report.CurrentTouch.Finger1Data2 = data2;
        }
        else
        {
            _report.CurrentTouch.Finger2Data0 = data0;
            _report.CurrentTouch.Finger2Data1 = data1;
            _report.CurrentTouch.Finger2Data2 = data2;
        }

        _report.CurrentTouch.PacketCounter++;
        return true;
    }

    // Accelerometer in m/s² including gravity, gyroscope in degrees a second, SDL's axes — which
    // are the DS4's own. False for a sensor this pad does not have.
    internal bool Motion(byte motionType, float x, float y, float z)
    {
        // The inverse of the calibration ViGEmBus reports for its pad, as Sunshine applies it: a
        // game that calibrates then reads back what the client measured.
        switch (motionType)
        {
            case MotionAccelerometer:
                _report.AccelX = Calibrated(x / EarthGravity * AccelPerG, -297, 1.010796f);
                _report.AccelY = Calibrated(y / EarthGravity * AccelPerG, -42, 1.014614f);
                _report.AccelZ = Calibrated(z / EarthGravity * AccelPerG, -512, 1.024768f);
                return true;

            case MotionGyroscope:
                _report.GyroX = Calibrated(x * GyroPerDegree, 1, 0.977596f);
                _report.GyroY = Calibrated(y * GyroPerDegree, 0, 0.972370f);
                _report.GyroZ = Calibrated(z * GyroPerDegree, 0, 0.971550f);
                return true;

            default:
                return false;
        }
    }

    // The report as it stands, timestamped now (a Stopwatch timestamp). Games that integrate the
    // gyroscope do it by this clock, so it is measured, not counted per report.
    internal ViGEmBus.Ds4ReportEx Stamped(long now)
    {
        var elapsedNs = (now - _start) * (1e9 / System.Diagnostics.Stopwatch.Frequency);
        _report.Timestamp = unchecked((ushort)(long)(elapsedNs / TimestampUnitNs));
        return _report;
    }

    private ref byte FingerState(int finger) =>
        ref (finger == 0 ? ref _report.CurrentTouch.Finger1 : ref _report.CurrentTouch.Finger2);

    private static ViGEmBus.Ds4Touch Untouched() => new() { Finger1 = FingerUp, Finger2 = FingerUp };

    // The client's 0 to 1, from a float off the network that may be anything.
    private static float Unit(float value) => float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;

    private static short Calibrated(float value, float bias, float scale)
    {
        if (!float.IsFinite(value)) return 0;
        return (short)Math.Clamp((value + bias) / scale, short.MinValue, short.MaxValue);
    }
}

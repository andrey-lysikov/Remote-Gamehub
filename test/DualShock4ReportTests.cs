//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Runtime.InteropServices;
using RemoteGameHub.Session;
using Xunit;

namespace RemoteGameHub.Tests;

// The DualShock 4 report ViGEmBus is handed: a byte out of place is a touchpad that reads as a
// gyroscope, and nothing but a game on a real machine would show it.
public class DualShock4ReportTests
{
    [Fact]
    public void The_report_has_the_size_and_offsets_of_DS4_REPORT_EX()
    {
        Assert.Equal(63, Marshal.SizeOf<ViGEmBus.Ds4ReportEx>());
        Assert.Equal(9, Marshal.SizeOf<ViGEmBus.Ds4Touch>());

        Assert.Equal(9, Offset(nameof(ViGEmBus.Ds4ReportEx.Timestamp)));
        Assert.Equal(12, Offset(nameof(ViGEmBus.Ds4ReportEx.GyroX)));
        Assert.Equal(18, Offset(nameof(ViGEmBus.Ds4ReportEx.AccelX)));
        Assert.Equal(29, Offset(nameof(ViGEmBus.Ds4ReportEx.BatteryLevelSpecial)));
        Assert.Equal(32, Offset(nameof(ViGEmBus.Ds4ReportEx.TouchPacketCount)));
        Assert.Equal(33, Offset(nameof(ViGEmBus.Ds4ReportEx.CurrentTouch)));
        Assert.Equal(42, Offset(nameof(ViGEmBus.Ds4ReportEx.PreviousTouch1)));
        Assert.Equal(51, Offset(nameof(ViGEmBus.Ds4ReportEx.PreviousTouch2)));
    }

    [Fact]
    public void A_new_pad_is_centred_with_no_finger_down()
    {
        var report = new DualShock4Report(0).Stamped(0);

        Assert.Equal(0x80, report.ThumbLX);
        Assert.Equal(0x8, report.Buttons & 0xF);
        Assert.Equal(0x80, report.CurrentTouch.Finger1 & 0x80);
        Assert.Equal(0x80, report.CurrentTouch.Finger2 & 0x80);
    }

    [Fact]
    public void A_finger_down_is_packed_as_two_12_bit_values()
    {
        var pad = new DualShock4Report(0);

        Assert.True(pad.Touch(DualShock4Report.TouchDown, 7, 0.5f, 0.5f));
        var touch = pad.Stamped(0).CurrentTouch;

        // 0.5 of 1920 x 943 is 960 x 471: 0x3C0 and 0x1D7.
        Assert.Equal(0x01, touch.Finger1);
        Assert.Equal(0xC0, touch.Finger1Data0);
        Assert.Equal(0x73, touch.Finger1Data1);
        Assert.Equal(0x1D, touch.Finger1Data2);
        Assert.Equal(0x80, touch.Finger2 & 0x80);
    }

    [Fact]
    public void Lifting_a_finger_sets_it_up_and_the_next_press_gets_a_new_tracking_number()
    {
        var pad = new DualShock4Report(0);

        pad.Touch(DualShock4Report.TouchDown, 7, 0.1f, 0.1f);
        pad.Touch(DualShock4Report.TouchUp, 7, 0.1f, 0.1f);
        Assert.Equal(0x81, pad.Stamped(0).CurrentTouch.Finger1);

        pad.Touch(DualShock4Report.TouchDown, 8, 0.1f, 0.1f);
        Assert.Equal(0x02, pad.Stamped(0).CurrentTouch.Finger1);
    }

    [Fact]
    public void A_second_finger_takes_the_second_slot_and_a_third_is_refused()
    {
        var pad = new DualShock4Report(0);

        Assert.True(pad.Touch(DualShock4Report.TouchDown, 1, 0, 0));
        Assert.True(pad.Touch(DualShock4Report.TouchDown, 2, 1, 1));
        Assert.False(pad.Touch(DualShock4Report.TouchDown, 3, 0, 0));

        var touch = pad.Stamped(0).CurrentTouch;
        Assert.Equal(0, touch.Finger2 & 0x80);

        // The far corner stays inside the pad: 1919 x 942.
        Assert.Equal(0x7F, touch.Finger2Data0);
        Assert.Equal(0xE7, touch.Finger2Data1);
        Assert.Equal(0x3A, touch.Finger2Data2);
    }

    [Fact]
    public void Moving_a_finger_that_never_went_down_changes_nothing()
    {
        var pad = new DualShock4Report(0);

        Assert.False(pad.Touch(DualShock4Report.TouchMove, 5, 0.5f, 0.5f));
        Assert.Equal(0, pad.Stamped(0).CurrentTouch.PacketCounter);
    }

    [Fact]
    public void Cancelling_all_lifts_both_fingers()
    {
        var pad = new DualShock4Report(0);
        pad.Touch(DualShock4Report.TouchDown, 1, 0, 0);
        pad.Touch(DualShock4Report.TouchDown, 2, 0, 0);

        Assert.True(pad.Touch(DualShock4Report.TouchCancelAll, 0, 0, 0));
        var touch = pad.Stamped(0).CurrentTouch;

        Assert.Equal(0x80, touch.Finger1 & 0x80);
        Assert.Equal(0x80, touch.Finger2 & 0x80);
        Assert.True(pad.Touch(DualShock4Report.TouchDown, 3, 0, 0));
    }

    [Fact]
    public void A_pad_at_rest_reads_one_g_down_Y_once_ViGEm_calibration_is_applied()
    {
        var report = new DualShock4Report(0).Stamped(0);

        // ViGEmBus reports bias -42 and scale 1.014614 for Y; undoing them must give 8192 per g.
        Assert.InRange(report.AccelY * 1.014614f + 42, 8190f, 8194f);
        Assert.InRange(report.AccelX * 1.010796f + 297, -2f, 2f);
    }

    [Fact]
    public void Gyroscope_degrees_a_second_become_16_units_each()
    {
        var pad = new DualShock4Report(0);

        Assert.True(pad.Motion(DualShock4Report.MotionGyroscope, 100, 0, 0));
        var report = pad.Stamped(0);

        Assert.InRange(report.GyroX * 0.977596f - 1, 1598f, 1602f);
    }

    [Fact]
    public void Readings_off_the_network_cannot_overflow_or_poison_the_report()
    {
        var pad = new DualShock4Report(0);

        pad.Motion(DualShock4Report.MotionGyroscope, 1e6f, float.NaN, float.NegativeInfinity);
        var report = pad.Stamped(0);

        Assert.Equal(short.MaxValue, report.GyroX);
        Assert.Equal(0, report.GyroY);
        Assert.Equal(0, report.GyroZ);
        Assert.False(pad.Motion(9, 0, 0, 0));
    }

    [Fact]
    public void The_timestamp_counts_in_units_of_16_thirds_of_a_microsecond()
    {
        var pad = new DualShock4Report(0);

        // One millisecond is 187.5 units.
        Assert.Equal(187, pad.Stamped(Stopwatch.Frequency / 1000).Timestamp);
    }

    private static int Offset(string field) =>
        (int)Marshal.OffsetOf<ViGEmBus.Ds4ReportEx>(field);
}

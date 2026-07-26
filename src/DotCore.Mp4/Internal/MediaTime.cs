using System;

namespace DotCore.Mp4;

internal static class MediaTime
{
    internal const int DefaultTrackTimescale = (int)TimeSpan.TicksPerSecond;

    public static long ToTicks(TimeSpan value, int timescale)
    {
        if (timescale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timescale));
        }

        var ticks = value.Ticks;
        var seconds = ticks / TimeSpan.TicksPerSecond;
        var remainder = ticks % TimeSpan.TicksPerSecond;

        long scaledRemainder;
        try
        {
            scaledRemainder = checked(remainder * (long)timescale);
        }
        catch (OverflowException ex)
        {
            throw new Mp4TimestampException("The timestamp is outside the representable track range.", ex);
        }

        if (scaledRemainder % TimeSpan.TicksPerSecond != 0)
        {
            throw new Mp4TimestampException(
                "The timestamp cannot be represented exactly at the selected MP4 track timescale.");
        }

        try
        {
            return checked(seconds * timescale + scaledRemainder / TimeSpan.TicksPerSecond);
        }
        catch (OverflowException ex)
        {
            throw new Mp4TimestampException("The timestamp is outside the representable track range.", ex);
        }
    }

    public static TimeSpan FromTicks(long value, int timescale)
    {
        if (timescale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timescale));
        }

        var seconds = value / timescale;
        var remainder = value % timescale;
        long scaledRemainder;
        try
        {
            scaledRemainder = checked(remainder * (long)TimeSpan.TicksPerSecond);
        }
        catch (OverflowException ex)
        {
            throw new Mp4FormatException("The MP4 timestamp is outside the representable TimeSpan range.", ex);
        }

        if (scaledRemainder % timescale != 0)
        {
            throw new Mp4FormatException("The MP4 timestamp cannot be converted to an exact TimeSpan.");
        }

        try
        {
            return new TimeSpan(checked(seconds * TimeSpan.TicksPerSecond + scaledRemainder / timescale));
        }
        catch (OverflowException ex)
        {
            throw new Mp4FormatException("The MP4 timestamp is outside the representable TimeSpan range.", ex);
        }
    }

    public static TimeSpan FromTicksRounded(long value, int timescale)
    {
        if (timescale <= 0) throw new Mp4FormatException("The MP4 timescale must be positive.");
        try
        {
            var seconds = value / timescale;
            var remainder = value % timescale;
            var scaledRemainder = checked(remainder * (long)TimeSpan.TicksPerSecond);
            var ticks = checked(seconds * TimeSpan.TicksPerSecond + scaledRemainder / timescale);
            var fractional = scaledRemainder % timescale;
            if (fractional != 0 && Math.Abs(fractional) * 2 >= timescale)
            {
                ticks = checked(ticks + (fractional > 0 ? 1 : -1));
            }

            return new TimeSpan(ticks);
        }
        catch (OverflowException error)
        {
            throw new Mp4FormatException("The MP4 timestamp is outside the representable TimeSpan range.", error);
        }
    }
}

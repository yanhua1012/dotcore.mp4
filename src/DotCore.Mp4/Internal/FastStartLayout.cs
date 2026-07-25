using System;

namespace DotCore.Mp4;

internal static class FastStartLayout
{
    private const int MaximumConvergencePasses = 8;

    public static long AdjustOffset(long offset, long adjustment)
    {
        if (offset < 0) throw new Mp4FormatException("A chunk offset must not be negative.");
        if (adjustment < 0) throw new Mp4FormatException("A faststart offset adjustment must not be negative.");
        try
        {
            return checked(offset + adjustment);
        }
        catch (OverflowException error)
        {
            throw new Mp4FormatException("A faststart chunk offset exceeds the supported range.", error);
        }
    }

    public static byte[] BuildStableMovieBox(Func<long, byte[]> buildMovieBox)
    {
        if (buildMovieBox == null) throw new ArgumentNullException(nameof(buildMovieBox));
        var adjustment = 0L;
        for (var pass = 0; pass < MaximumConvergencePasses; pass++)
        {
            var movie = buildMovieBox(adjustment);
            if (movie == null) throw new InvalidOperationException("The movie-box builder returned null.");
            if (movie.LongLength == adjustment) return movie;
            adjustment = movie.LongLength;
        }

        throw new Mp4FormatException("Faststart movie metadata length did not converge.");
    }
}

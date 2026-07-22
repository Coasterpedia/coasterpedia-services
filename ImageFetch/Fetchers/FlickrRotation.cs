using CoasterpediaServices.ImageFetch.Clients.Flickr;
using NetVips;

namespace CoasterpediaServices.ImageFetch.Fetchers;

/// <summary>
/// Bakes Flickr's out-of-band rotation flag into imported pixels.
/// </summary>
internal static class FlickrRotation
{
    // Formats where a re-encode is well-defined and lossless-enough to be worth it. GIF is
    // deliberately absent: the loader here reads a single frame, so rotating an animation
    // would flatten it.
    private static readonly string[] Rotatable = [".jpg", ".jpeg", ".png", ".webp", ".tif", ".tiff"];

    /// <param name="bytes">The downloaded original.</param>
    /// <param name="extension">Its file extension, including the dot.</param>
    /// <param name="rotation">getInfo's `rotation`, in degrees clockwise.</param>
    /// <param name="reference">
    /// Any size Flickr renders itself (i.e. not "Original"), used as the right-way-up
    /// reference. Null disables the cross-check, leaving only the 180 case actionable.
    /// </param>
    /// <returns>The corrected bytes, or the input array unchanged when nothing was needed.</returns>
    public static byte[] Apply(byte[] bytes, string extension, int rotation, FlickrSize? reference)
    {
        var angle = Normalise(rotation);
        if (angle == 0 || !Rotatable.Contains(extension.ToLowerInvariant()))
        {
            return bytes;
        }

        using var image = Image.NewFromBuffer(bytes);
        var exifOrientation = ExifOrientation(image);

        if (!NeedsTurning(image, exifOrientation, angle, reference))
        {
            return bytes;
        }

        // Autorot first: it applies (and clears) any EXIF Orientation, so the turn below is
        // measured from how the photo actually reads rather than from the raw pixel grid,
        // and no stale tag survives to be applied a second time downstream.
        using var rotated = image.Autorot().Rot(ToAngle(angle));
        return rotated.WriteToBuffer(SaveOptions(extension));
    }

    private static bool NeedsTurning(Image image, int exifOrientation, int angle, FlickrSize? reference)
    {
        // 180 is invisible to an aspect-ratio check, so it rests on the flag alone - and only
        // where there is no EXIF orientation that `rotation` might be a restatement of.
        if (angle == 180)
        {
            return exifOrientation is 1;
        }

        if (reference is not { Width: > 0, Height: > 0 } || reference.Width == reference.Height)
        {
            return false;
        }

        // EXIF orientations 5-8 are the quarter turns; they swap how the file reads.
        var swapped = exifOrientation is >= 5 and <= 8;
        var readsPortrait = swapped ? image.Width > image.Height : image.Height > image.Width;

        return readsPortrait != (reference.Height > reference.Width);
    }

    private static int ExifOrientation(Image image)
    {
        // vips surfaces the tag as the `orientation` field, absent when the file has none.
        if (!image.Contains("orientation"))
        {
            return 1;
        }

        var value = image.Get("orientation");
        return value is int orientation and >= 1 and <= 8 ? orientation : 1;
    }

    private static int Normalise(int rotation)
    {
        var angle = ((rotation % 360) + 360) % 360;
        return angle is 90 or 180 or 270 ? angle : 0;
    }

    private static Enums.Angle ToAngle(int angle) => angle switch
    {
        90 => Enums.Angle.D90,
        180 => Enums.Angle.D180,
        _ => Enums.Angle.D270
    };

    // Q=95 keeps the re-encode visually lossless; the alternative (a lossless jpegtran-style
    // transform) can't be done for the non-JPEG cases anyway, so one path covers all of them.
    private static string SaveOptions(string extension) =>
        extension.ToLowerInvariant() is ".jpg" or ".jpeg"
            ? ".jpg[Q=95]"
            : extension.ToLowerInvariant();
}

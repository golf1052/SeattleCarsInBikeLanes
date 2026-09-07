using System.Globalization;

namespace SeattleCarsInBikeLanes.Mobile.Core.Camera;

/// <summary>
/// The zoom levels a camera can usefully be driven to, and the arithmetic for moving around them.
/// </summary>
/// <remarks>
/// This sits apart from the camera view so the fiddly parts, sanitising what the platform reports
/// and working out where the next tap should land, can be tested without a device.
/// </remarks>
public readonly record struct ZoomRange
{
    /// <summary>
    /// The highest zoom the app will offer, whatever the device claims to support.
    /// </summary>
    /// <remarks>
    /// iOS reports max video zoom factors well past 100x on recent hardware, and Android is not far
    /// behind. Nearly all of that is a digital crop that produces a photo too soft to read a plate
    /// off, and spreading it across a pinch makes the low end, which is the part anyone actually
    /// wants, impossible to land on.
    /// </remarks>
    public const float MaximumUsableZoom = 10f;

    /// <summary>
    /// Zoom factors closer together than this are treated as the same level.
    /// </summary>
    /// <remarks>
    /// Platforms report awkward numbers, so a telephoto whose minimum is 1.9999998 should not offer
    /// a 2x preset a hair above where it already is.
    /// </remarks>
    private const float Tolerance = 0.05f;

    private ZoomRange(float minimum, float maximum)
    {
        Minimum = minimum;
        Maximum = maximum;
    }

    /// <summary>
    /// A camera that cannot zoom.
    /// </summary>
    public static ZoomRange None { get; } = new ZoomRange(1f, 1f);

    /// <summary>
    /// Builds a range from what a camera reports about itself.
    /// </summary>
    /// <remarks>
    /// Nothing here can be trusted. Android falls back to 1.0/1.0 when CameraX has no zoom state to
    /// read, ranges have been seen inverted or zeroed, and a NaN would poison every comparison that
    /// followed it, so anything that does not make sense collapses to a camera that cannot zoom.
    /// </remarks>
    public static ZoomRange FromCamera(float minimum, float maximum)
    {
        if (!float.IsFinite(minimum) || !float.IsFinite(maximum) || minimum <= 0f || maximum <= 0f)
        {
            return None;
        }

        if (maximum < minimum)
        {
            return None;
        }

        maximum = Math.Min(maximum, MaximumUsableZoom);

        // The cap can land below a telephoto's own minimum, which would leave the range inverted.
        if (maximum <= minimum + Tolerance)
        {
            return new ZoomRange(minimum, minimum);
        }

        return new ZoomRange(minimum, maximum);
    }

    /// <summary>
    /// The widest the camera goes. Below 1 on an ultra wide lens.
    /// </summary>
    public float Minimum { get; }

    /// <summary>
    /// The closest the camera goes, never above <see cref="MaximumUsableZoom"/>.
    /// </summary>
    public float Maximum { get; }

    /// <summary>
    /// Whether there is any room to zoom at all.
    /// </summary>
    /// <remarks>
    /// False for front cameras on plenty of phones. The controls are hidden rather than shown doing
    /// nothing.
    /// </remarks>
    public bool CanZoom => Maximum > Minimum + Tolerance;

    /// <summary>
    /// Where the camera should sit when nothing has asked for anything else.
    /// </summary>
    /// <remarks>
    /// 1x is what every camera app opens at, so it is what the user expects to see, but a telephoto
    /// lens cannot go that wide and clamps to its own minimum.
    /// </remarks>
    public float Default => Clamp(1f);

    /// <summary>
    /// Pulls a zoom factor back into the range.
    /// </summary>
    public float Clamp(float value)
    {
        if (!float.IsFinite(value))
        {
            return Math.Clamp(1f, Minimum, Maximum);
        }

        return Math.Clamp(value, Minimum, Maximum);
    }

    /// <summary>
    /// The zoom levels the pill steps through.
    /// </summary>
    /// <remarks>
    /// The ends of the range are always offered so the user can get all the way out and all the way
    /// in with taps, and the familiar 1x and 2x stops are offered when the camera reaches them. The
    /// minimum is only worth a stop of its own when it is wider than 1x, since otherwise 1x already
    /// clamps to it.
    /// </remarks>
    public IReadOnlyList<float> Presets
    {
        get
        {
            if (!CanZoom)
            {
                return [Minimum];
            }

            List<float> presets = new List<float>(4);

            // Widest first so the list comes out in order. The minimum is always a stop, because it
            // is the only way back to the widest view on a lens like a 1.5x telephoto where neither
            // 1x nor 2x lands on it.
            float[] candidates = [Minimum, 1f, 2f, Maximum];

            foreach (float candidate in candidates)
            {
                if (candidate < Minimum - Tolerance || candidate > Maximum + Tolerance)
                {
                    continue;
                }

                float clamped = Clamp(candidate);
                bool alreadyOffered = false;
                foreach (float existing in presets)
                {
                    if (Math.Abs(existing - clamped) <= Tolerance)
                    {
                        alreadyOffered = true;
                        break;
                    }
                }

                if (!alreadyOffered)
                {
                    presets.Add(clamped);
                }
            }

            return presets;
        }
    }

    /// <summary>
    /// The zoom level a tap on the pill should move to.
    /// </summary>
    /// <remarks>
    /// Always moves in, and wraps back to the widest once there is nowhere further to go, so
    /// repeated taps cycle rather than dead ending at the top.
    /// </remarks>
    public float NextPreset(float current)
    {
        IReadOnlyList<float> presets = Presets;
        if (presets.Count == 0)
        {
            return Default;
        }

        float from = Clamp(current);

        foreach (float preset in presets)
        {
            if (preset > from + Tolerance)
            {
                return preset;
            }
        }

        return presets[0];
    }

    /// <summary>
    /// Formats a zoom factor the way a camera app labels it.
    /// </summary>
    /// <remarks>
    /// One decimal is enough to tell 1.5x from 2x, and a trailing zero on a whole number is noise
    /// in a control this small.
    /// </remarks>
    public static string Format(float value)
    {
        if (!float.IsFinite(value))
        {
            value = 1f;
        }

        float rounded = MathF.Round(value, 1);

        return Math.Abs(rounded - MathF.Round(rounded)) < 0.01f
            ? $"{MathF.Round(rounded).ToString("0", CultureInfo.InvariantCulture)}x"
            : $"{rounded.ToString("0.0", CultureInfo.InvariantCulture)}x";
    }
}

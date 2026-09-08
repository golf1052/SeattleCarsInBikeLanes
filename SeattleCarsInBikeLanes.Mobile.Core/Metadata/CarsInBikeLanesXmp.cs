using XmpCore;

namespace SeattleCarsInBikeLanes.Mobile.Core.Metadata;

/// <summary>
/// The custom XMP schema the app stamps into photos so it can tell, from the photo alone, whether
/// it has already been submitted.
/// </summary>
/// <remarks>
/// The photo is the source of truth for this state. The app does not keep a mirror of it for
/// photos it captured, so the flag has to survive round tripping through the Photos library.
/// </remarks>
public static class CarsInBikeLanesXmp
{
    /// <summary>
    /// The XMP namespace URI.
    /// </summary>
    /// <remarks>
    /// This must be a real URI. XMP identifies properties by namespace URI, and the prefix is only
    /// a serialization detail that readers are free to rewrite, so a bare token like "cbl" produces
    /// a packet that other tools will not agree with us about.
    /// </remarks>
    public const string NamespaceUri = "https://seattle.carinbikelane.com/ns/xmp/1.0/";

    /// <summary>
    /// Preferred prefix. XMP may hand back a different one if it is already taken.
    /// </summary>
    public const string Prefix = "cbl";

    /// <summary>
    /// Whether the photo has been submitted to the site.
    /// </summary>
    public const string UploadedProperty = "uploaded";

    /// <summary>
    /// When the photo was submitted, ISO 8601.
    /// </summary>
    public const string UploadedAtProperty = "uploadedAt";

    /// <summary>
    /// The submission the photo was part of, so a photo can be traced back to its report.
    /// </summary>
    public const string SubmissionIdProperty = "submissionId";

    private static readonly object RegistrationLock = new object();
    private static bool registered;

    /// <summary>
    /// Registers the schema with XMP's global registry. Safe to call more than once.
    /// </summary>
    public static void Register()
    {
        if (registered)
        {
            return;
        }

        lock (RegistrationLock)
        {
            if (registered)
            {
                return;
            }

            XmpMetaFactory.SchemaRegistry.RegisterNamespace(NamespaceUri, Prefix);
            registered = true;
        }
    }

    /// <summary>
    /// Reads the app's upload state out of an XMP packet.
    /// </summary>
    /// <remarks>
    /// A photo with no packet, no app properties, or a malformed value is simply not uploaded.
    /// Throwing here would mean a single odd photo could break the whole photo roll.
    /// </remarks>
    public static XmpUploadState Read(IXmpMeta? meta)
    {
        if (meta is null)
        {
            return XmpUploadState.NotUploaded;
        }

        Register();

        bool uploaded;
        try
        {
            // XmpCore dereferences the missing property rather than returning null, so asking
            // whether it exists first is the only safe way to read an optional value.
            uploaded = meta.DoesPropertyExist(NamespaceUri, UploadedProperty) &&
                meta.GetPropertyBoolean(NamespaceUri, UploadedProperty);
        }
        catch (XmpException)
        {
            return XmpUploadState.NotUploaded;
        }

        DateTimeOffset? uploadedAt = null;
        string? rawUploadedAt = TryGetString(meta, UploadedAtProperty);
        if (!string.IsNullOrWhiteSpace(rawUploadedAt) &&
            DateTimeOffset.TryParse(rawUploadedAt,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out DateTimeOffset parsed))
        {
            uploadedAt = parsed;
        }

        return new XmpUploadState(uploaded, uploadedAt, TryGetString(meta, SubmissionIdProperty));
    }

    /// <summary>
    /// Writes the app's upload state into an XMP packet, replacing anything already there.
    /// </summary>
    public static void Write(IXmpMeta meta, XmpUploadState state)
    {
        ArgumentNullException.ThrowIfNull(meta);

        Register();

        meta.SetPropertyBoolean(NamespaceUri, UploadedProperty, state.Uploaded);

        if (state.UploadedAt.HasValue)
        {
            meta.SetProperty(NamespaceUri, UploadedAtProperty,
                state.UploadedAt.Value.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            DeleteIfPresent(meta, UploadedAtProperty);
        }

        if (!string.IsNullOrWhiteSpace(state.SubmissionId))
        {
            meta.SetProperty(NamespaceUri, SubmissionIdProperty, state.SubmissionId);
        }
        else
        {
            DeleteIfPresent(meta, SubmissionIdProperty);
        }
    }

    /// <summary>
    /// Creates a packet carrying the given state.
    /// </summary>
    public static IXmpMeta Create(XmpUploadState state)
    {
        Register();
        IXmpMeta meta = XmpMetaFactory.Create();
        Write(meta, state);
        return meta;
    }

    /// <summary>
    /// Parses a raw XMP packet, returning null rather than throwing on malformed input.
    /// </summary>
    public static IXmpMeta? TryParse(byte[] packet)
    {
        if (packet is null || packet.Length == 0)
        {
            return null;
        }

        Register();

        try
        {
            return XmpMetaFactory.ParseFromBuffer(packet);
        }
        catch (XmpException)
        {
            return null;
        }
    }

    private static string? TryGetString(IXmpMeta meta, string property)
    {
        try
        {
            return meta.DoesPropertyExist(NamespaceUri, property)
                ? meta.GetPropertyString(NamespaceUri, property)
                : null;
        }
        catch (XmpException)
        {
            return null;
        }
    }

    private static void DeleteIfPresent(IXmpMeta meta, string property)
    {
        try
        {
            if (meta.DoesPropertyExist(NamespaceUri, property))
            {
                meta.DeleteProperty(NamespaceUri, property);
            }
        }
        catch (XmpException)
        {
            // Deleting a property that was never there is not a failure worth surfacing.
        }
    }
}

/// <summary>
/// The app's view of whether a photo has been submitted.
/// </summary>
public readonly record struct XmpUploadState(bool Uploaded, DateTimeOffset? UploadedAt, string? SubmissionId)
{
    public static XmpUploadState NotUploaded { get; } = new XmpUploadState(false, null, null);

    public static XmpUploadState UploadedNow(string? submissionId) =>
        new XmpUploadState(true, DateTimeOffset.UtcNow, submissionId);
}

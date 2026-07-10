namespace PhotoAlbum.Core.Domain;

/// <summary>A phone (or other portable device) currently connected over MTP/PTP.</summary>
public sealed class PhoneDevice
{
    /// <summary>WPD device id — opaque, stable while connected.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Human-readable name, e.g. "Demitri's iPhone".</summary>
    public required string FriendlyName { get; init; }

    /// <summary>Manufacturer as reported by the device (e.g. "Apple Inc.").</summary>
    public string? Manufacturer { get; init; }

    /// <summary>Model as reported (e.g. "iPhone").</summary>
    public string? Model { get; init; }

    /// <summary>Serial number when exposed; used to key per-device backup settings.</summary>
    public string? SerialNumber { get; init; }

    /// <summary>Stable key for settings/backup indexes: serial when present, else friendly name.</summary>
    public string StableKey => string.IsNullOrEmpty(SerialNumber) ? FriendlyName : SerialNumber!;
}

/// <summary>A photo or video enumerated from a connected phone.</summary>
public sealed class PhoneMediaItem
{
    /// <summary>Device-side object id (WPD) — used to download the content.</summary>
    public required string ItemId { get; init; }

    /// <summary>File name, e.g. IMG_0421.HEIC.</summary>
    public required string Name { get; init; }

    /// <summary>Device folder it lives in, e.g. "DCIM/104APPLE".</summary>
    public required string Folder { get; init; }

    public long SizeBytes { get; init; }
    public DateTime? DateTaken { get; init; }
    public bool IsVideo { get; init; }

    /// <summary>Set after backup/diff: BLAKE3 of the content (lower-hex).</summary>
    public string? Blake3Hash { get; set; }

    /// <summary>True when a file with the same hash already exists in a previous backup.</summary>
    public bool AlreadyBackedUp { get; set; }
}

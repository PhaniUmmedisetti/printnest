namespace PrintNest.Domain.Entities;

/// <summary>
/// A physical store location where a PrintNest kiosk is installed.
///
/// Created via POST /api/v1/admin/stores.
/// Devices are associated with stores via Device.StoreId.
///
/// Lat/Lng are used for the map view on the customer web frontend.
/// </summary>
public sealed class Store
{
    /// <summary>Unique identifier. Example: "store_hyd_001". Set by admin at creation.</summary>
    public string StoreId { get; init; } = string.Empty;

    /// <summary>Display name shown to customers. Example: "DMart Hitech City".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Full address shown on the map pin popup.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>Latitude for map pin. WGS84 decimal degrees.</summary>
    public double Latitude { get; set; }

    /// <summary>Longitude for map pin. WGS84 decimal degrees.</summary>
    public double Longitude { get; set; }

    /// <summary>
    /// When false, the store is hidden from the customer map and no new jobs can be released there.
    /// Does not affect jobs already in Released/Downloading/Printing state.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

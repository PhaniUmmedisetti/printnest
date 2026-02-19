using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrintNest.Infrastructure.Persistence;

namespace PrintNest.Api.Controllers.Public;

/// <summary>
/// Public store discovery endpoint — used by the customer map view.
///
/// Returns active stores with location data for the map pins.
/// No authentication required.
/// </summary>
[ApiController]
[Route("api/v1/public/stores")]
public sealed class StoresController : ControllerBase
{
    private readonly AppDbContext _db;

    public StoresController(AppDbContext db) => _db = db;

    /// <summary>
    /// Get all active stores for the customer map.
    /// GET /api/v1/public/stores
    ///
    /// Future: add ?lat=&lng=&radiusKm= for proximity filtering.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetActiveStores()
    {
        var stores = await _db.Stores
            .AsNoTracking()
            .Where(s => s.IsActive)
            .OrderBy(s => s.Name)
            .Select(s => new
            {
                s.StoreId,
                s.Name,
                s.Address,
                s.Latitude,
                s.Longitude
            })
            .ToListAsync();

        return Ok(stores);
    }
}

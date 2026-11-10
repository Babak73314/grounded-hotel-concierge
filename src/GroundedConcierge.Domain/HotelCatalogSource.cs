namespace GroundedConcierge.Domain;

/// <summary>
/// Where the room catalogue, the base rates and the staff's per-night
/// overrides come from. In a real deployment this is a property-management
/// system, a channel manager or a database; the engine never knows which,
/// because all it receives is an immutable <see cref="HotelCatalogSnapshot"/>.
/// </summary>
public interface IHotelCatalogSource
{
    /// <summary>The catalogue as it stands now.</summary>
    HotelCatalogSnapshot Load();
}

/// <summary>
/// The in-memory catalogue source: the built-in fictional rooms plus any
/// overrides a test or the demo adds. Every edit produces a new snapshot with
/// a higher version, exactly as a persistent store would.
/// </summary>
public sealed class InMemoryHotelCatalogSource : IHotelCatalogSource
{
    private readonly object _gate = new();
    private readonly IReadOnlyList<HotelRoomType> _rooms;
    private readonly Dictionary<(string Room, DateOnly Date), HotelDailyOverride> _overrides = new();
    private int _version;

    public InMemoryHotelCatalogSource(IReadOnlyList<HotelRoomType>? rooms = null)
    {
        _rooms = rooms ?? HotelCatalogSnapshot.BuiltInRooms();
    }

    /// <summary>Record a staff override for one room type on one night (an empty one removes it).</summary>
    public void SetOverride(HotelDailyOverride @override)
    {
        ArgumentNullException.ThrowIfNull(@override);
        lock (_gate)
        {
            var key = (HotelCatalogSnapshot.Normalize(@override.RoomCode), @override.Date);
            if (@override.IsEmpty) _overrides.Remove(key);
            else _overrides[key] = @override;
            _version++;
        }
    }

    public HotelCatalogSnapshot Load()
    {
        lock (_gate)
        {
            return new HotelCatalogSnapshot(_rooms, _overrides.Values.ToList(), _version, source: "in-memory");
        }
    }
}

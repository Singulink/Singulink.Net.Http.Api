namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// Keeps the total length of the open uploads as this server knows it: the lengths of the open uploads that the last cleanup counted, the lengths of the
/// uploads that this server created since, and the lengths that creations in progress have reserved. An upload that ends on this server, with an outcome
/// or a deletion, stops counting right away, and so does an upload that expires without a chunk that this server knows of. An upload whose chunks arrived
/// on another server stops counting then too, until the next count counts it again.
/// </summary>
internal sealed class UploadTotal
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, UploadLength> _created = new(StringComparer.Ordinal);
    private readonly HashSet<string> _endedDuringCount = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reserving = new(StringComparer.Ordinal);
    private Dictionary<string, UploadLength> _counted = new(StringComparer.Ordinal);
    private long _countedLength;
    private long _createdLength;
    private long _reserved;
    private bool _isCounting;

    /// <summary>
    /// Reserves the length of an upload that is being created, unless the total would then be more than the limit.
    /// </summary>
    /// <param name="uploadId">The ID of the upload.</param>
    /// <param name="length">The length of the upload.</param>
    /// <param name="limit">The limit of the total, or <see langword="null"/> for none.</param>
    /// <param name="nowUtc">The current time, at which uploads that received no chunk before their unstarted expiry stop counting.</param>
    /// <returns><see langword="true"/> if the length was reserved; otherwise, <see langword="false"/>.</returns>
    public bool TryReserve(string uploadId, long length, long? limit, DateTime nowUtc)
    {
        lock (_lock)
        {
            RemoveExpired(_created, ref _createdLength, nowUtc);
            RemoveExpired(_counted, ref _countedLength, nowUtc);

            if (limit is { } maxTotal && _countedLength + _createdLength + _reserved > maxTotal - length)
                return false;

            _reserved += length;
            _reserving.Add(uploadId);

            return true;
        }
    }

    /// <summary>
    /// Ends the reservation of an upload's length: adds the length to the total if the upload was created, and gives it back if not.
    /// </summary>
    public void EndReservation(string uploadId, UploadLength length, bool isCreated)
    {
        lock (_lock)
        {
            _reserved -= length.Length;
            _reserving.Remove(uploadId);

            if (isCreated)
            {
                _created[uploadId] = length;
                _createdLength += length.Length;
            }
        }
    }

    /// <summary>
    /// Records that a chunk of an upload arrived on this server, after which it no longer expires as an upload without a chunk.
    /// </summary>
    public void Start(string uploadId)
    {
        lock (_lock)
        {
            if (_created.TryGetValue(uploadId, out var created) && created.UnstartedExpiresUtc is not null)
                _created[uploadId] = created with { UnstartedExpiresUtc = null };

            if (_counted.TryGetValue(uploadId, out var counted) && counted.UnstartedExpiresUtc is not null)
                _counted[uploadId] = counted with { UnstartedExpiresUtc = null };
        }
    }

    /// <summary>
    /// Takes an upload that ended on this server, with an outcome or a deletion, out of the total.
    /// </summary>
    public void End(string uploadId)
    {
        lock (_lock)
        {
            if (_created.Remove(uploadId, out var length))
                _createdLength -= length.Length;

            if (_counted.Remove(uploadId, out length))
                _countedLength -= length.Length;

            // The count in progress can have found the upload before it ended.
            if (_isCounting)
                _endedDuringCount.Add(uploadId);
        }
    }

    /// <summary>
    /// Starts a cleanup's count of the open uploads.
    /// </summary>
    /// <returns>The IDs of the uploads that this server created so far, whose lengths the count covers once it ends.</returns>
    public HashSet<string> StartCount()
    {
        lock (_lock)
        {
            _isCounting = true;
            _endedDuringCount.Clear();

            return [.. _created.Keys];
        }
    }

    /// <summary>
    /// Gets a value indicating whether a count counts an open upload that it found: unless this server is creating it, or created it after the count
    /// started, since its length is in the total already.
    /// </summary>
    public bool IsCounted(string uploadId, HashSet<string> createdBeforeCount)
    {
        lock (_lock)
            return !_reserving.Contains(uploadId) && (!_created.ContainsKey(uploadId) || createdBeforeCount.Contains(uploadId));
    }

    /// <summary>
    /// Ends a count with the lengths of the open uploads that it counted, which replace the lengths of the uploads that this server had created before it
    /// started. The uploads that ended on this server while it counted are left out.
    /// </summary>
    public void EndCount(Dictionary<string, UploadLength> counted, HashSet<string> createdBeforeCount)
    {
        lock (_lock)
        {
            foreach (string uploadId in _endedDuringCount)
                counted.Remove(uploadId);

            _counted = counted;
            _countedLength = counted.Values.Sum(l => l.Length);

            foreach (string uploadId in createdBeforeCount)
            {
                if (_created.Remove(uploadId, out var length))
                    _createdLength -= length.Length;
            }

            _isCounting = false;
            _endedDuringCount.Clear();
        }
    }

    /// <summary>
    /// Ends a count that failed, which leaves the total as it was.
    /// </summary>
    public void AbortCount()
    {
        lock (_lock)
        {
            _isCounting = false;
            _endedDuringCount.Clear();
        }
    }

    private static void RemoveExpired(Dictionary<string, UploadLength> lengths, ref long total, DateTime nowUtc)
    {
        foreach (var (uploadId, length) in lengths)
        {
            if (length.UnstartedExpiresUtc <= nowUtc)
            {
                lengths.Remove(uploadId);
                total -= length.Length;
            }
        }
    }
}

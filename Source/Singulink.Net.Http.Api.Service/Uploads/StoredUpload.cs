namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// Represents an upload in an <see cref="IUploadStore"/> with its current state: its record, when its last chunk was written, the state of its handler's
/// run and its outcome.
/// </summary>
public sealed class StoredUpload
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StoredUpload"/> class.
    /// </summary>
    /// <param name="record">The upload's record.</param>
    /// <param name="lastWriteUtc">The time the upload's last chunk was written, or the time it was created if no chunk was written, in UTC.</param>
    /// <param name="runState">The state of the claim of the upload's handler run.</param>
    /// <param name="runStartedUtc">The time the run was claimed, in UTC, if <paramref name="runState"/> is not <see cref="UploadRunState.None"/>.</param>
    /// <param name="outcome">The upload's outcome, if it has one.</param>
    /// <param name="outcomeUtc">The time the outcome was stored, in UTC, if the upload has one.</param>
    /// <exception cref="ArgumentException">A run state other than <see cref="UploadRunState.None"/> has no <paramref name="runStartedUtc"/>, an outcome
    /// has no <paramref name="outcomeUtc"/>, or an upload has both an outcome and a run state other than <see cref="UploadRunState.None"/>.</exception>
    public StoredUpload(
        UploadRecord record,
        DateTime lastWriteUtc,
        UploadRunState runState = UploadRunState.None,
        DateTime? runStartedUtc = null,
        string? outcome = null,
        DateTime? outcomeUtc = null)
    {
        if (runState is not UploadRunState.None && runStartedUtc is null)
            throw new ArgumentException("A claimed run requires the time it was claimed.", nameof(runStartedUtc));

        if (outcome is not null && outcomeUtc is null)
            throw new ArgumentException("An outcome requires the time it was stored.", nameof(outcomeUtc));

        if (outcome is not null && runState is not UploadRunState.None)
            throw new ArgumentException("An upload with an outcome has no claimed run.", nameof(runState));

        Record = record;
        LastWriteUtc = lastWriteUtc;
        RunState = runState;
        RunStartedUtc = runStartedUtc;
        Outcome = outcome;
        OutcomeUtc = outcomeUtc;
    }

    /// <summary>
    /// Gets the upload's record.
    /// </summary>
    public UploadRecord Record { get; }

    /// <summary>
    /// Gets the time a write of the upload's chunks last started or ended, which is later than its creation, or the time it was created
    /// (<see cref="UploadRecord.CreatedUtc"/>) if no chunk was written, in UTC. The service keeps an upload that has received no chunk for
    /// <see cref="ChunkedUploadOptions.UnstartedExpiry"/>, and moves an upload's expiry with this time, so a chunk whose write takes long keeps its upload
    /// while it arrives.
    /// </summary>
    public DateTime LastWriteUtc { get; }

    /// <summary>
    /// Gets the state of the claim of the upload's handler run.
    /// </summary>
    public UploadRunState RunState { get; }

    /// <summary>
    /// Gets the time the upload's handler run was claimed, in UTC, or <see langword="null"/> if it is not claimed.
    /// </summary>
    public DateTime? RunStartedUtc { get; }

    /// <summary>
    /// Gets the upload's outcome, as <see cref="IUploadStore.TrySetOutcomeAsync"/> stored it, or <see langword="null"/> if it has none.
    /// </summary>
    public string? Outcome { get; }

    /// <summary>
    /// Gets the time the upload's outcome was stored, in UTC, or <see langword="null"/> if it has none.
    /// </summary>
    public DateTime? OutcomeUtc { get; }
}

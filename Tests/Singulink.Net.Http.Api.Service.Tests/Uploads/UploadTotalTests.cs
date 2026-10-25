using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The total length of the open uploads that a server keeps, between the counts of the cleanup.
/// </summary>
[PrefixTestClass]
public sealed class UploadTotalTests
{
    private static readonly DateTime NowUtc = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void UploadThatIsBeingCreatedWhenACountStarts_IsCountedOnce()
    {
        var total = new UploadTotal();
        total.TryReserve("a", 100, null, NowUtc).ShouldBeTrue();

        // The count finds the upload's record while its creation is still in progress.
        var createdBeforeCount = total.StartCount();
        total.IsCounted("a", createdBeforeCount).ShouldBeFalse();

        total.EndReservation("a", new UploadLength(100, null), isCreated: true);
        total.EndCount([], createdBeforeCount);

        total.TryReserve("b", 1, 100, NowUtc).ShouldBeFalse();
        total.TryReserve("c", 1, 101, NowUtc).ShouldBeTrue();
    }
}

using Singulink.Net.Http.Api.Service.Uploads;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// The values that the errors of chunked uploads name in their messages, as <see cref="UploadErrorData"/> gets them.
/// </summary>
[PrefixTestClass]
public sealed class UploadErrorDataTests
{
    [TestMethod]
    public void ErrorsWithValues_GiveThemWithTheirTypes()
    {
        UploadErrorData.TryGetMaxSize(UploadErrors.TooLarge(100), out long maxSize).ShouldBeTrue();
        maxSize.ShouldBe(100);

        UploadErrorData.TryGetMaxUploadsPerUser(UploadErrors.LimitReached(3), out int maxUploadsPerUser).ShouldBeTrue();
        maxUploadsPerUser.ShouldBe(3);

        UploadErrorData.TryGetMaxTotalSizePerUser(UploadErrors.SizeLimitReached(1000), out long reached).ShouldBeTrue();
        reached.ShouldBe(1000);

        UploadErrorData.TryGetMaxTotalSizePerUser(UploadErrors.ExceedsSizeLimit(2000), out long exceeded).ShouldBeTrue();
        exceeded.ShouldBe(2000);

        UploadErrorData.TryGetProperty(UploadErrors.FileNameInvalid(), out string? property).ShouldBeTrue();
        property.ShouldBe("fileName");
    }

    [TestMethod]
    public void ErrorsWithoutAValue_GiveNone()
    {
        var notFound = UploadErrors.NotFound();

        UploadErrorData.TryGetMaxSize(notFound, out _).ShouldBeFalse();
        UploadErrorData.TryGetMaxUploadsPerUser(notFound, out _).ShouldBeFalse();
        UploadErrorData.TryGetMaxTotalSizePerUser(UploadErrors.TooLarge(100), out _).ShouldBeFalse();
        UploadErrorData.TryGetProperty(notFound, out string? property).ShouldBeFalse();
        property.ShouldBeNull();
    }
}

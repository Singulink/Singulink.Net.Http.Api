namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// Copies of API exceptions with another message (see <see cref="ApiException.WithMessage"/>).
/// </summary>
[PrefixTestClass]
public sealed class ApiExceptionWithMessageTests
{
    public static IEnumerable<object[]> Exceptions => [
        [new BadRequestApiException("A.")],
        [new UnauthorizedApiException("A.")],
        [new ForbiddenApiException("A.")],
        [new NotFoundApiException("A.")],
        [new ConflictApiException("A.")],
        [new UserChangedApiException("A.")],
        [new ContentTooLargeApiException("A.")],
        [new UnsupportedMediaTypeApiException("A.")],
        [new ValidationApiException("A.")],
        [new UserRequiredApiException("A.")],
        [new TooManyRequestsApiException("A.")],
        [new ServerErrorApiException("A.")],
        [new ServiceUnavailableApiException("A.")],
        [new ApiException((HttpStatusCode)418, "A.")],
    ];

    [TestMethod]
    [DynamicData(nameof(Exceptions))]
    public void Copy_HasTheTypeAndStatusOfTheException(ApiException exception)
    {
        var copy = exception.WithMessage("B.");

        copy.GetType().ShouldBe(exception.GetType());
        copy.StatusCode.ShouldBe(exception.StatusCode);
        copy.Message.ShouldBe("B.");
    }

    [TestMethod]
    public void Copy_KeepsEverythingButTheMessage()
    {
        var inner = new IOException("The disk failed.");
        var referenceId = Guid.NewGuid();
        var content = new ApiErrorContent("<html>Down for maintenance</html>", "text/html");
        var exception = new ServiceUnavailableApiException("Not now.", inner) {
            ErrorCode = "upload-storage-full", RetryAfter = TimeSpan.FromMinutes(1), ReferenceId = referenceId, ErrorContent = content,
        };

        exception.Data["MaxSize"] = 100L;

        var copy = exception.WithMessage("There is no room at the moment.");

        copy.ShouldBeOfType<ServiceUnavailableApiException>();
        copy.Message.ShouldBe("There is no room at the moment.");
        copy.ErrorCode.ShouldBe("upload-storage-full");
        copy.RetryAfter.ShouldBe(TimeSpan.FromMinutes(1));
        copy.ReferenceId.ShouldBe(referenceId);
        copy.InnerException.ShouldBeSameAs(inner);
        copy.ErrorContent.ShouldBeSameAs(content);
        copy.Data["MaxSize"].ShouldBe(100L);
        exception.Message.ShouldBe("Not now.");

        // The copy's data is its own.
        copy.Data["Other"] = 1;
        exception.Data.Contains("Other").ShouldBeFalse();
    }

    [TestMethod]
    public void Copy_OfAUserChangedError_KeepsTheExpectedUserId()
    {
        var exception = new UserChangedApiException("The user changed.") { ErrorCode = "the-code", ExpectedUserId = "5" };

        var copy = exception.WithMessage("Sign in again.");

        copy.ShouldBeOfType<UserChangedApiException>().ExpectedUserId.ShouldBe("5");
        copy.ErrorCode.ShouldBe("the-code");
    }

    [TestMethod]
    public async Task Copy_OfARedirect_KeepsTheLocationAndTheContent()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => {
            var response = Responses.Content(HttpStatusCode.Found, "Moved.", "text/plain");
            response.Headers.Location = new Uri("https://cdn.example.com/files/1");
            return response;
        }));

        var exception = await Should.ThrowAsync<ApiException>(() => client.SendRequestAsync(client.CreateGetRequest("files/1")));
        var copy = exception.WithMessage("The file has moved.");

        copy.Location.ShouldBe(new Uri("https://cdn.example.com/files/1"));
        copy.ErrorContent.ShouldNotBeNull().Content.ShouldBe("Moved.");
    }
}

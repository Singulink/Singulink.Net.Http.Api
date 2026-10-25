namespace Singulink.Net.Http.Api.Client;

[PrefixTestClass]
public sealed class ApiErrorResponseTests
{
    [TestMethod]
    public void Response_HasStatusContentTypeAndBody()
    {
        using var response = ApiErrorResponse.Create(new NotFoundApiException("The document does not exist.") { ErrorCode = "document-missing" });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.ToString().ShouldBe("text/plain; charset=utf-8; format=error-code");
        response.Headers.RetryAfter.ShouldBeNull();
    }

    [TestMethod]
    public async Task ResponseWithoutErrorCode_HasPlainMessage()
    {
        using var response = ApiErrorResponse.Create(new ForbiddenApiException("No access."));

        response.Content.Headers.ContentType!.Parameters.ShouldNotContain(p => p.Name == "format");
        (await response.Content.ReadAsStringAsync()).ShouldBe("No access.");
    }

    [TestMethod]
    public void ReferenceId_IsInItsHeader()
    {
        var referenceId = Guid.NewGuid();
        using var response = ApiErrorResponse.Create(new ServerErrorApiException("Failed.") { ReferenceId = referenceId });
        using var withoutReferenceId = ApiErrorResponse.Create(new ServerErrorApiException("Failed."));

        response.Headers.GetValues("Reference-Id").ShouldBe([referenceId.ToString("D")]);
        withoutReferenceId.Headers.Contains("Reference-Id").ShouldBeFalse();
    }

    [TestMethod]
    public void RetryAfter_IsInWholeSecondsRoundedUp()
    {
        using var response = ApiErrorResponse.Create(new TooManyRequestsApiException("Slow down.") { RetryAfter = TimeSpan.FromSeconds(1.2) });

        response.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task RetryAfterLongerThanTheHeaderCanHold_IsLimitedToIt()
    {
        foreach (var retryAfter in new[] { TimeSpan.MaxValue, TimeSpan.FromSeconds(3_000_000_000) })
        {
            using var response = ApiErrorResponse.Create(new ServiceUnavailableApiException("Closed for good.") { RetryAfter = retryAfter });
            response.Headers.RetryAfter!.ToString().ShouldBe("2147483647");

            var client = new TestApiClient(new FakeHttpMessageHandler(_ => ApiErrorResponse.Create(
                new ServiceUnavailableApiException("Closed for good.") { RetryAfter = retryAfter })));

            var ex = await Should.ThrowAsync<ServiceUnavailableApiException>(() => client.SendRequestAsync(client.CreateGetRequest("items")));
            ex.RetryAfter.ShouldBe(TimeSpan.FromSeconds(int.MaxValue));
        }
    }

    [TestMethod]
    public async Task Client_ThrowsTheExceptionTheResponseWasCreatedFrom()
    {
        ApiException[] exceptions = [
            new NotFoundApiException("The document does not exist.") { ErrorCode = "document-missing" },
            new UnauthorizedApiException("Sign in again.") { ErrorCode = ApiErrorCodes.SignInRequired },
            new TooManyRequestsApiException("Slow down.") { ErrorCode = ApiErrorCodes.RateLimited, RetryAfter = TimeSpan.FromSeconds(30) },
            new ValidationApiException("The name is required."),
            new ServiceUnavailableApiException("Starting.") { ErrorCode = ApiErrorCodes.CloudflareRangesUnavailable, RetryAfter = TimeSpan.FromSeconds(5) },
            new ApiException((HttpStatusCode)418, "I'm a teapot.") { ErrorCode = "teapot" },
            new ServerErrorApiException("Something went wrong.") { ReferenceId = Guid.NewGuid() },
        ];

        foreach (var expected in exceptions)
        {
            var client = new TestApiClient(new FakeHttpMessageHandler(_ => ApiErrorResponse.Create(expected)));

            var ex = await Should.ThrowAsync<ApiException>(() => client.SendRequestAsync(client.CreateGetRequest("items")));

            ex.GetType().ShouldBe(expected.GetType());
            ex.StatusCode.ShouldBe(expected.StatusCode);
            ex.Message.ShouldBe(expected.Message);
            ex.ErrorCode.ShouldBe(expected.ErrorCode);
            ex.RetryAfter.ShouldBe(expected.RetryAfter);
            ex.ReferenceId.ShouldBe(expected.ReferenceId);
            ex.ErrorContent.ShouldBeNull();
        }
    }
}

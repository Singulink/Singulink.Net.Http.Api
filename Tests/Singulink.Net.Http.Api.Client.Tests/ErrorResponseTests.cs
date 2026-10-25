namespace Singulink.Net.Http.Api.Client;

[PrefixTestClass]
public sealed class ErrorResponseTests
{
    [TestMethod]
    [DataRow(400, typeof(BadRequestApiException))]
    [DataRow(401, typeof(UnauthorizedApiException))]
    [DataRow(403, typeof(ForbiddenApiException))]
    [DataRow(404, typeof(NotFoundApiException))]
    [DataRow(409, typeof(ConflictApiException))]
    [DataRow(412, typeof(UserChangedApiException))]
    [DataRow(413, typeof(ContentTooLargeApiException))]
    [DataRow(415, typeof(UnsupportedMediaTypeApiException))]
    [DataRow(422, typeof(ValidationApiException))]
    [DataRow(428, typeof(UserRequiredApiException))]
    [DataRow(429, typeof(TooManyRequestsApiException))]
    [DataRow(500, typeof(ServerErrorApiException))]
    [DataRow(503, typeof(ServiceUnavailableApiException))]
    [DataRow(418, typeof(ApiException))]
    [DataRow(502, typeof(ApiException))]
    public async Task TextError_ThrowsMatchingApiException(int status, Type exceptionType)
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => Responses.Error((HttpStatusCode)status, "The error message.")));

        var ex = await Should.ThrowAsync<ApiException>(() => client.SendRequestAsync<TestItem>(client.CreateGetRequest("items/5")));

        ex.ShouldBeOfType(exceptionType);
        ex.StatusCode.ShouldBe((HttpStatusCode)status);
        ex.Message.ShouldBe("The error message.");
        ex.ErrorCode.ShouldBeNull();
        ex.ErrorContent.ShouldBeNull();
    }

    [TestMethod]
    [DataRow(400, typeof(BadRequestApiException))]
    [DataRow(401, typeof(UnauthorizedApiException))]
    [DataRow(403, typeof(ForbiddenApiException))]
    [DataRow(404, typeof(NotFoundApiException))]
    [DataRow(409, typeof(ConflictApiException))]
    [DataRow(412, typeof(UserChangedApiException))]
    [DataRow(413, typeof(ContentTooLargeApiException))]
    [DataRow(415, typeof(UnsupportedMediaTypeApiException))]
    [DataRow(422, typeof(ValidationApiException))]
    [DataRow(428, typeof(UserRequiredApiException))]
    [DataRow(429, typeof(TooManyRequestsApiException))]
    [DataRow(500, typeof(ServerErrorApiException))]
    [DataRow(503, typeof(ServiceUnavailableApiException))]
    [DataRow(418, typeof(ApiException))]
    [DataRow(502, typeof(ApiException))]
    public async Task ErrorCode_IsSplitFromMessage(int status, Type exceptionType)
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ =>
            Responses.Error((HttpStatusCode)status, "email-confirmation-required", "Please confirm your email address before signing in.")));

        var ex = await Should.ThrowAsync<ApiException>(() => client.SendRequestAsync(client.CreatePostRequest("sign-in")));

        ex.ShouldBeOfType(exceptionType);
        ex.StatusCode.ShouldBe((HttpStatusCode)status);
        ex.ErrorCode.ShouldBe("email-confirmation-required");
        ex.Message.ShouldBe("Please confirm your email address before signing in.");
    }

    [TestMethod]
    public async Task ErrorCode_EmptyBrackets_HasNoErrorCode()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ =>
            Responses.Content(HttpStatusCode.Forbidden, "[] Not allowed.", "text/plain; format=error-code")));

        var ex = await Should.ThrowAsync<ForbiddenApiException>(() => client.SendRequestAsync(client.CreateGetRequest("items")));

        ex.ErrorCode.ShouldBeNull();
        ex.Message.ShouldBe("Not allowed.");
    }

    [TestMethod]
    public async Task ErrorCode_MissingBrackets_ThrowsGenericErrorWithItsStatus()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ =>
            Responses.Content(HttpStatusCode.Forbidden, "Not allowed.", "text/plain; format=error-code")));

        var ex = await Should.ThrowAsync<ForbiddenApiException>(() => client.SendRequestAsync(client.CreateGetRequest("items")));

        ex.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        ex.Message.ShouldBe("The service returned an error (403 Forbidden).");
        ex.ErrorCode.ShouldBeNull();
        ex.ErrorContent.ShouldNotBeNull().Content.ShouldBe("Not allowed.");
    }

    [TestMethod]
    public async Task ErrorCode_InvalidCodeInBrackets_ThrowsGenericErrorWithItsStatus()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ =>
            Responses.Content(HttpStatusCode.Forbidden, "[not a code] Not allowed.", "text/plain; format=error-code")));

        var ex = await Should.ThrowAsync<ForbiddenApiException>(() => client.SendRequestAsync(client.CreateGetRequest("items")));

        ex.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        ex.Message.ShouldBe("The service returned an error (403 Forbidden).");
        ex.ErrorCode.ShouldBeNull();
        ex.ErrorContent.ShouldNotBeNull().Content.ShouldBe("[not a code] Not allowed.");
    }

    [TestMethod]
    public async Task EmptyTextError_HasGenericMessage()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => Responses.Error(HttpStatusCode.NotFound, string.Empty)));

        var ex = await Should.ThrowAsync<NotFoundApiException>(() => client.SendRequestAsync(client.CreateGetRequest("items/5")));

        ex.Message.ShouldBe("The service returned an error (404 Not Found).");
    }

    [TestMethod]
    public async Task NonTextError_ProvidesErrorContent()
    {
        const string html = "<html><body>Bad Gateway</body></html>";
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => Responses.Content(HttpStatusCode.BadGateway, html, "text/html")));

        var ex = await Should.ThrowAsync<ApiException>(() => client.SendRequestAsync(client.CreateGetRequest("items")));

        ex.ShouldBeOfType<ApiException>();
        ex.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        ex.Message.ShouldBe("The service returned an error (502 Bad Gateway).");
        ex.ErrorCode.ShouldBeNull();

        var errorContent = ex.ErrorContent.ShouldNotBeNull();
        errorContent.Content.ShouldBe(html);
        errorContent.ContentType.ShouldBe("text/html");
    }

    [TestMethod]
    [DataRow(401, typeof(UnauthorizedApiException), "401 Unauthorized")]
    [DataRow(429, typeof(TooManyRequestsApiException), "429 Too Many Requests")]
    [DataRow(500, typeof(ServerErrorApiException), "500 Internal Server Error")]
    [DataRow(502, typeof(ApiException), "502 Bad Gateway")]
    [DataRow(503, typeof(ServiceUnavailableApiException), "503 Service Unavailable")]
    [DataRow(504, typeof(ApiException), "504 Gateway Timeout")]
    [DataRow(520, typeof(ApiException), "520")]
    public async Task NonTextError_KeepsItsStatus(int status, Type exceptionType, string statusText)
    {
        const string html = "<html><body>Proxy error</body></html>";
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => Responses.Content((HttpStatusCode)status, html, "text/html")));

        var ex = await Should.ThrowAsync<ApiException>(() => client.SendRequestAsync(client.CreateGetRequest("items")));

        ex.ShouldBeOfType(exceptionType);
        ex.StatusCode.ShouldBe((HttpStatusCode)status);
        ex.Message.ShouldBe($"The service returned an error ({statusText}).");
        ex.ErrorContent.ShouldNotBeNull().Content.ShouldBe(html);
    }

    [TestMethod]
    public async Task EmptyNonTextError_KeepsItsStatus()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) {
            Content = new ByteArrayContent([]),
        }));

        var ex = await Should.ThrowAsync<ServiceUnavailableApiException>(() => client.SendRequestAsync(client.CreateGetRequest("items")));

        ex.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        ex.Message.ShouldBe("The service returned an error (503 Service Unavailable).");
        ex.ErrorContent.ShouldNotBeNull().Content.ShouldBeEmpty();
    }

    [TestMethod]
    [DataRow(429)]
    [DataRow(503)]
    [DataRow(409)]
    [DataRow(418)]
    public async Task RetryAfterSeconds_SetsRetryAfter(int status)
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ =>
            Responses.Error((HttpStatusCode)status, "rate-limited", "Slow down.").WithRetryAfter("120")));

        var ex = await Should.ThrowAsync<ApiException>(() => client.SendRequestAsync(client.CreateGetRequest("items")));

        ex.StatusCode.ShouldBe((HttpStatusCode)status);
        ex.RetryAfter.ShouldBe(TimeSpan.FromMinutes(2));
    }

    [TestMethod]
    public async Task RetryAfterDate_CountsFromTheResponseDate()
    {
        // The response date is far from the client's clock, which must not matter.
        var responseDate = new DateTimeOffset(2020, 1, 1, 12, 0, 0, TimeSpan.Zero);

        var client = new TestApiClient(new FakeHttpMessageHandler(_ => {
            var response = Responses.Error(HttpStatusCode.ServiceUnavailable, "Down for maintenance.").WithRetryAfter("Wed, 01 Jan 2020 12:01:30 GMT");
            response.Headers.Date = responseDate;
            return response;
        }));

        var ex = await Should.ThrowAsync<ServiceUnavailableApiException>(() => client.SendRequestAsync(client.CreateGetRequest("items")));

        ex.RetryAfter.ShouldBe(TimeSpan.FromSeconds(90));
    }

    [TestMethod]
    public async Task RetryAfterDate_WithoutResponseDate_CountsFromNow()
    {
        string date = DateTimeOffset.UtcNow.AddMinutes(10).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => Responses.Error((HttpStatusCode)429, "Slow down.").WithRetryAfter(date)));

        var ex = await Should.ThrowAsync<TooManyRequestsApiException>(() => client.SendRequestAsync(client.CreateGetRequest("items")));

        ex.RetryAfter.ShouldNotBeNull().ShouldBeInRange(TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(10));
    }

    [TestMethod]
    public async Task RetryAfterDate_InThePast_IsZero()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ =>
            Responses.Error((HttpStatusCode)429, "Slow down.").WithRetryAfter("Wed, 01 Jan 2020 12:00:00 GMT")));

        var ex = await Should.ThrowAsync<TooManyRequestsApiException>(() => client.SendRequestAsync(client.CreateGetRequest("items")));

        ex.RetryAfter.ShouldBe(TimeSpan.Zero);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("soon")]
    public async Task MissingOrInvalidRetryAfter_LeavesRetryAfterUnset(string? retryAfter)
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => {
            var response = Responses.Error((HttpStatusCode)429, "Slow down.");
            return retryAfter is null ? response : response.WithRetryAfter(retryAfter);
        }));

        var ex = await Should.ThrowAsync<TooManyRequestsApiException>(() => client.SendRequestAsync(client.CreateGetRequest("items")));

        ex.RetryAfter.ShouldBeNull();
    }

    [TestMethod]
    public async Task ReferenceIdHeader_SetsReferenceId()
    {
        var referenceId = Guid.NewGuid();
        var client = new TestApiClient(new FakeHttpMessageHandler(_ =>
            Responses.Error(HttpStatusCode.InternalServerError, "Failed.").WithHeader("Reference-Id", referenceId.ToString("D"))));

        var ex = await Should.ThrowAsync<ServerErrorApiException>(() => client.SendRequestAsync(client.CreateGetRequest("items")));

        ex.ReferenceId.ShouldBe(referenceId);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("not-a-guid")]
    public async Task MissingOrInvalidReferenceId_LeavesReferenceIdUnset(string? referenceId)
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => {
            var response = Responses.Error(HttpStatusCode.Conflict, "Conflict.");
            return referenceId is null ? response : response.WithHeader("Reference-Id", referenceId);
        }));

        var ex = await Should.ThrowAsync<ConflictApiException>(() => client.SendRequestAsync(client.CreateGetRequest("items")));

        ex.ReferenceId.ShouldBeNull();
    }

    [TestMethod]
    public async Task NonTextError_KeepsReferenceId()
    {
        var referenceId = Guid.NewGuid();
        var client = new TestApiClient(new FakeHttpMessageHandler(_ =>
            Responses.Content(HttpStatusCode.BadGateway, "<html>Bad gateway</html>", "text/html").WithHeader("Reference-Id", referenceId.ToString())));

        var ex = await Should.ThrowAsync<ApiException>(() => client.SendRequestAsync(client.CreateGetRequest("items")));

        ex.ReferenceId.ShouldBe(referenceId);
    }

    [TestMethod]
    public async Task NonTextError_KeepsRetryAfter()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ =>
            Responses.Content(HttpStatusCode.ServiceUnavailable, "<html>Down</html>", "text/html").WithRetryAfter("30")));

        var ex = await Should.ThrowAsync<ServiceUnavailableApiException>(() => client.SendRequestAsync(client.CreateGetRequest("items")));

        ex.RetryAfter.ShouldBe(TimeSpan.FromSeconds(30));
        ex.ErrorContent.ShouldNotBeNull();
    }

    [TestMethod]
    public async Task ErrorResponse_ForStringResponse_Throws()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => Responses.Error(HttpStatusCode.Unauthorized, "User is not signed in.")));

        var ex = await Should.ThrowAsync<UnauthorizedApiException>(() => client.SendRequestAsync<string>(client.CreateGetRequest("text")));

        ex.Message.ShouldBe("User is not signed in.");
    }

    [TestMethod]
    [DataRow(301, "301 Moved Permanently")]
    [DataRow(302, "302 Found")]
    [DataRow(303, "303 See Other")]
    [DataRow(307, "307 Temporary Redirect")]
    [DataRow(308, "308 Permanent Redirect")]
    public async Task Redirect_ThrowsWithLocation(int status, string statusText)
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => {
            var response = Responses.Content((HttpStatusCode)status, "Moved.", "text/plain");
            response.Headers.Location = new Uri("https://cdn.example.com/files/1?sig=abc");
            return response;
        }));

        var ex = await Should.ThrowAsync<ApiException>(() => client.SendRequestAsync(client.CreateGetRequest("files/1")));

        ex.ShouldBeOfType<ApiException>();
        ex.StatusCode.ShouldBe((HttpStatusCode)status);
        ex.Message.ShouldBe($"The service redirected the request to https://cdn.example.com/files/1 ({statusText}), and redirects are not followed.");
        ex.Location.ShouldBe(new Uri("https://cdn.example.com/files/1?sig=abc"));
        ex.ErrorCode.ShouldBeNull();

        var errorContent = ex.ErrorContent.ShouldNotBeNull();
        errorContent.Content.ShouldBe("Moved.");
        errorContent.ContentType.ShouldBe("text/plain");
    }

    [TestMethod]
    public async Task Redirect_RelativeLocation_IsResolvedAgainstRequest()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect) { Content = new ByteArrayContent([]) };
            response.Headers.Location = new Uri("../v2/items", UriKind.Relative);
            return response;
        }));

        var ex = await Should.ThrowAsync<ApiException>(() => client.SendRequestAsync(client.CreateGetRequest("items")));

        ex.Message.ShouldBe("The service redirected the request to https://api.example.com/v2/items (302 Found), and redirects are not followed.");
        ex.Location.ShouldBe(new Uri("https://api.example.com/v2/items"));
    }

    [TestMethod]
    public async Task Redirect_MessageLeavesOutUserInfoQueryAndFragment()
    {
        const string location = "https://user:secret@cdn.example.com:8443/files/1?X-Amz-Signature=abc#part";

        var client = new TestApiClient(new FakeHttpMessageHandler(_ => {
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Content = new ByteArrayContent([]) };
            response.Headers.Location = new Uri(location);
            return response;
        }));

        var ex = await Should.ThrowAsync<ApiException>(() => client.SendRequestAsync(client.CreateGetRequest("files/1")));

        ex.Message.ShouldBe(
            "The service redirected the request to https://cdn.example.com:8443/files/1 (307 Temporary Redirect), and redirects are not followed.");
        ex.Location.ShouldBe(new Uri(location));
    }

    [TestMethod]
    public async Task RedirectWithoutLocation_NamesStatus()
    {
        var client = new TestApiClient(
            new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect) { Content = new ByteArrayContent([]) }));

        var ex = await Should.ThrowAsync<ApiException>(() => client.SendRequestAsync(client.CreateGetRequest("items")));

        ex.Message.ShouldBe("The service responded with 302 Found.");
        ex.Location.ShouldBeNull();
    }

    [TestMethod]
    public async Task NotModified_IsNotReportedAsErrorOrRedirect()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler(_ => {
            var response = new HttpResponseMessage(HttpStatusCode.NotModified) { Content = new ByteArrayContent([]) };
            response.Headers.Location = new Uri("https://api.example.com/v1/items");
            return response;
        }));

        var ex = await Should.ThrowAsync<ApiException>(() => client.SendRequestAsync(client.CreateGetRequest("items")));

        ex.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        ex.Message.ShouldBe("The service responded with 304 Not Modified.");
    }
}

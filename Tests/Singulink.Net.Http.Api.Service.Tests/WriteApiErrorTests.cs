using Microsoft.AspNetCore.Http.Features;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// <see cref="HttpResponseExtensions.WriteApiErrorAsync"/> writes an exception in the toolkit's error format, with the retry time in the
/// <c>Retry-After</c> header.
/// </summary>
[PrefixTestClass]
public sealed class WriteApiErrorTests
{
    [TestMethod]
    public async Task WritesStatusContentAndRetryAfter()
    {
        var context = CreateContext();
        var exception = new TooManyRequestsApiException("Slow down.") { ErrorCode = "the-code", RetryAfter = TimeSpan.FromSeconds(90) };

        await context.Response.WriteApiErrorAsync(exception);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        context.Response.ContentType.ShouldBe("text/plain; charset=utf-8; format=error-code");
        context.Response.Headers.RetryAfter.ToString().ShouldBe("90");
        ReadBody(context).ShouldBe("[the-code] Slow down.");
    }

    [TestMethod]
    public async Task WithoutErrorCodeOrRetryAfter_WritesPlainMessageOnly()
    {
        var context = CreateContext();

        await context.Response.WriteApiErrorAsync(new ConflictApiException("Already exists."));

        context.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        context.Response.ContentType.ShouldBe("text/plain; charset=utf-8");
        context.Response.Headers.ContainsKey("Retry-After").ShouldBeFalse();
        context.Response.Headers.ContainsKey("Reference-Id").ShouldBeFalse();
        ReadBody(context).ShouldBe("Already exists.");
    }

    [TestMethod]
    public async Task ReferenceId_IsWrittenInItsHeader()
    {
        var context = CreateContext();
        var referenceId = Guid.CreateVersion7();

        await context.Response.WriteApiErrorAsync(new ServerErrorApiException("Failed.") { ReferenceId = referenceId });

        context.Response.Headers["Reference-Id"].ToString().ShouldBe(referenceId.ToString("D"));
        ReadBody(context).ShouldBe("Failed.");
    }

    [TestMethod]
    public async Task RetryAfterLongerThanAnInt32NumberOfSeconds_IsLimitedToIt()
    {
        var context = CreateContext();

        await context.Response.WriteApiErrorAsync(new ServiceUnavailableApiException("Closed for good.") { RetryAfter = TimeSpan.MaxValue });

        context.Response.Headers.RetryAfter.ToString().ShouldBe("2147483647");
    }

    [TestMethod]
    [DataRow(0.0, "0")]
    [DataRow(0.2, "1")]
    [DataRow(1.0, "1")]
    [DataRow(1.5, "2")]
    public async Task RetryAfter_IsWrittenInWholeSecondsRoundedUp(double seconds, string expected)
    {
        var context = CreateContext();

        await context.Response.WriteApiErrorAsync(new ServiceUnavailableApiException("Starting.") { RetryAfter = TimeSpan.FromSeconds(seconds) });

        context.Response.Headers.RetryAfter.ToString().ShouldBe(expected);
    }

    [TestMethod]
    public async Task StartedResponse_Throws()
    {
        var context = CreateContext();
        var responseFeature = new TestResponseFeature();
        context.Features.Set<IHttpResponseFeature>(responseFeature);
        responseFeature.MarkStarted();

        await Should.ThrowAsync<InvalidOperationException>(() => context.Response.WriteApiErrorAsync(new BadRequestApiException("Bad.")));
    }

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        return context;
    }

    private static string ReadBody(DefaultHttpContext context) => System.Text.Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
}

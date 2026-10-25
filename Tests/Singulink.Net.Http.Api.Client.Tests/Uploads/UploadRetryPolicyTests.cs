namespace Singulink.Net.Http.Api.Client;

[PrefixTestClass]
public sealed class UploadRetryPolicyTests
{
    public static IEnumerable<object[]> RetryableFailures => [
        [new HttpRequestException("The connection was refused.")],
        [new TaskCanceledException("Timed out.", new TimeoutException())],
        [new ServiceUnavailableApiException("Busy.")],
        [new ApiException(HttpStatusCode.RequestTimeout, "The request body arrived too slowly.")],
        [new TooManyRequestsApiException("Wait.") { ErrorCode = ApiErrorCodes.RateLimited, RetryAfter = TimeSpan.FromSeconds(5) }],
        [new ApiException(HttpStatusCode.BadGateway, "Bad gateway.")],
    ];

    public static IEnumerable<object[]> FinalFailures => [
        [new TooManyRequestsApiException("Too many uploads.")],
        [new BadRequestApiException("The chunk is not valid.")],
        [new NotFoundApiException("The upload does not exist.")],
        [new ConflictApiException("The upload is closed.")],
        [new UnauthorizedApiException("Sign in again.") { ErrorCode = ApiErrorCodes.SignInRequired }],
        [new IOException("The file can't be read.")],
        [new OperationCanceledException()],
    ];

    [TestMethod]
    [DynamicData(nameof(RetryableFailures))]
    public void RetryableFailure_IsRetryable(Exception exception) => UploadRetryPolicy.IsRetryable(exception).ShouldBeTrue();

    [TestMethod]
    [DynamicData(nameof(FinalFailures))]
    public void OtherFailure_IsNotRetryable(Exception exception)
    {
        UploadRetryPolicy.IsRetryable(exception).ShouldBeFalse();
        new UploadRetryPolicy(TimeSpan.FromMinutes(5), new ManualTimeProvider()).GetRetryDelay(exception, 1).ShouldBeNull();
    }

    [TestMethod]
    [DataRow(1, 1.0)]
    [DataRow(2, 2.0)]
    [DataRow(3, 4.0)]
    [DataRow(4, 8.0)]
    [DataRow(5, 16.0)]
    [DataRow(6, 30.0)]
    [DataRow(7, 30.0)]
    [DataRow(1000, 30.0)]
    public void Backoff_DoublesFromOneSecondUpToThirtySeconds(int failedAttempts, double seconds)
    {
        UploadRetryPolicy.GetBackoff(failedAttempts, 0).ShouldBe(TimeSpan.FromSeconds(seconds));
    }

    [TestMethod]
    public void Backoff_IsReducedByUpToHalfAtRandom()
    {
        UploadRetryPolicy.GetBackoff(2, 0.5).ShouldBe(TimeSpan.FromSeconds(1.5));
        UploadRetryPolicy.GetBackoff(6, 0.999999).TotalSeconds.ShouldBe(15, tolerance: 0.001);
    }

    [TestMethod]
    public void RetryDelay_IsTheBackoffWithJitter()
    {
        var policy = new UploadRetryPolicy(TimeSpan.FromMinutes(5), new ManualTimeProvider());

        for (int i = 0; i < 100; i++)
        {
            var delay = policy.GetRetryDelay(new HttpRequestException(), 3).ShouldNotBeNull();
            delay.ShouldBeGreaterThan(TimeSpan.FromSeconds(2));
            delay.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(4));
        }
    }

    [TestMethod]
    public void RetryDelay_FollowsRetryAfter()
    {
        var policy = new UploadRetryPolicy(TimeSpan.FromMinutes(5), new ManualTimeProvider());
        var exception = new ServiceUnavailableApiException("Full.") { RetryAfter = TimeSpan.FromSeconds(45) };

        policy.GetRetryDelay(exception, 1).ShouldBe(TimeSpan.FromSeconds(45));
        policy.GetRetryDelay(exception, 9).ShouldBe(TimeSpan.FromSeconds(45));
    }

    [TestMethod]
    public void RetryDelay_WithZeroRetryAfter_IsTheBackoff()
    {
        var policy = new UploadRetryPolicy(TimeSpan.FromMinutes(5), new ManualTimeProvider());
        var exception = new TooManyRequestsApiException("Wait.") { RetryAfter = TimeSpan.Zero };

        policy.GetRetryDelay(exception, 1).ShouldNotBeNull().ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public void RetryDelay_EndingAfterTheRetryTimeout_IsNull()
    {
        var time = new ManualTimeProvider();
        var policy = new UploadRetryPolicy(TimeSpan.FromMinutes(5), time);
        var exception = new ServiceUnavailableApiException("Full.") { RetryAfter = TimeSpan.FromMinutes(1) };

        time.Advance(TimeSpan.FromMinutes(4));
        policy.GetRetryDelay(exception, 1).ShouldBe(TimeSpan.FromMinutes(1));

        time.Advance(TimeSpan.FromSeconds(1));
        policy.GetRetryDelay(exception, 1).ShouldBeNull();
    }

    [TestMethod]
    public void Success_StartsTheRetryTimeoutAgain()
    {
        var time = new ManualTimeProvider();
        var policy = new UploadRetryPolicy(TimeSpan.FromMinutes(5), time);
        var exception = new ServiceUnavailableApiException("Full.") { RetryAfter = TimeSpan.FromMinutes(1) };

        time.Advance(TimeSpan.FromMinutes(10));
        policy.GetRetryDelay(exception, 1).ShouldBeNull();

        policy.OnSuccess();
        policy.GetRetryDelay(exception, 1).ShouldBe(TimeSpan.FromMinutes(1));
    }

    [TestMethod]
    public void ZeroRetryTimeout_RetriesNothing()
    {
        var policy = new UploadRetryPolicy(TimeSpan.Zero, new ManualTimeProvider());

        policy.GetRetryDelay(new HttpRequestException(), 1).ShouldBeNull();
    }

    [TestMethod]
    public void InfiniteRetryTimeout_RetriesWithoutLimit()
    {
        var time = new ManualTimeProvider();
        var policy = new UploadRetryPolicy(Timeout.InfiniteTimeSpan, time);

        time.Advance(TimeSpan.FromDays(400));

        policy.GetRetryDelay(new HttpRequestException(), 50).ShouldNotBeNull();
        policy.GetRetryDelay(new ServiceUnavailableApiException("Full.") { RetryAfter = TimeSpan.FromDays(100) }, 1)
            .ShouldBe(TimeSpan.FromMilliseconds(int.MaxValue));
    }

    [TestMethod]
    public async Task Delay_IsMeasuredWithTheTimeProvider()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var policy = new UploadRetryPolicy(TimeSpan.FromMinutes(5), time);

        var delay = policy.DelayAsync(TimeSpan.FromSeconds(2), CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(1.9));
        delay.IsCompleted.ShouldBeFalse();

        time.Advance(TimeSpan.FromSeconds(0.1));
        (await Task.WhenAny(delay, Task.Delay(TimeSpan.FromSeconds(10)))).ShouldBeSameAs(delay);
    }
}

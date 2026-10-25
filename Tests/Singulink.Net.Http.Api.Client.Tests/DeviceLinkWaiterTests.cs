using Singulink.Net.Http.Api.DataContracts;

namespace Singulink.Net.Http.Api.Client;

[PrefixTestClass]
public sealed class DeviceLinkWaiterTests
{
    private static readonly DeviceLinkStart Link = new("link-id", "K7PX-M3QR", "poll-secret", TimeSpan.FromMinutes(10));

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ApprovedLink_ReturnsSignInCode()
    {
        var checks = new List<CheckDeviceLinkCommand>();
        var waiter = CreateWaiter();

        string code = await waiter.WaitForApprovalAsync((command, _) => {
            checks.Add(command);
            return Task.FromResult(new DeviceLinkStatus(checks.Count is 3 ? "sign-in-code" : null));
        }, cancellationToken: TestContext.CancellationToken);

        code.ShouldBe("sign-in-code");
        checks.Count.ShouldBe(3);
        checks.ShouldAllBe(c => c.Id == "link-id" && c.PollSecret == "poll-secret");
    }

    [TestMethod]
    public async Task ExpiredLink_ThrowsTheServicesError()
    {
        var waiter = CreateWaiter();

        var ex = await Should.ThrowAsync<UnauthorizedApiException>(() => waiter.WaitForApprovalAsync(
            (_, _) => Task.FromException<DeviceLinkStatus>(Expired()), cancellationToken: TestContext.CancellationToken));

        ex.ErrorCode.ShouldBe(SignInErrorCodes.DeviceLinkExpired);
    }

    [TestMethod]
    public async Task TransientFailures_AreCheckedAgainAndReported()
    {
        var waiter = CreateWaiter();
        var reachability = new List<bool>();
        var outcomes = new Queue<Exception?>([new HttpRequestException(), new TaskCanceledException("Timed out.", new TimeoutException()), null, null]);
        int checks = 0;

        string code = await waiter.WaitForApprovalAsync(
            (_, _) => {
                checks++;

                if (outcomes.Dequeue() is { } exception)
                    return Task.FromException<DeviceLinkStatus>(exception);

                return Task.FromResult(new DeviceLinkStatus(outcomes.Count is 0 ? "sign-in-code" : null));
            },
            reachability.Add,
            TestContext.CancellationToken);

        code.ShouldBe("sign-in-code");
        checks.ShouldBe(4);
        reachability.ShouldBe([false, true]);
    }

    [TestMethod]
    public async Task TransientFailureAfterLifetime_IsThrown()
    {
        var time = new ManualTimeProvider();
        var waiter = CreateWaiter(time);
        int checks = 0;

        var ex = await Should.ThrowAsync<HttpRequestException>(() => waiter.WaitForApprovalAsync(
            (_, _) => {
                if (++checks is 3)
                    time.Advance(Link.Lifetime);

                return Task.FromException<DeviceLinkStatus>(new HttpRequestException("The service can't be reached."));
            },
            cancellationToken: TestContext.CancellationToken));

        ex.Message.ShouldBe("The service can't be reached.");
        checks.ShouldBe(3);
    }

    [TestMethod]
    public async Task OtherFailure_IsThrownRightAway()
    {
        var waiter = CreateWaiter();
        int checks = 0;

        await Should.ThrowAsync<ForbiddenApiException>(() => waiter.WaitForApprovalAsync(
            (_, _) => {
                checks++;
                return Task.FromException<DeviceLinkStatus>(new ForbiddenApiException("Blocked."));
            },
            cancellationToken: TestContext.CancellationToken));

        checks.ShouldBe(1);
    }

    [TestMethod]
    public async Task CancelledWait_ThrowsOperationCanceled()
    {
        var waiter = CreateWaiter();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        await Should.ThrowAsync<OperationCanceledException>(() => waiter.WaitForApprovalAsync((_, _) => {
            cancellation.Cancel();
            return Task.FromResult(new DeviceLinkStatus(null));
        }, cancellationToken: cancellation.Token));
    }

    [TestMethod]
    public async Task PollInterval_IsMeasuredWithTheTimeProvider()
    {
        var time = new ManualTimeProvider(manualTimers: true);
        var waiter = new DeviceLinkWaiter(Link, time) { PollInterval = TimeSpan.FromSeconds(2) };
        int checks = 0;

        var wait = waiter.WaitForApprovalAsync(
            (_, _) => Task.FromResult(new DeviceLinkStatus(Interlocked.Increment(ref checks) is 2 ? "sign-in-code" : null)),
            cancellationToken: TestContext.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(1.5));
        await Task.Delay(100, TestContext.CancellationToken);
        Volatile.Read(ref checks).ShouldBe(0);

        time.Advance(TimeSpan.FromSeconds(0.5));
        await WaitUntilAsync(() => Volatile.Read(ref checks) is 1 && time.PendingTimerCount is 1);

        time.Advance(TimeSpan.FromSeconds(2));
        (await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(10), TestContext.CancellationToken))).ShouldBeSameAs(wait);
        (await wait).ShouldBe("sign-in-code");
    }

    [TestMethod]
    public void RemainingLifetime_CountsDownFromCreation()
    {
        var time = new ManualTimeProvider();
        var waiter = CreateWaiter(time);

        waiter.RemainingLifetime.ShouldBe(Link.Lifetime);

        time.Advance(TimeSpan.FromMinutes(4));
        waiter.RemainingLifetime.ShouldBe(TimeSpan.FromMinutes(6));

        time.Advance(TimeSpan.FromMinutes(6));
        waiter.RemainingLifetime.ShouldBe(TimeSpan.Zero);

        time.Advance(TimeSpan.FromMinutes(1));
        waiter.RemainingLifetime.ShouldBe(TimeSpan.Zero);
    }

    [TestMethod]
    public void PollInterval_DefaultsToTwoSecondsAndMustBePositive()
    {
        var waiter = new DeviceLinkWaiter(Link);

        waiter.PollInterval.ShouldBe(TimeSpan.FromSeconds(2));
        Should.Throw<ArgumentOutOfRangeException>(() => waiter.PollInterval = TimeSpan.Zero);
    }

    private async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));

        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private static DeviceLinkWaiter CreateWaiter(TimeProvider? timeProvider = null) =>
        new(Link, timeProvider) { PollInterval = TimeSpan.FromMilliseconds(1) };

    private static UnauthorizedApiException Expired() =>
        new("The device link has expired. Start a new one.") { ErrorCode = SignInErrorCodes.DeviceLinkExpired };
}

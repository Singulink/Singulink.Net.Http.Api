using Microsoft.Extensions.Logging;
using Singulink.Net.Http.Api.DataContracts;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Device link tests, run against the memory store and against a shared store with a new service instance for every call.
/// </summary>
public abstract class DeviceLinkTests
{
    private const string Device = "Windows (Chrome 140)";
    private const string Location = "Toronto, Canada";
    private const string Approver = "approver-1";
    private const string OtherApprover = "approver-2";

    private static readonly IPAddress DeviceIp = IPAddress.Parse("203.0.113.10");
    private static readonly IPAddress DeviceIpv6 = IPAddress.Parse("2001:db8::10");
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    protected abstract StoreKind StoreKind { get; }

    protected SignInTestContext CreateContext(Action<DeviceLinkOptions>? configureLinks = null) => new(StoreKind, configureLinks: configureLinks);

    protected SignInTestContext CreateContext(out InterceptingShortLivedStore interceptor)
    {
        InterceptingShortLivedStore? created = null;
        var context = new SignInTestContext(StoreKind, wrapStore: store => created = new InterceptingShortLivedStore(store));
        interceptor = created!;

        return context;
    }

    [TestMethod]
    public async Task FullFlow_SignsInNewDeviceAsApprover()
    {
        var context = CreateContext();
        var pkce = Client.Pkce.Create();
        var startedUtc = context.Clock.GetUtcNow().UtcDateTime;

        // The new device starts the link and shows the code.

        var start = await context.Links.StartAsync(pkce.Challenge, Device, DeviceIp, Location);

        start.Id.Length.ShouldBe(43);
        start.PollSecret.Length.ShouldBe(43);
        start.PollSecret.ShouldNotBe(start.Id);
        ShortCode.Format(start.Code).ShouldBe(start.Code);
        start.Lifetime.ShouldBe(Lifetime);

        (await context.Links.PollAsync(start.Id, start.PollSecret)).ShouldNotBeNull().IsApproved.ShouldBeFalse();

        // The approver types the code (as displayed, in lower case) and sees the new device's details.

        string typedCode = ShortCode.Format(start.Code).ToLowerInvariant();
        var request = (await context.Links.FindAsync(typedCode, Approver)).ShouldNotBeNull();

        request.Device.ShouldBe(Device);
        request.IpAddress.ShouldBe(DeviceIp.ToString());
        request.Location.ShouldBe(Location);
        request.CreatedUtc.ShouldBe(startedUtc);
        request.CreatedUtc.Kind.ShouldBe(DateTimeKind.Utc);

        (await context.Links.PollAsync(start.Id, start.PollSecret)).ShouldNotBeNull().IsApproved.ShouldBeFalse();

        // The approver approves, and the next poll gets the sign-in code.

        (await context.Links.ApproveAsync(typedCode, Approver)).Outcome.ShouldBe(DeviceLinkApprovalOutcome.Approved);

        var status = (await context.Links.PollAsync(start.Id, start.PollSecret)).ShouldNotBeNull();
        status.IsApproved.ShouldBeTrue();
        status.ApproverUserId.ShouldBe(Approver);
        status.SignInCode.ShouldNotBeNull();

        (await context.Codes.RedeemAsync(status.SignInCode, pkce.Verifier)).ShouldNotBeNull().UserId.ShouldBe(Approver);

        // The code can no longer be looked up, and later polls get the same sign-in code, which is used up.

        (await context.Links.FindAsync(start.Code, Approver)).ShouldBeNull();
        (await context.Links.ApproveAsync(start.Code, OtherApprover)).Outcome.ShouldBe(DeviceLinkApprovalOutcome.NotFound);

        var laterStatus = (await context.Links.PollAsync(start.Id, start.PollSecret)).ShouldNotBeNull();
        laterStatus.SignInCode.ShouldBe(status.SignInCode);
        (await context.Codes.RedeemAsync(laterStatus.SignInCode!, pkce.Verifier)).ShouldBeNull();
    }

    [TestMethod]
    public async Task SignInCode_IsBoundToLinkChallenge()
    {
        var context = CreateContext();
        var pkce = Client.Pkce.Create();

        var start = await context.Links.StartAsync(pkce.Challenge, Device, DeviceIp, null);
        (await context.Links.ApproveAsync(start.Code, Approver)).Outcome.ShouldBe(DeviceLinkApprovalOutcome.Approved);

        var status = (await context.Links.PollAsync(start.Id, start.PollSecret)).ShouldNotBeNull();
        status.IsApproved.ShouldBeTrue();

        // Whoever sees the code without the new device's verifier cannot use it, and the attempt uses it up.
        (await context.Codes.RedeemAsync(status.SignInCode, Client.Pkce.Create().Verifier)).ShouldBeNull();
        (await context.Codes.RedeemAsync(status.SignInCode, pkce.Verifier)).ShouldBeNull();
    }

    [TestMethod]
    public async Task Start_WithoutIpOrLocation()
    {
        var context = CreateContext();
        var start = await context.Links.StartAsync(Client.Pkce.Create().Challenge, Device, null, null);

        var request = (await context.Links.FindAsync(start.Code, Approver)).ShouldNotBeNull();
        request.IpAddress.ShouldBeNull();
        request.Location.ShouldBeNull();
    }

    [TestMethod]
    public async Task Start_WithIpv6Address()
    {
        var context = CreateContext();
        var start = await context.Links.StartAsync(Client.Pkce.Create().Challenge, Device, DeviceIpv6, null);

        (await context.Links.FindAsync(start.Code, Approver)).ShouldNotBeNull().IpAddress.ShouldBe(DeviceIpv6.ToString());
    }

    [TestMethod]
    public async Task Start_WithMalformedChallenge_ThrowsBadRequest()
    {
        var context = CreateContext();

        await Should.ThrowAsync<BadRequestApiException>(() => context.Links.StartAsync("not-a-challenge", Device, DeviceIp, null));
        await Should.ThrowAsync<BadRequestApiException>(() => context.Links.StartAsync(string.Empty, Device, DeviceIp, null));
    }

    [TestMethod]
    public async Task Start_IssuesDistinctLinks()
    {
        var context = CreateContext();
        var starts = new List<DeviceLinkStart>();

        for (int i = 0; i < 50; i++)
            starts.Add(await context.Links.StartAsync(Client.Pkce.Create().Challenge, Device, DeviceIp, null));

        starts.Select(s => s.Id).Distinct().Count().ShouldBe(starts.Count);
        starts.Select(s => s.Code).Distinct().Count().ShouldBe(starts.Count);
        starts.Select(s => s.PollSecret).Distinct().Count().ShouldBe(starts.Count);
    }

    [TestMethod]
    public async Task FindAndApprove_WithApproverSessionToken_UseItsUserId()
    {
        var context = CreateContext();
        var start = await context.Links.StartAsync(Client.Pkce.Create().Challenge, Device, DeviceIp, null);
        var approver = new TestSessionToken(SessionId: 1, UserId: 42, Stamp: 1);

        (await context.Links.FindAsync(start.Code, approver)).ShouldNotBeNull();
        (await context.Links.ApproveAsync(start.Code, approver)).IsApproved.ShouldBeTrue();

        (await context.Links.PollAsync(start.Id, start.PollSecret)).ShouldNotBeNull().ApproverUserId.ShouldBe("42");
    }

    [TestMethod]
    public async Task Approve_ReturnsTheApprovedDevice()
    {
        var context = CreateContext();
        var startedUtc = context.Clock.GetUtcNow().UtcDateTime;
        var start = await context.Links.StartAsync(Client.Pkce.Create().Challenge, Device, DeviceIp, Location);

        var approval = await context.Links.ApproveAsync(start.Code, Approver);

        approval.IsApproved.ShouldBeTrue();
        approval.Link.Device.ShouldBe(Device);
        approval.Link.IpAddress.ShouldBe(DeviceIp.ToString());
        approval.Link.Location.ShouldBe(Location);
        approval.Link.CreatedUtc.ShouldBe(startedUtc);

        var again = await context.Links.ApproveAsync(start.Code, Approver);
        again.IsApproved.ShouldBeFalse();
        again.Link.ShouldBeNull();

        var notFound = await context.Links.ApproveAsync(ShortCode.Generate(), Approver);
        notFound.Outcome.ShouldBe(DeviceLinkApprovalOutcome.NotFound);
        notFound.Link.ShouldBeNull();
    }

    [TestMethod]
    public async Task Approve_Twice_ReturnsAlreadyApproved_AndKeepsFirstApprover()
    {
        var context = CreateContext();
        var start = await context.Links.StartAsync(Client.Pkce.Create().Challenge, Device, DeviceIp, null);

        (await context.Links.ApproveAsync(start.Code, Approver)).Outcome.ShouldBe(DeviceLinkApprovalOutcome.Approved);
        (await context.Links.ApproveAsync(start.Code, Approver)).Outcome.ShouldBe(DeviceLinkApprovalOutcome.AlreadyApproved);
        (await context.Links.ApproveAsync(start.Code, OtherApprover)).Outcome.ShouldBe(DeviceLinkApprovalOutcome.AlreadyApproved);

        // An approved link that has not been polled yet can still be looked up.
        (await context.Links.FindAsync(start.Code, OtherApprover)).ShouldNotBeNull();

        (await context.Links.PollAsync(start.Id, start.PollSecret)).ShouldNotBeNull().ApproverUserId.ShouldBe(Approver);
    }

    [TestMethod]
    public async Task Approve_UnderConcurrency_SucceedsOnce()
    {
        var context = CreateContext();
        const int rounds = 100;
        var starts = new DeviceLinkStart[rounds];

        for (int i = 0; i < rounds; i++)
            starts[i] = await context.Links.StartAsync(Client.Pkce.Create().Challenge, Device, DeviceIp, null);

        var approvers = new int[rounds];

        var results = Concurrently.RunTasks(threads: 8, rounds, round => {
            string approver = $"approver-{round}-{Interlocked.Increment(ref approvers[round])}";
            return context.Links.ApproveAsync(starts[round].Code, approver);
        });

        foreach (var round in results)
        {
            round.Count(r => r.Outcome is DeviceLinkApprovalOutcome.Approved).ShouldBe(1);
            round.Count(r => r.Outcome is DeviceLinkApprovalOutcome.AlreadyApproved).ShouldBe(round.Length - 1);
        }
    }

    [TestMethod]
    public async Task GetStatus_UnderConcurrencyAfterApproval_GivesEveryPollTheSameSignInCode()
    {
        var context = CreateContext();
        const int rounds = 100;
        var starts = new DeviceLinkStart[rounds];

        for (int i = 0; i < rounds; i++)
        {
            starts[i] = await context.Links.StartAsync(Client.Pkce.Create().Challenge, Device, DeviceIp, null);
            (await context.Links.ApproveAsync(starts[i].Code, Approver)).Outcome.ShouldBe(DeviceLinkApprovalOutcome.Approved);
        }

        var results = Concurrently.RunTasks(threads: 8, rounds, round => context.Links.PollAsync(starts[round].Id, starts[round].PollSecret));

        foreach (var round in results)
        {
            round.ShouldAllBe(s => s != null && s.IsApproved);
            round.Select(s => s!.SignInCode).Distinct().ShouldHaveSingleItem();
        }
    }

    [TestMethod]
    public async Task ApproveAndPoll_UnderConcurrency_ApprovalIsNeverLost()
    {
        var context = CreateContext();
        const int rounds = 50;
        var starts = new DeviceLinkStart[rounds];

        for (int i = 0; i < rounds; i++)
            starts[i] = await context.Links.StartAsync(Client.Pkce.Create().Challenge, Device, DeviceIp, null);

        var callers = new int[rounds];

        // Three approvers and three polls of the new device race in each round. Exactly one approval wins, and the one sign-in code issued for the link
        // signs in that approver.
        var results = Concurrently.RunTasks(threads: 6, rounds, async round => {
            var start = starts[round];
            int caller = Interlocked.Increment(ref callers[round]);

            if (caller % 2 is 0)
            {
                string approver = $"approver-{round}-{caller}";
                bool approved = (await context.Links.ApproveAsync(start.Code, approver)).IsApproved;

                return (Approver: approved ? approver : null, Status: (DeviceLinkPollResult?)null);
            }

            return (Approver: (string?)null, Status: await context.Links.PollAsync(start.Id, start.PollSecret));
        });

        for (int round = 0; round < rounds; round++)
        {
            string winner = results[round].Select(r => r.Approver).Where(a => a is not null).ShouldHaveSingleItem().ShouldNotBeNull();
            var approvedStatuses = results[round].Select(r => r.Status).Where(s => s is { IsApproved: true }).ToList();
            approvedStatuses.ShouldAllBe(s => s!.ApproverUserId == winner);

            var status = (await context.Links.PollAsync(starts[round].Id, starts[round].PollSecret)).ShouldNotBeNull();
            status.ApproverUserId.ShouldBe(winner);
            approvedStatuses.ShouldAllBe(s => s!.SignInCode == status.SignInCode);
        }
    }

    [TestMethod]
    public async Task GetStatus_ChecksPollSecret()
    {
        var context = CreateContext();
        var start = await context.Links.StartAsync(Client.Pkce.Create().Challenge, Device, DeviceIp, null);
        var other = await context.Links.StartAsync(Client.Pkce.Create().Challenge, Device, DeviceIp, null);

        (await context.Links.PollAsync(start.Id, "wrong")).ShouldBeNull();
        (await context.Links.PollAsync(start.Id, string.Empty)).ShouldBeNull();
        (await context.Links.PollAsync(start.Id, start.PollSecret[..^1] + (start.PollSecret[^1] is 'A' ? 'B' : 'A'))).ShouldBeNull();
        (await context.Links.PollAsync(start.Id, other.PollSecret)).ShouldBeNull();
        (await context.Links.PollAsync(start.Id, start.Id)).ShouldBeNull();
        (await context.Links.PollAsync(start.PollSecret, start.PollSecret)).ShouldBeNull();
        (await context.Links.PollAsync("unknown", start.PollSecret)).ShouldBeNull();

        (await context.Links.ApproveAsync(start.Code, Approver)).Outcome.ShouldBe(DeviceLinkApprovalOutcome.Approved);

        // A wrong secret after approval gets nothing and does not use the link up.
        (await context.Links.PollAsync(start.Id, other.PollSecret)).ShouldBeNull();
        (await context.Links.PollAsync(start.Id, start.PollSecret)).ShouldNotBeNull().IsApproved.ShouldBeTrue();
    }

    [TestMethod]
    public async Task Check_ReturnsStatusContract_WithSameCodeAsPoll()
    {
        var context = CreateContext();
        var pkce = Client.Pkce.Create();
        var start = await context.Links.StartAsync(pkce.Challenge, Device, DeviceIp, null);

        (await context.Links.CheckAsync(start.Id, start.PollSecret)).SignInCode.ShouldBeNull();

        (await context.Links.ApproveAsync(start.Code, Approver)).Outcome.ShouldBe(DeviceLinkApprovalOutcome.Approved);

        var status = await context.Links.CheckAsync(start.Id, start.PollSecret);
        status.SignInCode.ShouldNotBeNull();
        (await context.Links.PollAsync(start.Id, start.PollSecret)).ShouldNotBeNull().SignInCode.ShouldBe(status.SignInCode);
        (await context.Codes.RedeemAsync(status.SignInCode, pkce.Verifier)).ShouldNotBeNull().UserId.ShouldBe(Approver);
    }

    [TestMethod]
    public async Task Check_UnknownExpiredOrWrongSecret_ThrowsDeviceLinkExpired()
    {
        var context = CreateContext();
        var start = await context.Links.StartAsync(Client.Pkce.Create().Challenge, Device, DeviceIp, null);

        await ShouldThrowExpiredAsync(() => context.Links.CheckAsync(start.Id, "wrong"));
        await ShouldThrowExpiredAsync(() => context.Links.CheckAsync("unknown", start.PollSecret));

        context.Clock.Advance(Lifetime);
        await ShouldThrowExpiredAsync(() => context.Links.CheckAsync(start.Id, start.PollSecret));

        static async Task ShouldThrowExpiredAsync(Func<Task> check)
        {
            var ex = await Should.ThrowAsync<UnauthorizedApiException>(check);
            ex.ErrorCode.ShouldBe(SignInErrorCodes.DeviceLinkExpired);
        }
    }

    [TestMethod]
    public async Task Link_ExpiresAfterLifetime()
    {
        var context = CreateContext();
        var pending = await context.Links.StartAsync(Client.Pkce.Create().Challenge, Device, DeviceIp, null);
        var approved = await context.Links.StartAsync(Client.Pkce.Create().Challenge, Device, DeviceIp, null);

        context.Clock.Advance(Lifetime - TimeSpan.FromSeconds(1));
        (await context.Links.PollAsync(pending.Id, pending.PollSecret)).ShouldNotBeNull();
        (await context.Links.ApproveAsync(approved.Code, Approver)).Outcome.ShouldBe(DeviceLinkApprovalOutcome.Approved);

        context.Clock.Advance(TimeSpan.FromSeconds(1));
        (await context.Links.PollAsync(pending.Id, pending.PollSecret)).ShouldBeNull();
        (await context.Links.FindAsync(pending.Code, Approver)).ShouldBeNull();
        (await context.Links.ApproveAsync(pending.Code, OtherApprover)).Outcome.ShouldBe(DeviceLinkApprovalOutcome.NotFound);

        // Approved but not polled for longer than the grace period after the code expired.
        context.Clock.Advance(TimeSpan.FromMinutes(1));
        (await context.Links.PollAsync(approved.Id, approved.PollSecret)).ShouldBeNull();
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("v2")]
    public async Task Approve_AppData_IsCarriedBySignInCode(string? appData)
    {
        var context = CreateContext();
        var pkce = Client.Pkce.Create();
        var start = await context.Links.StartAsync(pkce.Challenge, Device, DeviceIp, null);

        (await context.Links.ApproveAsync(start.Code, Approver, appData)).Outcome.ShouldBe(DeviceLinkApprovalOutcome.Approved);
        var status = (await context.Links.PollAsync(start.Id, start.PollSecret)).ShouldNotBeNull();

        var redeemed = (await context.Codes.RedeemAsync(status.SignInCode!, pkce.Verifier)).ShouldNotBeNull();
        redeemed.UserId.ShouldBe(Approver);
        redeemed.AppData.ShouldBe(appData);
    }

    [TestMethod]
    public async Task SignInCode_IsValidForAsLongAsTheLinkCanBePolled()
    {
        var context = new SignInTestContext(StoreKind, configureCodes: o => o.Lifetime = TimeSpan.FromMinutes(1));
        var pkce = Client.Pkce.Create();
        var start = await context.Links.StartAsync(pkce.Challenge, Device, DeviceIp, null);

        (await context.Links.ApproveAsync(start.Code, Approver)).Outcome.ShouldBe(DeviceLinkApprovalOutcome.Approved);
        (await context.Links.PollAsync(start.Id, start.PollSecret)).ShouldNotBeNull().IsApproved.ShouldBeTrue();

        // The response with the code did not reach the new device, which polls again shortly before the link can no longer be polled.
        context.Clock.Advance(Lifetime + TimeSpan.FromSeconds(50));
        var status = (await context.Links.PollAsync(start.Id, start.PollSecret)).ShouldNotBeNull();

        (await context.Codes.RedeemAsync(status.SignInCode!, pkce.Verifier)).ShouldNotBeNull().UserId.ShouldBe(Approver);
    }

    [TestMethod]
    public async Task SignInCode_IssuedAtTheEnd_IsValidForTheSignInCodeLifetime()
    {
        var context = CreateContext();
        var pkce = Client.Pkce.Create();
        var start = await context.Links.StartAsync(pkce.Challenge, Device, DeviceIp, null);

        context.Clock.Advance(Lifetime - TimeSpan.FromSeconds(1));
        (await context.Links.ApproveAsync(start.Code, Approver)).Outcome.ShouldBe(DeviceLinkApprovalOutcome.Approved);
        var status = (await context.Links.PollAsync(start.Id, start.PollSecret)).ShouldNotBeNull();

        // The new device asks the user whether to stay signed in before it redeems the code.
        context.Clock.Advance(new SignInCodeOptions().Lifetime - TimeSpan.FromSeconds(1));
        (await context.Codes.RedeemAsync(status.SignInCode!, pkce.Verifier)).ShouldNotBeNull().UserId.ShouldBe(Approver);
    }

    [TestMethod]
    public async Task ApprovalInTheLastSeconds_CanStillBeCollected()
    {
        var context = CreateContext();
        var pkce = Client.Pkce.Create();
        var start = await context.Links.StartAsync(pkce.Challenge, Device, DeviceIp, null);

        context.Clock.Advance(Lifetime - TimeSpan.FromSeconds(1));
        (await context.Links.ApproveAsync(start.Code, Approver)).Outcome.ShouldBe(DeviceLinkApprovalOutcome.Approved);

        // The code has expired by the time the new device polls, but the approval still reaches it.
        context.Clock.Advance(TimeSpan.FromSeconds(30));
        var status = (await context.Links.PollAsync(start.Id, start.PollSecret)).ShouldNotBeNull();

        status.ApproverUserId.ShouldBe(Approver);
        (await context.Codes.RedeemAsync(status.SignInCode!, pkce.Verifier)).ShouldNotBeNull().UserId.ShouldBe(Approver);
    }

    [TestMethod]
    public async Task Approval_OutlivesTheCode_WhenTheApprovingServersClockIsAhead()
    {
        var context = CreateContext();
        var start = await context.Links.StartAsync(Client.Pkce.Create().Challenge, Device, DeviceIp, null);

        // The approving server's clock is 9 minutes ahead, so by its clock the link has only a minute left.
        var serverAhead = context.CreateLinks(new TestClock(context.Clock.GetUtcNow() + TimeSpan.FromMinutes(9)));
        (await serverAhead.ApproveAsync(start.Code, Approver)).Outcome.ShouldBe(DeviceLinkApprovalOutcome.Approved);

        // Later, while the code can still be looked up, no one else can approve the link.
        context.Clock.Advance(TimeSpan.FromMinutes(5));
        (await context.Links.ApproveAsync(start.Code, OtherApprover)).Outcome.ShouldBe(DeviceLinkApprovalOutcome.AlreadyApproved);

        (await context.Links.PollAsync(start.Id, start.PollSecret)).ShouldNotBeNull().ApproverUserId.ShouldBe(Approver);
    }

    [TestMethod]
    public async Task LostStatusResponse_CanBePolledAgain()
    {
        var context = CreateContext();
        var pkce = Client.Pkce.Create();
        var start = await context.Links.StartAsync(pkce.Challenge, Device, DeviceIp, null);
        (await context.Links.ApproveAsync(start.Code, Approver)).Outcome.ShouldBe(DeviceLinkApprovalOutcome.Approved);

        // The response to the first poll never reaches the new device.
        _ = await context.Links.PollAsync(start.Id, start.PollSecret);

        var status = (await context.Links.PollAsync(start.Id, start.PollSecret)).ShouldNotBeNull();
        (await context.Codes.RedeemAsync(status.SignInCode!, pkce.Verifier)).ShouldNotBeNull().UserId.ShouldBe(Approver);
    }

    [TestMethod]
    public async Task StoreFailureAfterIssuingTheCode_DoesNotLoseIt()
    {
        var context = CreateContext(out var hooks);
        var pkce = Client.Pkce.Create();
        var start = await context.Links.StartAsync(pkce.Challenge, Device, DeviceIp, null);
        (await context.Links.ApproveAsync(start.Code, Approver)).Outcome.ShouldBe(DeviceLinkApprovalOutcome.Approved);

        hooks.FailOnce("Remove", ToolkitKeys.DeviceLinkCode);
        await Should.ThrowAsync<InvalidOperationException>(() => context.Links.PollAsync(start.Id, start.PollSecret));

        var status = (await context.Links.PollAsync(start.Id, start.PollSecret)).ShouldNotBeNull();
        (await context.Codes.RedeemAsync(status.SignInCode!, pkce.Verifier)).ShouldNotBeNull().UserId.ShouldBe(Approver);
    }

    [TestMethod]
    public async Task CancellationWhileIssuingTheCode_DoesNotLoseIt()
    {
        var context = CreateContext(out var hooks);
        var pkce = Client.Pkce.Create();
        var start = await context.Links.StartAsync(pkce.Challenge, Device, DeviceIp, null);
        (await context.Links.ApproveAsync(start.Code, Approver)).Outcome.ShouldBe(DeviceLinkApprovalOutcome.Approved);

        // The request is cancelled as the sign-in code is created; the code is issued anyway.
        using var cts = new CancellationTokenSource();
        hooks.Before = (operation, key) => {
            if (operation is "TryAdd" && key.StartsWith(ToolkitKeys.SignInCode, StringComparison.Ordinal))
                cts.Cancel();
        };

        var status = (await context.Links.PollAsync(start.Id, start.PollSecret, cts.Token)).ShouldNotBeNull();
        hooks.Before = null;

        (await context.Links.PollAsync(start.Id, start.PollSecret)).ShouldNotBeNull().SignInCode.ShouldBe(status.SignInCode);
        (await context.Codes.RedeemAsync(status.SignInCode!, pkce.Verifier)).ShouldNotBeNull().UserId.ShouldBe(Approver);
    }

    [TestMethod]
    public async Task LinkThatCannotBeAdded_RemovesItsCode()
    {
        var context = CreateContext(out var hooks);
        string? code = null;

        hooks.After = (operation, key) => {
            if (operation is "TryAdd" && key.StartsWith(ToolkitKeys.DeviceLinkCode, StringComparison.Ordinal))
                code = key[ToolkitKeys.DeviceLinkCode.Length..];
        };

        hooks.FailOnce("TryAdd", ToolkitKeys.DeviceLink);
        await Should.ThrowAsync<InvalidOperationException>(() => context.Links.StartAsync(Client.Pkce.Create().Challenge, Device, DeviceIp, null));

        // No one can approve a device that could never collect its sign-in code.
        (await context.Links.ApproveAsync(code.ShouldNotBeNull(), Approver)).Outcome.ShouldBe(DeviceLinkApprovalOutcome.NotFound);
    }

    [TestMethod]
    public async Task CancellationAfterTheCodeIsAdded_StillAddsTheLink()
    {
        var context = CreateContext(out var hooks);
        var pkce = Client.Pkce.Create();
        using var cts = new CancellationTokenSource();

        hooks.After = (operation, key) => {
            if (operation is "TryAdd" && key.StartsWith(ToolkitKeys.DeviceLinkCode, StringComparison.Ordinal))
                cts.Cancel();
        };

        var start = await context.Links.StartAsync(pkce.Challenge, Device, DeviceIp, null, cts.Token);
        hooks.After = null;

        (await context.Links.ApproveAsync(start.Code, Approver)).Outcome.ShouldBe(DeviceLinkApprovalOutcome.Approved);
        var status = (await context.Links.PollAsync(start.Id, start.PollSecret)).ShouldNotBeNull();
        (await context.Codes.RedeemAsync(status.SignInCode!, pkce.Verifier)).ShouldNotBeNull().UserId.ShouldBe(Approver);
    }

    [TestMethod]
    public async Task CancellationAfterALookupIsCounted_GivesARightCodeBack()
    {
        var context = CreateContext(out var hooks);
        var start = await context.Links.StartAsync(Client.Pkce.Create().Challenge, Device, DeviceIp, null);
        string wrongCode = GetOtherCode(start.Code);

        for (int i = 0; i < 4; i++)
            (await context.Links.FindAsync(wrongCode, Approver)).ShouldBeNull();

        // The request is cancelled right after the lookup is counted.
        using var cts = new CancellationTokenSource();
        hooks.After = (operation, key) => {
            if (operation is "Increment")
                cts.Cancel();
        };

        (await context.Links.FindAsync(start.Code, Approver, cts.Token)).ShouldNotBeNull();
        hooks.After = null;

        // The right code was not counted, so a fifth wrong code is still allowed.
        (await context.Links.FindAsync(wrongCode, Approver)).ShouldBeNull();
        await Should.ThrowAsync<TooManyRequestsApiException>(() => context.Links.FindAsync(wrongCode, Approver));
    }

    [TestMethod]
    public async Task FailedLookup_IsNotCountedAsAWrongCode()
    {
        var context = CreateContext(out var hooks);
        string wrongCode = ShortCode.Generate();

        for (int i = 0; i < 4; i++)
            (await context.Links.FindAsync(wrongCode, Approver)).ShouldBeNull();

        hooks.FailOnce("Get", ToolkitKeys.DeviceLinkCode);
        await Should.ThrowAsync<InvalidOperationException>(() => context.Links.FindAsync(wrongCode, Approver));

        (await context.Links.FindAsync(wrongCode, Approver)).ShouldBeNull();
        await Should.ThrowAsync<TooManyRequestsApiException>(() => context.Links.FindAsync(wrongCode, Approver));
    }

    [TestMethod]
    public async Task UnreadableLink_IsTreatedAsMissingAndLogged()
    {
        var context = CreateContext();
        var start = await context.Links.StartAsync(Client.Pkce.Create().Challenge, Device, DeviceIp, null);

        // As if a link were stored by a version that kept something else under its key.
        string linkKey = ToolkitKeys.DeviceLink + start.Id;
        await context.Store.TakeAsync(linkKey);
        await context.Store.TryAddAsync(linkKey, """{"code":"ABC"}""", Lifetime);

        (await context.Links.PollAsync(start.Id, start.PollSecret)).ShouldBeNull();
        context.Logs.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("could not be read", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Link_UsesConfiguredLifetime()
    {
        var lifetime = TimeSpan.FromMinutes(3);
        var context = CreateContext(o => o.Lifetime = lifetime);

        var start = await context.Links.StartAsync(Client.Pkce.Create().Challenge, Device, DeviceIp, null);
        start.Lifetime.ShouldBe(lifetime);

        context.Clock.Advance(lifetime);
        (await context.Links.PollAsync(start.Id, start.PollSecret)).ShouldBeNull();
    }

    [TestMethod]
    public async Task WrongCodes_AfterFive_RefuseLookupsForThatApprover()
    {
        var context = CreateContext();
        var start = await context.Links.StartAsync(Client.Pkce.Create().Challenge, Device, DeviceIp, null);
        string wrongCode = GetOtherCode(start.Code);

        for (int i = 0; i < 4; i++)
            (await context.Links.FindAsync(wrongCode, Approver)).ShouldBeNull();

        (await context.Links.ApproveAsync(wrongCode, Approver)).Outcome.ShouldBe(DeviceLinkApprovalOutcome.NotFound);

        // Even the right code is refused now, by both lookup and approval.
        var ex = await Should.ThrowAsync<TooManyRequestsApiException>(() => context.Links.FindAsync(start.Code, Approver));
        ex.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        ex.ErrorCode.ShouldBe(SignInErrorCodes.DeviceLinkTooManyWrongCodes);
        ex.RetryAfter.ShouldBe(Lifetime);

        ex = await Should.ThrowAsync<TooManyRequestsApiException>(() => context.Links.ApproveAsync(start.Code, Approver));
        ex.ErrorCode.ShouldBe(SignInErrorCodes.DeviceLinkTooManyWrongCodes);
        ex.RetryAfter.ShouldBe(Lifetime);

        // Other approvers are not affected.
        (await context.Links.FindAsync(start.Code, OtherApprover)).ShouldNotBeNull();
        (await context.Links.ApproveAsync(start.Code, OtherApprover)).Outcome.ShouldBe(DeviceLinkApprovalOutcome.Approved);
    }

    [TestMethod]
    public async Task WrongCodes_RetryAfterIsTheConfiguredLifetime()
    {
        var lifetime = TimeSpan.FromMinutes(3);
        var context = CreateContext(o => o.Lifetime = lifetime);
        string wrongCode = ShortCode.Generate();

        for (int i = 0; i < 5; i++)
            (await context.Links.FindAsync(wrongCode, Approver)).ShouldBeNull();

        var ex = await Should.ThrowAsync<TooManyRequestsApiException>(() => context.Links.FindAsync(wrongCode, Approver));
        ex.RetryAfter.ShouldBe(lifetime);

        // Lookups are possible again once the retry time has passed.
        context.Clock.Advance(lifetime);
        (await context.Links.FindAsync(wrongCode, Approver)).ShouldBeNull();
    }

    [TestMethod]
    public async Task RightCodes_DoNotCountAsWrongCodes()
    {
        var context = CreateContext();
        string wrongCode = ShortCode.Generate();

        for (int i = 0; i < 4; i++)
            (await context.Links.FindAsync(wrongCode, Approver)).ShouldBeNull();

        for (int i = 0; i < 10; i++)
        {
            var start = await context.Links.StartAsync(Client.Pkce.Create().Challenge, Device, DeviceIp, null);
            (await context.Links.FindAsync(start.Code, Approver)).ShouldNotBeNull();
            (await context.Links.ApproveAsync(start.Code, Approver)).Outcome.ShouldBe(DeviceLinkApprovalOutcome.Approved);
        }

        // The fifth wrong code still counts as the fifth.
        (await context.Links.FindAsync(wrongCode, Approver)).ShouldBeNull();
        await Should.ThrowAsync<TooManyRequestsApiException>(() => context.Links.FindAsync(wrongCode, Approver));
    }

    [TestMethod]
    public async Task WrongCodes_AreForgottenAfterLifetimeWithoutWrongCodes()
    {
        var context = CreateContext();
        string wrongCode = ShortCode.Generate();

        for (int i = 0; i < 5; i++)
        {
            (await context.Links.FindAsync(wrongCode, Approver)).ShouldBeNull();
            context.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        // The count lives on for the lifetime after the last wrong code, not after the first, and refused lookups do not extend it.
        context.Clock.Advance(Lifetime - TimeSpan.FromMinutes(2));
        await Should.ThrowAsync<TooManyRequestsApiException>(() => context.Links.FindAsync(wrongCode, Approver));

        context.Clock.Advance(TimeSpan.FromMinutes(1));

        var start = await context.Links.StartAsync(Client.Pkce.Create().Challenge, Device, DeviceIp, null);
        (await context.Links.FindAsync(start.Code, Approver)).ShouldNotBeNull();
    }

    [TestMethod]
    public void WrongCodes_UnderConcurrency_AreAllCounted()
    {
        var context = CreateContext();
        string wrongCode = ShortCode.Generate();

        var results = Concurrently.RunTasks(threads: 10, rounds: 1, async _ => {
            try
            {
                (await context.Links.FindAsync(wrongCode, Approver)).ShouldBeNull();
                return true;
            }
            catch (TooManyRequestsApiException)
            {
                return false;
            }
        });

        results[0].Count(looked => looked).ShouldBe(5);
    }

    [TestMethod]
    public async Task RightCodes_UnderConcurrency_AreFound()
    {
        var context = CreateContext();
        var start = await context.Links.StartAsync(Client.Pkce.Create().Challenge, Device, DeviceIp, null);

        // Each lookup counts as a wrong code until the code is found, so up to 5 lookups of one approver can run at the same moment.
        var results = Concurrently.RunTasks(threads: 5, rounds: 20, _ => context.Links.FindAsync(start.Code, Approver));

        results.SelectMany(r => r).ShouldAllBe(r => r != null);
        (await context.Links.ApproveAsync(start.Code, Approver)).Outcome.ShouldBe(DeviceLinkApprovalOutcome.Approved);
    }

    [TestMethod]
    public async Task InputThatCannotBeACode_DoesNotCountAsWrongCode()
    {
        var context = CreateContext();
        var start = await context.Links.StartAsync(Client.Pkce.Create().Challenge, Device, DeviceIp, null);

        for (int i = 0; i < 10; i++)
        {
            (await context.Links.FindAsync("UUUU-UUUU", Approver)).ShouldBeNull();
            (await context.Links.ApproveAsync("123", Approver)).Outcome.ShouldBe(DeviceLinkApprovalOutcome.NotFound);
        }

        (await context.Links.FindAsync(start.Code, Approver)).ShouldNotBeNull();
    }

    private static string GetOtherCode(string code)
    {
        string other;

        do
        {
            other = ShortCode.Generate();
        }
        while (other == ShortCode.Normalize(code));

        return other;
    }
}

[PrefixTestClass]
public sealed class DeviceLinkTestsWithMemoryStore : DeviceLinkTests
{
    protected override StoreKind StoreKind => StoreKind.Memory;
}

[PrefixTestClass]
public sealed class DeviceLinkTestsWithSharedStore : DeviceLinkTests
{
    protected override StoreKind StoreKind => StoreKind.Shared;

    [TestMethod]
    public async Task Links_AreKeptUnderToolkitKeys_WithoutUserIds()
    {
        var context = CreateContext();
        var store = (SharedShortLivedStore)context.Store;

        var start = await context.Links.StartAsync(Client.Pkce.Create().Challenge, "Device", null, null);
        (await context.Links.FindAsync(ShortCode.Generate(), "user@example.com")).ShouldBeNull();
        (await context.Links.ApproveAsync(start.Code, "user@example.com")).Outcome.ShouldBe(DeviceLinkApprovalOutcome.Approved);

        store.Keys.ShouldAllBe(k => k.StartsWith("httpapi:device-link", StringComparison.Ordinal));
        store.Keys.ShouldContain("httpapi:device-link:" + start.Id);
        store.Keys.ShouldContain("httpapi:device-link-code:" + ShortCode.Normalize(start.Code));
        store.Keys.ShouldNotContain(k => k.Contains("user@example.com", StringComparison.Ordinal));
    }
}

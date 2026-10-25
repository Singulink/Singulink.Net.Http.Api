using Singulink.Net.Http.Api.DataContracts;

namespace Singulink.Net.Http.Api.Client.Passkeys;

[PrefixTestClass]
public sealed class PasskeyCeremonyPrefetchTests
{
    private static readonly PasskeyCredential Credential = new("{}", PasskeyAttachment.Platform);

    [TestMethod]
    public async Task CancelledPrompt_KeepsCeremonyForAnotherTry()
    {
        var service = new FakeService();
        var prefetch = service.CreatePrefetch();
        var prompted = new List<string>();

        prefetch.Prefetch();

        for (int i = 0; i < 2; i++)
        {
            await Should.ThrowAsync<PasskeyAuthenticatorException>(() => prefetch.RunAsync(
                (options, _) => {
                    prompted.Add(options);
                    throw new PasskeyAuthenticatorException(PasskeyErrorReason.Cancelled);
                },
                (_, _, _) => Task.FromResult("finished")));
        }

        service.Begun.ShouldHaveSingleItem();
        prompted.ShouldBe([service.Begun[0].Ceremony.OptionsJson, service.Begun[0].Ceremony.OptionsJson]);
    }

    [TestMethod]
    public async Task PrefetchedCeremony_IsUsedByTheNextTry()
    {
        var service = new FakeService();
        var prefetch = service.CreatePrefetch();
        var finished = new List<SignIn>();

        prefetch.Prefetch();
        prefetch.Prefetch();
        service.Begun.ShouldHaveSingleItem();

        string result = await prefetch.RunAsync(Prompt, (signIn, _, _) => {
            finished.Add(signIn);
            return Task.FromResult("finished");
        });

        result.ShouldBe("finished");
        finished.ShouldBe([service.Begun[0]]);
        service.Begun.ShouldHaveSingleItem();
    }

    [TestMethod]
    public async Task FailedFinish_BeginsNewCeremonyForAnotherTry()
    {
        var service = new FakeService();
        var prefetch = service.CreatePrefetch();
        var finished = new List<SignIn>();

        await Should.ThrowAsync<ValidationApiException>(() => prefetch.RunAsync(Prompt, (signIn, _, _) => {
            finished.Add(signIn);
            return Task.FromException<string>(new ValidationApiException("The passkey could not be verified."));
        }));

        // The next ceremony was begun right away, before the next try asks for it.
        service.Begun.Count.ShouldBe(2);

        string result = await prefetch.RunAsync(Prompt, (signIn, _, _) => {
            finished.Add(signIn);
            return Task.FromResult("finished");
        });

        result.ShouldBe("finished");
        prefetch.ResentCredential.ShouldBeFalse();
        service.Begun.Count.ShouldBe(2);
        finished.ShouldBe([service.Begun[0], service.Begun[1]]);
    }

    [TestMethod]
    public async Task FinishThatFailedForTheMoment_SendsTheSameCredentialAgain()
    {
        var service = new FakeService();
        var prefetch = service.CreatePrefetch();
        int prompts = 0;
        var finished = new List<(SignIn SignIn, PasskeyCredential Credential)>();
        var outcomes = new Queue<Exception?>([new HttpRequestException(), null]);

        Task<PasskeyCredential> CountedPrompt(string options, CancellationToken cancellationToken)
        {
            prompts++;
            return Task.FromResult(new PasskeyCredential("{}", PasskeyAttachment.Platform));
        }

        Task<string> Finish(SignIn signIn, PasskeyCredential credential, CancellationToken cancellationToken)
        {
            finished.Add((signIn, credential));
            return outcomes.Dequeue() is { } exception ? Task.FromException<string>(exception) : Task.FromResult("finished");
        }

        await Should.ThrowAsync<HttpRequestException>(() => prefetch.RunAsync(CountedPrompt, Finish));
        prefetch.ResentCredential.ShouldBeFalse();

        string result = await prefetch.RunAsync(CountedPrompt, Finish);

        result.ShouldBe("finished");
        prefetch.ResentCredential.ShouldBeTrue();
        prompts.ShouldBe(1);
        service.Begun.ShouldHaveSingleItem();
        finished.Count.ShouldBe(2);
        finished[1].ShouldBe(finished[0]);
    }

    [TestMethod]
    public async Task CancelledFinish_SendsTheSameCredentialAgain()
    {
        var service = new FakeService();
        var prefetch = service.CreatePrefetch();
        int prompts = 0;
        using var cancellation = new CancellationTokenSource();

        Task<PasskeyCredential> CountedPrompt(string options, CancellationToken cancellationToken)
        {
            prompts++;
            return Task.FromResult(Credential);
        }

        await Should.ThrowAsync<OperationCanceledException>(() => prefetch.RunAsync(CountedPrompt, (_, _, _) => {
            cancellation.Cancel();
            return Task.FromCanceled<string>(cancellation.Token);
        }, cancellation.Token));

        (await prefetch.RunAsync(CountedPrompt, (_, _, _) => Task.FromResult("finished"))).ShouldBe("finished");

        prompts.ShouldBe(1);
        prefetch.ResentCredential.ShouldBeTrue();
    }

    [TestMethod]
    public async Task DiscardedCredential_IsNotSentAgain()
    {
        var service = new FakeService();
        var prefetch = service.CreatePrefetch();
        int prompts = 0;

        Task<PasskeyCredential> CountedPrompt(string options, CancellationToken cancellationToken)
        {
            prompts++;
            return Task.FromResult(Credential);
        }

        await Should.ThrowAsync<HttpRequestException>(
            () => prefetch.RunAsync(CountedPrompt, (_, _, _) => Task.FromException<string>(new HttpRequestException())));
        prefetch.DiscardUnsentCredential();

        (await prefetch.RunAsync(CountedPrompt, (_, _, _) => Task.FromResult("finished"))).ShouldBe("finished");

        prompts.ShouldBe(2);
        prefetch.ResentCredential.ShouldBeFalse();
        service.Begun.Count.ShouldBe(2);
    }

    [TestMethod]
    public async Task CeremonyThatFailedToBegin_IsBegunWhenNeeded()
    {
        int attempts = 0;
        var ceremony = new PasskeyCeremony("ceremony", """{"challenge":"x"}""", TimeSpan.FromMinutes(5));

        var prefetch = new PasskeyCeremonyPrefetch<PasskeyCeremony>(
            _ => ++attempts is 1 ? Task.FromException<PasskeyCeremony>(new HttpRequestException()) : Task.FromResult(ceremony),
            c => c);

        prefetch.Prefetch();

        string result = await prefetch.RunAsync(Prompt, (begun, _, _) => Task.FromResult(begun.Id));

        result.ShouldBe("ceremony");
        attempts.ShouldBe(2);
    }

    [TestMethod]
    public async Task FailedBegin_IsThrownFromTheTry()
    {
        var prefetch = new PasskeyCeremonyPrefetch<PasskeyCeremony>(_ => Task.FromException<PasskeyCeremony>(new NotFoundApiException("Gone.")), c => c);

        await Should.ThrowAsync<NotFoundApiException>(() => prefetch.RunAsync(Prompt, (_, _, _) => Task.CompletedTask));
    }

    [TestMethod]
    public void Ceremony_IsBegunAgainAfterFourFifthsOfItsLifetime()
    {
        var time = new ManualTimeProvider();
        var service = new FakeService(TimeSpan.FromMinutes(5));
        var prefetch = service.CreatePrefetch(time);

        prefetch.Prefetch();
        time.Advance(TimeSpan.FromMinutes(4));
        prefetch.Prefetch();
        service.Begun.ShouldHaveSingleItem();

        time.Advance(TimeSpan.FromTicks(1));
        prefetch.Prefetch();
        service.Begun.Count.ShouldBe(2);
    }

    [TestMethod]
    public async Task ExpiredCeremony_IsBegunAgainForTheTry()
    {
        var time = new ManualTimeProvider();
        var service = new FakeService(TimeSpan.FromMinutes(5));
        var prefetch = service.CreatePrefetch(time);
        var finished = new List<SignIn>();

        prefetch.Prefetch();
        time.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromTicks(1));

        string result = await prefetch.RunAsync(Prompt, (signIn, _, _) => {
            finished.Add(signIn);
            return Task.FromResult("finished");
        });

        result.ShouldBe("finished");
        service.Begun.Count.ShouldBe(2);
        finished.ShouldBe([service.Begun[1]]);
    }

    [TestMethod]
    public async Task ExpiredUnsentCredential_IsNotSentAgain()
    {
        var time = new ManualTimeProvider();
        var service = new FakeService(TimeSpan.FromMinutes(5));
        var prefetch = service.CreatePrefetch(time);
        int prompts = 0;

        Task<PasskeyCredential> CountedPrompt(string options, CancellationToken cancellationToken)
        {
            prompts++;
            return Task.FromResult(Credential);
        }

        await Should.ThrowAsync<HttpRequestException>(
            () => prefetch.RunAsync(CountedPrompt, (_, _, _) => Task.FromException<string>(new HttpRequestException())));
        time.Advance(TimeSpan.FromMinutes(5));

        (await prefetch.RunAsync(CountedPrompt, (_, _, _) => Task.FromResult("finished"))).ShouldBe("finished");

        prompts.ShouldBe(2);
        prefetch.ResentCredential.ShouldBeFalse();
    }

    private static Task<PasskeyCredential> Prompt(string options, CancellationToken cancellationToken) => Task.FromResult(Credential);

    /// <summary>
    /// A ceremony of the app's own, which holds the service's ceremony with the app's state.
    /// </summary>
    private sealed record SignIn(PasskeyCeremony Ceremony, string Verifier);

    private sealed class FakeService(TimeSpan? lifetime = null)
    {
        public List<SignIn> Begun { get; } = [];

        public PasskeyCeremonyPrefetch<SignIn> CreatePrefetch(TimeProvider? timeProvider = null) => new(BeginAsync, s => s.Ceremony, timeProvider);

        private Task<SignIn> BeginAsync(CancellationToken cancellationToken)
        {
            int number = Begun.Count + 1;
            var ceremony = new PasskeyCeremony($"ceremony-{number}", $$"""{"challenge":"{{number}}"}""", lifetime ?? TimeSpan.FromMinutes(5));
            var signIn = new SignIn(ceremony, $"verifier-{number}");

            Begun.Add(signIn);
            return Task.FromResult(signIn);
        }
    }
}

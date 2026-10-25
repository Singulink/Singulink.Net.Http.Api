using System.Net;

namespace Singulink.Net.Http.Api.Client;

/// <summary>
/// The cues that hub connections in the browser wait for (see <see cref="ApiClientBase.WaitForRequestCueAsync"/>): the successful responses of requests
/// with a user ID precondition that began after the wait, and the user ID precondition that a URL carries.
/// </summary>
[PrefixTestClass]
public sealed class RequestCueTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task SuccessfulResponse_EndsTheWaitsForItsUserIdPrecondition_AndForAny()
    {
        var parent = new TestApiClient(new FakeHttpMessageHandler());
        var user7 = new TestApiClient(parent, userIdPrecondition: 7);
        using var cancellation = new CancellationTokenSource();

        var for7 = parent.WaitForRequestCueAsync("7", cancellation.Token);
        var for8 = user7.WaitForRequestCueAsync("8", cancellation.Token);
        var forAny = parent.WaitForRequestCueAsync(null, cancellation.Token);

        await user7.SendRequestAsync(user7.CreateGetRequest("documents"));

        (await EndsAsync(for7)).ShouldBeTrue();
        (await EndsAsync(forAny)).ShouldBeTrue();
        await Task.Delay(100);
        for8.IsCompleted.ShouldBeFalse();

        cancellation.Cancel();
    }

    [TestMethod]
    public async Task RequestWithoutUserIdPrecondition_FailedRequest_AndResponseFromAnotherHost_EndNoWait()
    {
        var handler = new FakeHttpMessageHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/forbidden", StringComparison.Ordinal) ?
                Responses.Error(HttpStatusCode.Forbidden, "Not allowed.") : Responses.NoContent());

        var parent = new TestApiClient(handler);
        var user7 = new TestApiClient(parent, userIdPrecondition: 7);
        using var cancellation = new CancellationTokenSource();
        var forAny = parent.WaitForRequestCueAsync(null, cancellation.Token);

        await parent.SendRequestAsync(parent.CreateGetRequest("documents"));
        await Should.ThrowAsync<ForbiddenApiException>(() => user7.SendRequestAsync(user7.CreateGetRequest("forbidden")));
        await user7.SendRequestAsync(new HttpRequestMessage(HttpMethod.Get, "https://other.example.com/v1/documents?if-userId=7"));

        await Task.Delay(100);
        forAny.IsCompleted.ShouldBeFalse();

        cancellation.Cancel();
    }

    [TestMethod]
    public async Task ResponseOfARequestThatBeganBeforeTheWait_EndsNoWait()
    {
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var handler = new FakeHttpMessageHandler(async (request, _) => {
            if (request.RequestUri!.AbsolutePath.EndsWith("/slow", StringComparison.Ordinal))
            {
                received.TrySetResult(true);
                await gate.Task;
            }

            return Responses.NoContent();
        });

        var parent = new TestApiClient(handler);
        var user7 = new TestApiClient(parent, userIdPrecondition: 7);
        var slowRequest = user7.SendRequestAsync(user7.CreateGetRequest("slow"));
        await received.Task;

        using var cancellation = new CancellationTokenSource();
        var wait = parent.WaitForRequestCueAsync("7", cancellation.Token);

        gate.SetResult(true);
        await slowRequest;
        await Task.Delay(100);
        wait.IsCompleted.ShouldBeFalse();

        await user7.SendRequestAsync(user7.CreateGetRequest("documents"));
        (await EndsAsync(wait)).ShouldBeTrue();
    }

    [TestMethod]
    public async Task Wait_EndsWithTheToken()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler());
        using var cancellation = new CancellationTokenSource();
        var wait = client.WaitForRequestCueAsync(null, cancellation.Token);

        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => wait);
    }

    [TestMethod]
    [DataRow("https://api.example.com/v1/documents?if-userId=7", "7")]
    [DataRow("https://api.example.com/v1/documents?page=2&IF-USERID=a%20b%26c", "a b&c")]
    [DataRow("https://api.example.com/v1/documents?if-userId=a+b", "a b")]
    [DataRow("https://api.example.com/v1/documents?if-userId=7&if-userId=7", null)]
    [DataRow("https://api.example.com/v1/documents?if-userId=", null)]
    [DataRow("https://api.example.com/v1/documents?if-userId", null)]
    [DataRow("https://api.example.com/v1/documents?page=2", null)]
    [DataRow("https://api.example.com/v1/documents", null)]
    public void UserIdPrecondition_IsReadAsTheServiceReadsIt(string url, string? expected)
    {
        new TestApiClient(new FakeHttpMessageHandler()).GetUserIdPrecondition(new Uri(url)).ShouldBe(expected);
    }

    [TestMethod]
    public void UserIdPrecondition_IsReadWithTheClientsQueryName()
    {
        var client = new TestApiClient(new FakeHttpMessageHandler()) { PreconditionName = "as-user" };

        client.GetUserIdPrecondition(new Uri("https://api.example.com/v1/documents?if-userId=7&as-user=8")).ShouldBe("8");
    }

    private static async Task<bool> EndsAsync(Task task) => await Task.WhenAny(task, Task.Delay(Timeout)) == task;
}

using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Singulink.Net.Http.Api.Client;

namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Without dynamic code (Native AOT), SignalR invokes hub methods with reflection, which wraps their exceptions in a
/// <see cref="TargetInvocationException"/>. <see cref="ApiExceptionHubFilter"/> unwraps them, which these tests simulate by throwing the wrapped
/// exceptions directly.
/// </summary>
[PrefixTestClass]
public sealed class ApiExceptionHubFilterTests
{
    public static class WrappingHubContract
    {
        public static HubMethod<string> ApiError { get; } = new();

        public static HubMethod<string> HubError { get; } = new();
    }

    public sealed class WrappingHub : Hub
    {
        public string ApiError() => throw new TargetInvocationException(new ForbiddenApiException("no access") { ErrorCode = "denied" });

        public string HubError() => throw new TargetInvocationException(new HubException("custom hub error"));
    }

    [TestMethod]
    public async Task WrappedApiException_IsReportedAsApiError()
    {
        await using var host = await StartAsync();
        await using var connection = await ConnectAsync(host);

        var ex = await Should.ThrowAsync<ForbiddenApiException>(() => connection.InvokeAsync(WrappingHubContract.ApiError));
        ex.Message.ShouldBe("no access");
        ex.ErrorCode.ShouldBe("denied");
    }

    [TestMethod]
    public async Task WrappedHubException_IsReportedWithItsMessage()
    {
        await using var host = await StartAsync();
        await using var connection = await ConnectAsync(host);

        var ex = await Should.ThrowAsync<HubException>(() => connection.InvokeAsync(WrappingHubContract.HubError));
        ex.Message.ShouldContain("custom hub error");
    }

    private static Task<TestWebHost> StartAsync()
    {
        return TestWebHost.StartAsync(
            app => app.MapHub<WrappingHub>("/hub"),
            configureServices: s => s.AddSignalR(o => o.AddApiExceptionFilter()));
    }

    private static async Task<ApiHubConnection> ConnectAsync(TestWebHost host)
    {
        var server = host.App.GetTestServer();

        var hubConnection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "hub"), options => {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        var connection = new ApiHubConnection(hubConnection);
        await connection.StartAsync();

        return connection;
    }
}

namespace Singulink.Net.Http.Api.Client;

[PrefixTestClass]
public sealed class UploadOptionsTests
{
    [TestMethod]
    public void Defaults()
    {
        var options = new UploadOptions();

        options.ChunkSize.ShouldBeNull();
        options.MaxConcurrency.ShouldBe(1);
        options.ChunkTimeout.ShouldBe(TimeSpan.FromMinutes(5));
        options.RetryTimeout.ShouldBe(TimeSpan.FromMinutes(5));
        options.CompletionIdleTimeout.ShouldBe(TimeSpan.FromSeconds(60));
        options.Progress.ShouldBeNull();
        options.TimeProvider.ShouldBeNull();
    }

    [TestMethod]
    public void ValidValues_AreKept()
    {
        var options = new UploadOptions {
            ChunkSize = 1,
            MaxConcurrency = 4,
            ChunkTimeout = Timeout.InfiniteTimeSpan,
            RetryTimeout = TimeSpan.Zero,
            CompletionIdleTimeout = Timeout.InfiniteTimeSpan,
        };

        options.ChunkSize.ShouldBe(1);
        options.MaxConcurrency.ShouldBe(4);
        options.ChunkTimeout.ShouldBe(Timeout.InfiniteTimeSpan);
        options.RetryTimeout.ShouldBe(TimeSpan.Zero);
        options.CompletionIdleTimeout.ShouldBe(Timeout.InfiniteTimeSpan);
        new UploadOptions { RetryTimeout = Timeout.InfiniteTimeSpan }.RetryTimeout.ShouldBe(Timeout.InfiniteTimeSpan);
    }

    [TestMethod]
    public void InvalidValues_Throw()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new UploadOptions { ChunkSize = 0 });
        Should.Throw<ArgumentOutOfRangeException>(() => new UploadOptions { MaxConcurrency = 0 });
        Should.Throw<ArgumentOutOfRangeException>(() => new UploadOptions { MaxConcurrency = 5 });
        Should.Throw<ArgumentOutOfRangeException>(() => new UploadOptions { ChunkTimeout = TimeSpan.Zero });
        Should.Throw<ArgumentOutOfRangeException>(() => new UploadOptions { ChunkTimeout = TimeSpan.FromSeconds(-2) });
        Should.Throw<ArgumentOutOfRangeException>(() => new UploadOptions { RetryTimeout = TimeSpan.FromSeconds(-2) });
        Should.Throw<ArgumentOutOfRangeException>(() => new UploadOptions { CompletionIdleTimeout = TimeSpan.Zero });
    }
}

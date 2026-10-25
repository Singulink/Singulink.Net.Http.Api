namespace Singulink.Net.Http.Api.Service;

[PrefixTestClass]
public sealed class ShortLivedIdTests
{
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [DataRow("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [DataRow("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [DataRow("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [DataRow("not a key, but as long as one is: 43 chars")]
    public void IsWellFormed_RejectsValuesThatCannotBeKeys(string? value)
    {
        ShortLivedId.IsWellFormed(value).ShouldBeFalse();
    }

    [TestMethod]
    public async Task IsWellFormed_AcceptsTheKeysTheToolkitHandsOut()
    {
        var store = new MemoryShortLivedStore(TimeProvider.System);

        ShortLivedId.IsWellFormed(await store.AddAsync("test:", "value", TimeSpan.FromMinutes(1))).ShouldBeTrue();
        ShortLivedId.IsWellFormed("abcdefghijklmnopqrstuvwxyz-_0123456789ABCDE").ShouldBeTrue();
        ShortLivedId.Length.ShouldBe(43);
    }
}

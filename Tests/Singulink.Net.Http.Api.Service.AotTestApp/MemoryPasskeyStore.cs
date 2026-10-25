using System.Collections.Concurrent;
using Singulink.Net.Http.Api.Service.Passkeys;

namespace Singulink.Net.Http.Api.Service.AotTestApp;

/// <summary>
/// The passkey store. It is created by dependency injection, through the constructor that takes <see cref="MemoryPasskeys"/>.
/// </summary>
internal sealed class MemoryPasskeyStore(MemoryPasskeys passkeys) : IPasskeyStore
{
    public Task<PasskeyRecord?> FindAsync(byte[] credentialId, CancellationToken cancellationToken)
    {
        return Task.FromResult(passkeys.Records.GetValueOrDefault(Convert.ToBase64String(credentialId)));
    }

    public Task<IReadOnlyList<PasskeyRecord>> GetUserPasskeysAsync(string userId, CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<PasskeyRecord>>([.. passkeys.Records.Values.Where(p => p.UserId == userId)]);
    }

    public Task UpdateAsync(PasskeyRecord passkey, DateTime usedUtc, CancellationToken cancellationToken)
    {
        passkeys.Add(passkey);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Keeps the registered passkeys, standing in for a database table.
/// </summary>
internal sealed class MemoryPasskeys
{
    public ConcurrentDictionary<string, PasskeyRecord> Records { get; } = [];

    public void Add(PasskeyRecord passkey) => Records[Convert.ToBase64String(passkey.CredentialId)] = passkey;
}

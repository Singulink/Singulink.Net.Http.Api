namespace Singulink.Net.Http.Api.Service.Passkeys.Infrastructure;

/// <summary>
/// A passkey store kept in memory, standing in for an app's passkey table.
/// </summary>
public sealed class InMemoryPasskeyStore : IPasskeyStore
{
    private readonly Lock _lock = new();
    private readonly List<StoredPasskey> _passkeys = [];

    public IReadOnlyList<StoredPasskey> Passkeys
    {
        get {
            lock (_lock)
                return _passkeys.ToList();
        }
    }

    public int UpdateCount { get; private set; }

    public void Add(PasskeyRecord passkey, string name = "Passkey")
    {
        lock (_lock)
            _passkeys.Add(new StoredPasskey(passkey, name, null));
    }

    public StoredPasskey Get(byte[] credentialId)
    {
        lock (_lock)
            return _passkeys.Single(p => p.Record.CredentialId.AsSpan().SequenceEqual(credentialId));
    }

    public Task<PasskeyRecord?> FindAsync(byte[] credentialId, CancellationToken cancellationToken)
    {
        lock (_lock)
            return Task.FromResult(_passkeys.FirstOrDefault(p => p.Record.CredentialId.AsSpan().SequenceEqual(credentialId))?.Record);
    }

    public Task<IReadOnlyList<PasskeyRecord>> GetUserPasskeysAsync(string userId, CancellationToken cancellationToken)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<PasskeyRecord>>(_passkeys.Where(p => p.Record.UserId == userId).Select(p => p.Record).ToList());
    }

    public Task UpdateAsync(PasskeyRecord passkey, DateTime usedUtc, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            int index = _passkeys.FindIndex(p => p.Record.CredentialId.AsSpan().SequenceEqual(passkey.CredentialId));
            var stored = _passkeys[index];

            var updated = new PasskeyRecord {
                UserId = stored.Record.UserId,
                CredentialId = stored.Record.CredentialId,
                PublicKey = stored.Record.PublicKey,
                SignCount = passkey.SignCount,
                Transports = stored.Record.Transports,
                IsBackupEligible = stored.Record.IsBackupEligible,
                IsBackedUp = passkey.IsBackedUp,
                AaGuid = stored.Record.AaGuid,
            };

            _passkeys[index] = stored with { Record = updated, LastUsedUtc = usedUtc };
            UpdateCount++;
        }

        return Task.CompletedTask;
    }

    public sealed record StoredPasskey(PasskeyRecord Record, string Name, DateTime? LastUsedUtc);
}

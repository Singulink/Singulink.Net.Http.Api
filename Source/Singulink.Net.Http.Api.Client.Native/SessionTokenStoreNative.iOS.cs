#if __IOS__
using Foundation;
using Security;

namespace Singulink.Net.Http.Api.Client;

/// <content>
/// The iOS storage: keychain items that only this device can read, while it is unlocked. Their accessibility keeps backups from carrying them to other
/// devices.
/// </content>
internal static partial class SessionTokenStoreNative
{
    private static bool IsSupportedCore => true;

    private static string? ReadCore(string name)
    {
        var record = SecKeyChain.QueryAsRecord(CreateQuery(name), out var status);

        if (status is SecStatusCode.ItemNotFound)
            return null;

        if (status is not SecStatusCode.Success)
            throw new InvalidOperationException($"The session token could not be read from the keychain ({status}).");

        return record?.ValueData?.ToString(NSStringEncoding.UTF8);
    }

    private static void WriteCore(string name, string? token)
    {
        if (token is null)
        {
            var removeStatus = SecKeyChain.Remove(CreateQuery(name));

            if (removeStatus is not (SecStatusCode.Success or SecStatusCode.ItemNotFound))
                throw new InvalidOperationException($"The session token could not be removed from the keychain ({removeStatus}).");

            return;
        }

        var data = NSData.FromString(token, NSStringEncoding.UTF8);

        // The item is changed in place, so that a write that fails (such as while the device is locked) keeps the token that was stored before.
        var status = SecKeyChain.Update(CreateQuery(name), new SecRecord { ValueData = data });

        if (status is SecStatusCode.ItemNotFound)
        {
            status = SecKeyChain.Add(new SecRecord(SecKind.GenericPassword) {
                Service = StoreName,
                Account = name,
                Accessible = SecAccessible.WhenUnlockedThisDeviceOnly,
                ValueData = data,
            });
        }

        if (status is not SecStatusCode.Success)
            throw new InvalidOperationException($"The session token could not be stored in the keychain ({status}).");
    }

    private static SecRecord CreateQuery(string name) => new(SecKind.GenericPassword) { Service = StoreName, Account = name };
}
#endif

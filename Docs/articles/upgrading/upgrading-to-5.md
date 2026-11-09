<div class="article">

# Upgrading to 5.0

This guide covers upgrading an app from 4.x to 5.0. Most of the changes are in chunked uploads: apps register their upload store, and stores of an app's own implement a few new members.

## Before Upgrading

- Upgrade servers that share an upload directory or store together: 4.x and 5.0 servers can't share one.
- Uploads that 4.x finished are removed when the 5.0 app starts, so a completion made again for one of them fails with `404` (`upload-not-found`). Uploads that are still receiving chunks carry on.
- 4.x clients keep working against a 5.0 service. A 5.0 client of a 4.x service uploads small files with the three requests after the service answers its direct upload with `405`.

## Registration and Options

- `AddChunkedUploads` no longer registers an upload store, and `ChunkedUploadOptions.Directory` is gone. Register the directory store with <xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddDirectoryUploadStore*>, setting its required <xref:Singulink.Net.Http.Api.Service.Uploads.DirectoryUploadStoreOptions.Directory> and <xref:Singulink.Net.Http.Api.Service.Uploads.DirectoryUploadStoreOptions.SharingMode>, or register a store of your own; without one, the app fails to start (see [The directory store](../guides/uploads.md#the-directory-store)).
- With <xref:Singulink.Net.Http.Api.Service.StoreSharingMode.ProcessLocal>, the directory store locks its directory, so a second process that starts while the first runs, such as with IIS overlapped recycling, fails to start. With <xref:Singulink.Net.Http.Api.Service.StoreSharingMode.Shared>, the short-lived store must be shared too.
- Paths are absolute paths of [Singulink.IO.FileSystem](https://github.com/Singulink/Singulink.IO.FileSystem):
  - <xref:Singulink.Net.Http.Api.Service.Uploads.DirectoryUploadStoreOptions.Directory> is an `IAbsoluteDirectoryPath`.
  - `CloudflareForwardingOptions.SavedRangesPath` is replaced by <xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.SavedRangesFile>, an `IAbsoluteFilePath`.
  - `UploadedFile.FilePath` is replaced by <xref:Singulink.Net.Http.Api.Service.Uploads.UploadedFile.File>, and <xref:Singulink.Net.Http.Api.Service.Uploads.UploadedFile.MoveToAsync*> and the matching members of <xref:Singulink.Net.Http.Api.Service.Uploads.UploadContent> take an `IAbsoluteFilePath`.
  - <xref:Singulink.Net.Http.Api.Client.SessionTokenStore.FolderPath?displayProperty=nameWithType> is an `IAbsoluteDirectoryPath`.
  - Relative paths are no longer resolved against the content root, and a path with names that are not friendly, such as one that ends with a space or a dot, throws an <xref:System.ArgumentException>.
- <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyOptions.RelyingPartyId>, <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyOptions.RelyingPartyName>, <xref:Singulink.Net.Http.Api.Service.CloudflareForwardingOptions.SavedRangesFile> and <xref:Singulink.Net.Http.Api.Service.ClientRateLimitOptions.PermitLimit> are `required`: options created in code set them, and options bound from configuration without them fail at startup.
- `PasskeyOptions.RequireUserVerification` and `RequireDiscoverableCredentials` are replaced by <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyOptions.UserVerification> and <xref:Singulink.Net.Http.Api.Service.Passkeys.PasskeyOptions.DiscoverableCredentials>: `true` becomes `Required` (the default) and `false` becomes `Preferred`.
- Options can't be changed once the library has taken them, and an invalid value throws when it is set rather than when the app starts.
- <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.IdleExpiry> no longer limits how long a handler runs; <xref:Singulink.Net.Http.Api.Service.Uploads.ChunkedUploadOptions.ProcessingTimeout> does, 1 hour by default. An app that set `IdleExpiry` to stop handlers sooner sets `ProcessingTimeout` instead.
- `IdleExpiry` is 15 minutes by default rather than 1 day, so a client that is away longer between requests finds its upload gone.
- The limits of open uploads are counted in the short-lived store, which servers that share uploads must share too (see [Limits of open uploads](../guides/uploads.md#limits-of-open-uploads)). `AddShortLivedStore(options => ...)` no longer sets the memory store's size: the named options of each key of <xref:Singulink.Net.Http.Api.Service.ShortLivedStoreKeys> do.

## Cancelling Uploads

- A cancellation no longer deletes an upload whose handler is running: it cancels the handler's token, and a handler that finishes anyway has its outcome stored. Handlers stop or finish once their token is cancelled (see [Cancelling an upload](../guides/uploads.md#cancelling-an-upload)).
- An upload of the toolkit's client that is cancelled after its handler may have started waits for the service, and returns the handler's result if it finished (see [Cancellation and Aborting](../guides/uploads-client.md#cancellation-and-aborting)).

## Direct Uploads and Upload IDs

- A file that fits in one chunk is uploaded in one request, `POST {pattern}/uploads/direct`, with the file as its body (see [Direct Uploads](../guides/uploads.md#direct-uploads)). Proxy, firewall and gateway rules, rate limits of the endpoint group, and middleware, filters, validators or handlers that read the request see it.
- An app with a CORS policy of its own allows the `Upload-Creation` request header for its upload endpoints and exposes `Location`.
- An override of `SendStreamingAsync` or an `HttpHandler` that matches upload paths also sees direct uploads, and tests with a fake service or <xref:Singulink.Net.Http.Api.Service.Testing.FaultInjectionHandler> see them first for small files.
- Clients choose the IDs of their uploads, GUIDs (<xref:Singulink.Net.Http.Api.DataContracts.CreateUploadCommand.Id?displayProperty=nameWithType>), in place of idempotency keys (see [Upload IDs](../guides/uploads.md#upload-ids)). A validator can run more than once for one upload.
- New error codes: `upload-direct-too-large`, `upload-creation-header-too-large`, `upload-cancelled`, `upload-finished`, `upload-id-conflict` and `upload-cancellation-unavailable`. An exception handler that words errors by their codes covers them too (see [Messages of the service's errors](../guides/uploads.md#messages-of-the-services-errors)).

## Stores of Your Own

An <xref:Singulink.Net.Http.Api.Service.Uploads.IUploadStore> of the app's own keeps finished uploads apart from the open ones (see [Stores of your own](../guides/uploads.md#stores-of-your-own)):

- New members without a default implementation: <xref:Singulink.Net.Http.Api.Service.Uploads.IUploadStore.SharingMode>, <xref:Singulink.Net.Http.Api.Service.Uploads.IUploadStore.GetUsageAsync*> and <xref:Singulink.Net.Http.Api.Service.Uploads.IUploadStore.RemoveOutcomesAsync*>.
- Uploads are kept by their user and ID. <xref:Singulink.Net.Http.Api.Service.Uploads.IUploadStore.TryCreateAsync*> returns an <xref:Singulink.Net.Http.Api.Service.Uploads.UploadCreationResult>, and creates an upload unless the user has one with its ID.
- The idempotency key members are gone: `ReserveKeyAsync`, `ReleaseKeyAsync`, `IsKeyReservedAsync`, `UploadKeyReservation` and `UploadRecord.IdempotencyKey`.
- <xref:Singulink.Net.Http.Api.Service.Uploads.IUploadStore.ListAsync*> lists only open uploads, and has no `userId` or `withoutOutcomes` parameter.
- <xref:Singulink.Net.Http.Api.Service.Uploads.IUploadStore.TrySetOutcomeAsync*>, <xref:Singulink.Net.Http.Api.Service.Uploads.IUploadStore.GetAsync*>, <xref:Singulink.Net.Http.Api.Service.Uploads.IUploadStore.DeleteAsync*> and <xref:Singulink.Net.Http.Api.Service.Uploads.IUploadStore.WriteChunkAsync*> have new guarantees, which <xref:Singulink.Net.Http.Api.Service.Testing.UploadStoreContract.CheckAsync*?displayProperty=nameWithType> and the new <xref:Singulink.Net.Http.Api.Service.Testing.UploadStoreContract.CheckOutcomeRemovalAsync*?displayProperty=nameWithType> check. The latter removes outcomes of all users, so it runs only against a store that holds test data.

An <xref:Singulink.Net.Http.Api.Service.IShortLivedStore> of the app's own implements the new <xref:Singulink.Net.Http.Api.Service.IShortLivedStore.SharingMode>, <xref:Singulink.Net.Http.Api.Service.IShortLivedStore.SetAsync*> and <xref:Singulink.Net.Http.Api.Service.IShortLivedStore.GetUsageAsync*> (see [The operations](../guides/multiple-servers.md#the-operations)).

## Next Steps

Continue with these related articles:

- [Chunked Uploads](../guides/uploads.md) - Upload endpoints, their limits and their storage.
- [Chunked Uploads in Client Apps](../guides/uploads-client.md) - Sending files from client apps.
- [Upload Format](../concepts/upload-format.md) - The requests and responses of the protocol.

</div>

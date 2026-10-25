<div class="article">

# JSON Serialization

The toolkit configures JSON serializer options with the app's type info resolver and its null handling, on the service, in clients and in any other options instance. This guide covers what the null handling does, where each helper applies it, and the order the helpers depend on.

## The Null Handling

Options with the null handling read JSON as follows:

- A `null` value for a property or constructor parameter that is not nullable is rejected.
- A missing value for a required member that is not nullable is rejected: a constructor parameter without a default value, a `required` member or a member with <xref:System.Text.Json.Serialization.JsonRequiredAttribute>.
- Required members that are nullable are optional: a missing constructor parameter is read as `null`, and a missing property is left as it is.

Writing rejects a `null` value in a property that is not nullable too. Other `null` values are written, unless <xref:System.Text.Json.JsonSerializerOptions.DefaultIgnoreCondition> leaves them out.

Rejected values throw a <xref:System.Text.Json.JsonException>, which is not an <xref:Singulink.Net.Http.Api.ApiException>. A service reports a request body that it rejects to the client as a `400` (see [Requests That Cannot Be Read](exception-handling.md#requests-that-cannot-be-read)). A response that it rejects fails like an unexpected exception, a streaming response ends with an error record, and SignalR closes the connection of a hub method whose result it rejects. A client's request method throws the <xref:System.Text.Json.JsonException> for a response body that it rejects, and for a command that it rejects when it sends the request. With this contract:

```csharp
public sealed record CreateNoteCommand(string Title, string? Body, int? Priority, int FolderId)
{
    public required string ClientId { get; init; }

    public required string? Color { get; init; }
}
```

| JSON | Result |
| --- | --- |
| `{"title":"t","folderId":1,"clientId":"c"}` | Read, with `Body`, `Priority` and `Color` set to `null` |
| `{"title":"t","body":null,"priority":null,"folderId":1,"clientId":"c","color":null}` | Read, with `Body`, `Priority` and `Color` set to `null` |
| `{"folderId":1,"clientId":"c"}` | Rejected: `title` is missing |
| `{"title":null,"folderId":1,"clientId":"c"}` | Rejected: `title` is `null` |
| `{"title":"t","clientId":"c"}` | Rejected: `folderId` is missing |
| `{"title":"t","folderId":1}` | Rejected: `clientId` is missing |

Writing `new CreateNoteCommand("t", null, null, 1) { ClientId = "c", Color = null }` produces `{"title":"t","body":null,"priority":null,"folderId":1,"clientId":"c","color":null}`, which reads back as the same command. A `null` value that is left out is read as the member's default value instead, such as `50` for `int? Limit { get; init; } = 50`.

System.Text.Json's <xref:System.Text.Json.JsonSerializerOptions.RespectNullableAnnotations> and <xref:System.Text.Json.JsonSerializerOptions.RespectRequiredConstructorParameters> settings reject the `null` values and the missing constructor parameters, but the second one also rejects a missing constructor parameter that is nullable, and System.Text.Json always rejects a missing `required` or <xref:System.Text.Json.Serialization.JsonRequiredAttribute> member, nullable or not. The null handling turns both settings on and makes the required members that are nullable optional, in the type info that the options' resolvers provide, with source-generated and reflection-based contracts alike.

## Services

<xref:Singulink.Net.Http.Api.Service.ServiceCollectionExtensions.AddApiJson*> configures the JSON options of minimal API requests and responses (including streamed items) and of SignalR hub payloads:

```csharp
services.AddApiJson(AppJsonContext.Default, options => options.Converters.Add(new JsonStringEnumConverter()));
```

For each of the two options instances, it inserts the resolver at the front of the options' type info resolvers, runs the callback, then applies the null handling, so resolvers that the callback adds are covered too. The hub payload options are configured whether `AddSignalR` is called before or after `AddApiJson`, and are only used if SignalR is registered.

> [!IMPORTANT]
> Configuration of either options instance that is registered after `AddApiJson`, such as another `ConfigureHttpJsonOptions` call or an `AddJsonProtocol` callback, runs after it. Resolvers that it adds are not covered by the null handling: in the types they provide, nullable members that are required, such as constructor parameters without a default value, stay required, so a request that leaves out such a value is rejected.

The toolkit's own JSON, such as the session cookie, the states it keeps in the short-lived store and the records of streaming responses, uses type info of its own and is not affected by the app's options. Error responses are plain text (see [Error Response Format](../concepts/error-format.md)).

## Clients

The <xref:Singulink.Net.Http.Api.Client.ApiClientBase> constructors that take a type info resolver create options with <xref:System.Text.Json.JsonSerializerDefaults.Web?displayProperty=nameWithType> defaults and the null handling, and so do the <xref:Singulink.Net.Http.Api.Client.SignalRApiClientBase> constructors, whose hub connections use the same options for their payloads. The configuration callback runs after the null handling is applied, so the settings it makes take precedence:

```csharp
public MyApiClient(string? sessionToken, Action<string?>? sessionTokenChanged)
    : base(AppJsonContext.Default, sessionToken, sessionTokenChanged, options => options.Converters.Add(new JsonStringEnumConverter())) { }
```

Resolvers that the callback adds are not covered by the null handling, unless the callback applies it again with <xref:Singulink.Net.Http.Api.ApiNullHandlingExtensions.ConfigureApiNullHandling*> after adding them. The constructors that take <xref:System.Text.Json.JsonSerializerOptions> use the options as they are, and the reflection-based constructors without options use web defaults without the null handling.

With every constructor, a response whose body is `null` throws a <xref:System.Text.Json.JsonException>, since the type that a request reads its response as doesn't allow `null`.

## Other Options Instances

<xref:Singulink.Net.Http.Api.ApiNullHandlingExtensions.ConfigureApiNullHandling*> applies the null handling to an options instance and returns it:

```csharp
var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = AppJsonContext.Default }.ConfigureApiNullHandling();
```

It sets <xref:System.Text.Json.JsonSerializerOptions.RespectNullableAnnotations> and <xref:System.Text.Json.JsonSerializerOptions.RespectRequiredConstructorParameters> to `true`, and makes required members that are nullable optional in the type info of the resolvers that the options have when it is called. It leaves <xref:System.Text.Json.JsonSerializerOptions.DefaultIgnoreCondition> as it is.

> [!IMPORTANT]
> It must be called after the options' contexts and other resolvers are added: resolvers added afterwards are not covered. Calling it again also covers the resolvers added since, and leaves the ones it already covers as they are.

It throws an <xref:System.InvalidOperationException> if the options have no resolver yet, or if they are read-only, as options are once they have been used for serialization.

#### Source-generated type info

Type info that a source-generated context provides for its own options, such as `AppJsonContext.Default.CreateNoteCommand`, is not affected by the null handling, and <xref:System.Text.Json.Serialization.JsonSourceGenerationOptionsAttribute> has no setting for the rest of the null handling. That type info follows the context's own settings: `required` members are required, nullable or not, and with `RespectRequiredConstructorParameters = true` in the attribute, so is every constructor parameter without a default value. An options instance with the null handling provides type info that has it, and <xref:Singulink.Net.Http.Api.JsonSerializerOptionsExtensions.GetTypeInfo*> returns it typed:

```csharp
var typeInfo = options.GetTypeInfo<CreateNoteCommand>();
var command = JsonSerializer.Deserialize(json, typeInfo);
```

The <xref:System.Text.Json.JsonSerializer> overloads that take type info work in trimmed and Native AOT apps, and the ones that take options warn there.

## Next Steps

Continue with these related articles:

- [Exception Handling](exception-handling.md) - How requests that cannot be read are reported to the client.
- [Building API Clients](api-clients.md) - Constructing a client and its serializer options.
- [Trimming and Native AOT](trimming-and-aot.md) - The type info that trimmed and Native AOT apps provide.

</div>

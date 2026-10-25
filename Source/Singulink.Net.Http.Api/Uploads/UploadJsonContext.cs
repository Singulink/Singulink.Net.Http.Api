using System.Text.Json;
using System.Text.Json.Serialization;
using Singulink.Net.Http.Api.DataContracts;

namespace Singulink.Net.Http.Api;

/// <summary>
/// The JSON type info of the contracts of chunked uploads, which the service and the client serialize with this context rather than with the app's
/// options, so that apps do not list them in their own contexts. A missing or <see langword="null"/> value where a contract does not allow one is
/// rejected, and <see langword="null"/> values are left out when writing.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(CreateUploadCommand))]
[JsonSerializable(typeof(UploadSession))]
[JsonSerializable(typeof(UploadStatus))]
internal sealed partial class UploadJsonContext : JsonSerializerContext;

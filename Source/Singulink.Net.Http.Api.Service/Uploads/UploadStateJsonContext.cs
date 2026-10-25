using System.Text.Json;
using System.Text.Json.Serialization;

namespace Singulink.Net.Http.Api.Service.Uploads;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UploadMetadata))]
[JsonSerializable(typeof(UploadOutcome))]
internal sealed partial class UploadStateJsonContext : JsonSerializerContext;

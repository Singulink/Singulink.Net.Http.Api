using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Singulink.Net.Http.Api;

/// <summary>
/// Extension methods for <see cref="JsonSerializerOptions"/>.
/// </summary>
public static class JsonSerializerOptionsExtensions
{
    /// <summary>
    /// Gets the type info for <typeparamref name="T"/> that the options provide, as <see cref="JsonSerializerOptions.GetTypeInfo(Type)"/> does.
    /// </summary>
    /// <typeparam name="T">The type to get the type info for.</typeparam>
    /// <param name="options">The options that provide the type info.</param>
    /// <exception cref="NotSupportedException">The options' type info resolvers provide no type info for <typeparamref name="T"/>.</exception>
    public static JsonTypeInfo<T> GetTypeInfo<T>(this JsonSerializerOptions options) => (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
}

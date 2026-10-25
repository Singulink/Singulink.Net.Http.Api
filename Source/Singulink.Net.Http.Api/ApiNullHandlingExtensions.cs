using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Singulink.Net.Http.Api;

/// <summary>
/// Extension methods that apply the toolkit's null handling to JSON serializer options.
/// </summary>
/// <remarks>
/// <para>
/// With the null handling, reading JSON rejects a <see langword="null"/> value for a property or constructor parameter that is not nullable, and a missing
/// value for a required member that is not nullable: a constructor parameter without a default value, a <see langword="required"/> member or a member with
/// <see cref="JsonRequiredAttribute"/>. Required members that are nullable are optional: a missing constructor parameter is read as <see langword="null"/>,
/// and a missing property is left as it is. Rejected values throw a <see cref="JsonException"/>.</para>
/// <para>
/// Writing JSON rejects a <see langword="null"/> value in a property that is not nullable too, with a <see cref="JsonException"/>. Other <see
/// langword="null"/> values are written unless <see cref="JsonSerializerOptions.DefaultIgnoreCondition"/> says otherwise, and are read back as <see
/// langword="null"/>, also for a member whose default value is not <see langword="null"/>.</para>
/// </remarks>
public static class ApiNullHandlingExtensions
{
    /// <summary>
    /// Applies the null handling to the options and to the type info of their current type info resolvers (see <see cref="ApiNullHandlingExtensions"/>).
    /// It must be called after the options' contexts and other resolvers are added: resolvers added afterwards are not covered.
    /// </summary>
    /// <param name="options">The options to configure.</param>
    /// <returns>The options, so that calls can be chained.</returns>
    /// <remarks>
    /// <para>
    /// Sets <see cref="JsonSerializerOptions.RespectNullableAnnotations"/> and <see cref="JsonSerializerOptions.RespectRequiredConstructorParameters"/> to
    /// <see langword="true"/>, and makes required members that are nullable optional in the type info that the options' resolvers provide.</para>
    /// <para>
    /// Calling it again also covers the resolvers added since the last call. Type info that a source-generated context provides for its own options, such
    /// as <c>MyJsonContext.Default.MyType</c>, is not affected.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The options are read-only, or have no type info resolver.</exception>
    public static JsonSerializerOptions ConfigureApiNullHandling(this JsonSerializerOptions options)
    {
        if (options.IsReadOnly)
        {
            throw new InvalidOperationException(
                "The JSON serializer options are read-only, so the API null handling cannot be applied to them. Apply it before the options are used.");
        }

        var resolvers = options.TypeInfoResolverChain;

        if (resolvers.Count is 0)
        {
            throw new InvalidOperationException(
                "The JSON serializer options have no type info resolver, so the API null handling has nothing to apply to. Add your context/resolver first, " +
                $"then call {nameof(ConfigureApiNullHandling)}().");
        }

        options.RespectNullableAnnotations = true;
        options.RespectRequiredConstructorParameters = true;

        if (resolvers is not [NullHandlingResolver])
        {
            // Resolvers that already have the null handling are unwrapped, so that it is applied once.
            var combined = JsonTypeInfoResolver.Combine([.. resolvers.Select(r => r is NullHandlingResolver handling ? handling.Inner : r)]);
            options.TypeInfoResolver = new NullHandlingResolver(combined);
        }

        return options;
    }

    private sealed class NullHandlingResolver(IJsonTypeInfoResolver inner) : IJsonTypeInfoResolver
    {
        public IJsonTypeInfoResolver Inner => inner;

        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            var typeInfo = inner.GetTypeInfo(type, options);

            if (typeInfo is not null)
            {
                // IsSetNullable covers constructor parameters too: a parameter's nullability is that of its property.
                foreach (var property in typeInfo.Properties)
                {
                    if (property.IsRequired && property.IsSetNullable)
                        property.IsRequired = false;
                }
            }

            return typeInfo;
        }
    }
}

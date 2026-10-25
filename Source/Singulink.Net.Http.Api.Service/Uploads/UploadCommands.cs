using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace Singulink.Net.Http.Api.Service.Uploads;

/// <summary>
/// Reads the app's commands of uploads, which the client sends as JSON with the creation.
/// </summary>
internal static class UploadCommands
{
    private static readonly ConcurrentDictionary<ParameterInfo, bool> _isRequiredCache = [];

    /// <summary>
    /// Reads the command of an <see cref="UploadRequest{TCommand}"/> or <see cref="UploadedFile{TCommand}"/> parameter, which is required unless its
    /// command type allows <see langword="null"/>.
    /// </summary>
    public static TCommand Read<TCommand>(JsonElement? command, ParameterInfo parameter, JsonSerializerOptions serializerOptions)
    {
        bool isRequired = _isRequiredCache.GetOrAdd(parameter, IsRequired);
        return (TCommand)Read(command, typeof(TCommand), isRequired, serializerOptions)!;
    }

    /// <summary>
    /// Reads a command of the specified type.
    /// </summary>
    /// <exception cref="BadRequestApiException">The command is missing and required, or cannot be read.</exception>
    public static object? Read(JsonElement? command, Type commandType, bool isRequired, JsonSerializerOptions serializerOptions)
    {
        if (command is not { ValueKind: not JsonValueKind.Null } element)
            return isRequired ? throw UploadErrors.CommandMissing() : null;

        try
        {
            return element.Deserialize(serializerOptions.GetTypeInfo(commandType)) ?? (isRequired ? throw UploadErrors.CommandMissing() : null);
        }
        catch (JsonException ex)
        {
            throw UploadErrors.CommandInvalid(ex);
        }
    }

    /// <summary>
    /// Gets a value indicating whether the command of an <see cref="UploadRequest{TCommand}"/> or <see cref="UploadedFile{TCommand}"/> parameter is
    /// required: its command type is a value type other than <see cref="Nullable{T}"/>, or a reference type that the parameter declares as not nullable.
    /// </summary>
    public static bool IsRequired(ParameterInfo parameter)
    {
        var commandType = GetCommandType(parameter.ParameterType)!;

        if (commandType.IsValueType)
            return Nullable.GetUnderlyingType(commandType) is null;

        var nullability = new NullabilityInfoContext().Create(parameter);
        return nullability.GenericTypeArguments is [{ ReadState: NullabilityState.NotNull }];
    }

    /// <summary>
    /// Gets the command type of an <see cref="UploadRequest{TCommand}"/> or <see cref="UploadedFile{TCommand}"/> type, or a type derived from one, or
    /// <see langword="null"/> for another type.
    /// </summary>
    public static Type? GetCommandType(Type type)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (t.IsGenericType && (t.GetGenericTypeDefinition() == typeof(UploadRequest<>) || t.GetGenericTypeDefinition() == typeof(UploadedFile<>)))
                return t.GetGenericArguments()[0];
        }

        return null;
    }
}

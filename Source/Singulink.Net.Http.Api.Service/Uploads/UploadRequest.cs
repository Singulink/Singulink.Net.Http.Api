using System.Reflection;

namespace Singulink.Net.Http.Api.Service.Uploads;

#pragma warning disable SA1402 // File may only contain a single type

/// <summary>
/// Represents the creation of a chunked upload, which the validator of the upload takes as a parameter: the file's name, length and content type as the
/// client sent them.
/// </summary>
public class UploadRequest : IBindableFromHttpContext<UploadRequest>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UploadRequest"/> class.
    /// </summary>
    /// <param name="fileName">The name of the file.</param>
    /// <param name="length">The length of the file in bytes.</param>
    /// <param name="contentType">The content type of the file, if the client sent one.</param>
    public UploadRequest(string fileName, long length, string? contentType = null)
    {
        FileName = fileName;
        Length = length;
        ContentType = contentType;
    }

    /// <summary>
    /// Gets the name of the file.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// Gets the length of the file in bytes. The upload has exactly this many bytes.
    /// </summary>
    public long Length { get; }

    /// <summary>
    /// Gets the content type of the file, or <see langword="null"/> if the client sent none.
    /// </summary>
    public string? ContentType { get; }

    /// <summary>
    /// Binds the upload request parameter of the validator of a chunked upload.
    /// </summary>
    /// <exception cref="InvalidOperationException">The parameter is not a parameter of the validator of a chunked upload.</exception>
    static ValueTask<UploadRequest?> IBindableFromHttpContext<UploadRequest>.BindAsync(HttpContext context, ParameterInfo parameter)
    {
        return new(UploadRequestBinding.Get(context).Request);
    }
}

/// <summary>
/// Represents the creation of a chunked upload with the app's command, which the validator of the upload takes as a parameter.
/// </summary>
/// <typeparam name="TCommand">The type of the command.</typeparam>
public class UploadRequest<TCommand> : UploadRequest, IBindableFromHttpContext<UploadRequest<TCommand>>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UploadRequest{TCommand}"/> class.
    /// </summary>
    /// <param name="fileName">The name of the file.</param>
    /// <param name="length">The length of the file in bytes.</param>
    /// <param name="command">The app's command.</param>
    /// <param name="contentType">The content type of the file, if the client sent one.</param>
    public UploadRequest(string fileName, long length, TCommand command, string? contentType = null) : base(fileName, length, contentType)
    {
        Command = command;
    }

    /// <summary>
    /// Gets the app's command, read with the app's JSON options.
    /// </summary>
    public TCommand Command { get; }

    /// <summary>
    /// Binds the upload request parameter of the validator of a chunked upload, with the command.
    /// </summary>
    /// <exception cref="InvalidOperationException">The parameter is not a parameter of the validator of a chunked upload.</exception>
    /// <exception cref="BadRequestApiException">The command is missing where the parameter does not allow <see langword="null"/>, or cannot be
    /// read.</exception>
    static ValueTask<UploadRequest<TCommand>?> IBindableFromHttpContext<UploadRequest<TCommand>>.BindAsync(HttpContext context, ParameterInfo parameter)
    {
        var binding = UploadRequestBinding.Get(context);
        var request = binding.Request;
        var command = UploadCommands.Read<TCommand>(binding.Command, parameter, binding.SerializerOptions);

        return new(new UploadRequest<TCommand>(request.FileName, request.Length, command, request.ContentType));
    }
}

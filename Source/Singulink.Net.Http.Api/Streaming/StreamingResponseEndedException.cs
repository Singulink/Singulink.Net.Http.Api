#if !NET
namespace Singulink.Net.Http.Api;

/// <summary>
/// The exception that is thrown when a streaming response ends before its end record, on runtimes without <c>HttpIOException</c>, so that API clients
/// can tell it from other I/O errors.
/// </summary>
internal sealed class StreamingResponseEndedException(string message) : IOException(message);
#endif

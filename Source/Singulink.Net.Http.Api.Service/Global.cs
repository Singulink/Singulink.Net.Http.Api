using System.Diagnostics.CodeAnalysis;

// CA2007: Consider calling ConfigureAwait on the awaited task
[assembly: SuppressMessage("Reliability", "CA2007", Justification = "ASP.NET Core does not have a sync context")]

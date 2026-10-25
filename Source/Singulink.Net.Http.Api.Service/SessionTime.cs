namespace Singulink.Net.Http.Api.Service;

/// <summary>
/// Date and time arithmetic for session times, which can involve very large limits.
/// </summary>
internal static class SessionTime
{
    /// <summary>
    /// Adds a time span to a date and time, limited to the range of <see cref="DateTime"/>.
    /// </summary>
    public static DateTime Add(DateTime dateTime, TimeSpan timeSpan)
    {
        if (timeSpan.Ticks > 0 && timeSpan.Ticks > DateTime.MaxValue.Ticks - dateTime.Ticks)
            return DateTime.SpecifyKind(DateTime.MaxValue, dateTime.Kind);

        if (timeSpan.Ticks < -dateTime.Ticks)
            return DateTime.SpecifyKind(DateTime.MinValue, dateTime.Kind);

        return dateTime + timeSpan;
    }
}

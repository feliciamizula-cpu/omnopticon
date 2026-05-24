namespace Argus.AgentService.Agents;

using Cronos;

internal static class AgentScheduleCalculator
{
    public static DateTimeOffset? GetNextRun(string? scheduleExpression, DateTimeOffset fromUtc)
    {
        if (string.IsNullOrWhiteSpace(scheduleExpression))
            return null;

        var expression = scheduleExpression.Trim();

        if (TryParseEveryExpression(expression, out var interval))
            return fromUtc.Add(interval);

        try
        {
            var cron = CronExpression.Parse(expression, CronFormat.Standard);
            return cron.GetNextOccurrence(fromUtc.UtcDateTime, TimeZoneInfo.Utc);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseEveryExpression(string expression, out TimeSpan interval)
    {
        interval = TimeSpan.Zero;
        var normalized = expression.Trim().ToLowerInvariant();

        if (!normalized.StartsWith("every ", StringComparison.Ordinal))
            return false;

        var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !int.TryParse(parts[1], out var value) || value < 1)
            return false;

        interval = parts[2] switch
        {
            "minute" or "minutes" => TimeSpan.FromMinutes(value),
            "hour" or "hours" => TimeSpan.FromHours(value),
            "day" or "days" => TimeSpan.FromDays(value),
            _ => TimeSpan.Zero
        };

        return interval > TimeSpan.Zero;
    }
}

namespace Argus.AgentService.Agents;

internal static class AgentScheduleCalculator
{
    public static DateTimeOffset? GetNextRun(string? scheduleExpression, DateTimeOffset fromUtc)
    {
        if (string.IsNullOrWhiteSpace(scheduleExpression))
            return null;

        var expression = scheduleExpression.Trim();

        if (TryParseEveryExpression(expression, out var interval))
            return fromUtc.Add(interval);

        return TryGetNextCronOccurrence(expression, fromUtc);
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

    private static DateTimeOffset? TryGetNextCronOccurrence(string expression, DateTimeOffset fromUtc)
    {
        var parts = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 5)
            return null;

        var minute = parts[0];
        var hour = parts[1];

        if (minute == "*" && hour == "*")
            return TruncateToMinute(fromUtc).AddMinutes(1);

        if (minute.StartsWith("*/", StringComparison.Ordinal)
            && int.TryParse(minute[2..], out var minuteInterval)
            && minuteInterval > 0
            && hour == "*")
        {
            var next = TruncateToMinute(fromUtc).AddMinutes(1);
            while (next.Minute % minuteInterval != 0)
                next = next.AddMinutes(1);
            return next;
        }

        if (int.TryParse(minute, out var fixedMinute) && fixedMinute is >= 0 and <= 59)
        {
            if (hour == "*")
            {
                var next = new DateTimeOffset(
                    fromUtc.Year, fromUtc.Month, fromUtc.Day, fromUtc.Hour, fixedMinute, 0, TimeSpan.Zero);
                return next > fromUtc ? next : next.AddHours(1);
            }

            if (hour.StartsWith("*/", StringComparison.Ordinal)
                && int.TryParse(hour[2..], out var hourInterval)
                && hourInterval > 0)
            {
                var next = TruncateToHour(fromUtc).AddMinutes(fixedMinute);
                if (next <= fromUtc)
                    next = next.AddHours(1);
                while (next.Hour % hourInterval != 0)
                    next = next.AddHours(1);
                return next;
            }

            if (int.TryParse(hour, out var fixedHour) && fixedHour is >= 0 and <= 23)
            {
                var next = new DateTimeOffset(
                    fromUtc.Year, fromUtc.Month, fromUtc.Day, fixedHour, fixedMinute, 0, TimeSpan.Zero);
                return next > fromUtc ? next : next.AddDays(1);
            }
        }

        return null;
    }

    private static DateTimeOffset TruncateToMinute(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, TimeSpan.Zero);

    private static DateTimeOffset TruncateToHour(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, value.Hour, 0, 0, TimeSpan.Zero);
}

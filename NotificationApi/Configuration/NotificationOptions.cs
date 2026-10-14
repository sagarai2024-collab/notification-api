namespace NotificationApi.Configuration;

// Bound from the "Notifications" section of appsettings.json.
public class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>The same request inside this many minutes is treated as a client retry.</summary>
    public int DuplicateWindowMinutes { get; set; } = 5;

    /// <summary>How many times we try to send before the notification becomes Failed.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Wait before the 2nd attempt. It doubles each time: 30s, 60s, 120s ...</summary>
    public int RetryBaseDelaySeconds { get; set; } = 30;

    /// <summary>A notification stuck in Processing longer than this (app crashed) is picked up again.</summary>
    public int ProcessingTimeoutMinutes { get; set; } = 5;

    /// <summary>Maximum notifications handled in one processing run.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>When true, a background worker processes pending notifications automatically.</summary>
    public bool WorkerEnabled { get; set; } = false;

    public int WorkerIntervalSeconds { get; set; } = 10;

    public TimeSpan DuplicateWindow => TimeSpan.FromMinutes(DuplicateWindowMinutes);
    public TimeSpan ProcessingTimeout => TimeSpan.FromMinutes(ProcessingTimeoutMinutes);

    public TimeSpan RetryDelay(int attemptNumber) =>
        TimeSpan.FromSeconds(RetryBaseDelaySeconds * Math.Pow(2, Math.Max(0, attemptNumber - 1)));
}

public class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Empty = authentication is off. When set, every API call needs the header X-Api-Key.</summary>
    public string ApiKey { get; set; } = "";
}

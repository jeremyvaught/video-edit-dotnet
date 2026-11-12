namespace VideoEdit.Services;

public sealed class VideoEditException : Exception
{
    public VideoEditException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }

    public static VideoEditException StaleWrite(int expected, int actual) =>
        new("stale_write", $"Project revision {expected} is stale; current revision is {actual}.");

    public static VideoEditException Validation(string message) =>
        new("validation", message);

    public static VideoEditException NotFound(string message) =>
        new("not_found", message);

    public static VideoEditException Conflict(string message) =>
        new("conflict", message);

    public static VideoEditException ExportBlocked(string message) =>
        new("export_blocked", message);
}

namespace JavaSelect.App.Services;

internal readonly record struct ForwardedNotification(string Action, string? CandidateId);

internal static class NotificationCommand
{
    internal static ForwardedNotification? FromArguments(IReadOnlyList<string> arguments)
    {
        var indices = Enumerable.Range(0, arguments.Count).Where(index => arguments[index] == "--notification-action").ToArray();
        if (indices.Length == 0) return null;
        if (indices.Length != 1 || indices[0] + 1 >= arguments.Count) throw new ArgumentException("Invalid notification action.");
        var action = arguments[indices[0] + 1];
        var id = action == "open" ? null : indices[0] + 2 < arguments.Count ? arguments[indices[0] + 2] : null;
        return Validate(action, id) ?? throw new ArgumentException("Invalid notification action.");
    }

    internal static string Encode(ForwardedNotification action) => $"notification|{action.Action}|{action.CandidateId}";

    internal static ForwardedNotification? Decode(string message)
    {
        var fields = message.Split('|');
        return fields.Length == 3 && fields[0] == "notification" ? Validate(fields[1], fields[2].Length == 0 ? null : fields[2]) : null;
    }

    private static ForwardedNotification? Validate(string action, string? id)
    {
        if (action == "open") return id is null ? new(action, null) : null;
        if (action is not ("add" or "ignore") || string.IsNullOrEmpty(id) || id.Length > 128 || !id.All(char.IsAsciiLetterOrDigit)) return null;
        return new(action, id);
    }
}

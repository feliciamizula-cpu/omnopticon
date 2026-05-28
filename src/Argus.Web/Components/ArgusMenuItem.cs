namespace Argus.Web.Components;

public record ArgusMenuItem(string Label, Func<Task> Action);

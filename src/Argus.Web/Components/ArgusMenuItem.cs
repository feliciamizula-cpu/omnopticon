namespace Argus.Web.Components;

public record ArgusMenuItem
{
    public string Label { get; init; } = "";
    public Func<Task>? Action { get; init; }
    public bool IsSeparator { get; init; }
    public bool IsDisabled { get; init; }

    public ArgusMenuItem() { }

    public ArgusMenuItem(string label, Func<Task>? action = null, bool isDisabled = false)
    {
        Label = label;
        Action = action;
        IsSeparator = false;
        IsDisabled = isDisabled;
    }

    public static ArgusMenuItem Separator() => new() { IsSeparator = true };
}
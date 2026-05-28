using Microsoft.AspNetCore.Components;

namespace Argus.Web.Components;

public sealed class ArgusGridColumnDef
{
    public required string Key { get; init; }
    public required string Header { get; init; }
    public Func<object, string?>? Value { get; init; }
    public RenderFragment<object>? CellTemplate { get; init; }
    public string? Width { get; init; }
    public bool Sortable { get; init; } = true;
    public bool IsPrimary { get; init; }
}
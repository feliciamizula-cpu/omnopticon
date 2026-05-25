using Microsoft.AspNetCore.Components;

namespace Argus.Web.Components;

public enum GridAlign { Left, Center, Right }

public sealed class GridColumn<TItem>
{
    public required string Header { get; init; }
    public Func<TItem, string?>? Value { get; init; }
    public RenderFragment<TItem>? CellTemplate { get; init; }
    public string? Width { get; init; }
    public bool Sortable { get; init; } = true;
    public bool Searchable { get; init; } = true;
    public bool Primary { get; init; }
    public bool Resizable { get; init; }
    public GridAlign Align { get; init; } = GridAlign.Left;
}

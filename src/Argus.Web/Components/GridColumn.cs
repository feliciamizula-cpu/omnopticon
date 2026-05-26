using Microsoft.AspNetCore.Components;

namespace Argus.Web.Components;

/// <summary>Defines one column of an <see cref="ArgusGrid{TItem}"/>.</summary>
public sealed class GridColumn<TItem>
{
    /// <summary>Column header label and internal key.</summary>
    public required string Header { get; init; }

    /// <summary>Extracts a string value used for sorting, searching, and filtering. Null = display-only.</summary>
    public Func<TItem, string?>? Value { get; init; }

    /// <summary>Custom cell render template. When set, overrides plain-text Value rendering.</summary>
    public RenderFragment<TItem>? Cell { get; init; }

    /// <summary>CSS column width (e.g. "120px", "minmax(0,1fr)"). Null = auto-fill.</summary>
    public string? Width { get; init; }

    /// <summary>Whether this column is sortable (requires Value).</summary>
    public bool Sortable { get; init; } = true;

    /// <summary>Whether this column participates in global search and per-column filter (requires Value).</summary>
    public bool Searchable { get; init; } = true;

    /// <summary>Renders with foreground color and sans-serif font — use for the main description column.</summary>
    public bool Primary { get; init; }
}

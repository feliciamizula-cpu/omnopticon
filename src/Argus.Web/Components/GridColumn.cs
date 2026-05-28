namespace Argus.Web.Components;

public class GridColumn<T>
{
    public required string Header { get; set; }
    public Func<T, string>? Value { get; set; }
    public bool Primary { get; set; }
    public bool Sortable { get; set; } = true;
}

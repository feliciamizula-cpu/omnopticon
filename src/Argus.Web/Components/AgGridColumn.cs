namespace Argus.Web.Components;

public class GridColumn<T>
{
    public required string Field { get; set; }
    public string Header { get; set; } = "";
    public Func<T, string>? Value { get; set; }
    public bool Primary { get; set; }
    public string? Width { get; set; }
    public bool Sortable { get; set; } = true;
    public bool Filter { get; set; }

    public AgGridColumn ToAgGridColumn()
    {
        return new AgGridColumn
        {
            Field = Field,
            Header = Header,
            Width = Width,
            Sortable = Sortable,
            Filter = Filter,
            ValueFormatter = Value != null ? (obj => Value((T)obj)) : null
        };
    }
}

public class AgGridColumn
{
    public required string Field { get; set; }
    public string Header { get; set; } = "";
    public string? Width { get; set; }
    public int? Flex { get; set; }
    public bool Sortable { get; set; } = true;
    public bool Filter { get; set; } = false;
    public string? CellClass { get; set; }
    public Func<object, string>? ValueFormatter { get; set; }
    public bool Hide { get; set; } = false;

    public object ToDict()
    {
        var dict = new Dictionary<string, object>
        {
            ["field"] = Field,
            ["headerName"] = string.IsNullOrEmpty(Header) ? Field : Header,
            ["sortable"] = Sortable,
            ["filter"] = Filter,
            ["hide"] = Hide
        };

        if (!string.IsNullOrEmpty(Width))
        {
            dict["width"] = Width;
        }

        if (Flex.HasValue)
        {
            dict["flex"] = Flex.Value;
        }

        if (!string.IsNullOrEmpty(CellClass))
        {
            dict["cellClass"] = CellClass;
        }

        if (ValueFormatter != null)
        {
            dict["valueFormatter"] = ValueFormatter;
        }

        return dict;
    }
}

public class AgGridContextMenuEvent
{
    public object Data { get; set; } = null!;
    public int MouseX { get; set; }
    public int MouseY { get; set; }
}
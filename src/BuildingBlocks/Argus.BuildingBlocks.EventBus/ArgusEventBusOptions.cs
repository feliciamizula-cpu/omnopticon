namespace Argus.BuildingBlocks.EventBus;

public sealed class ArgusEventBusOptions
{
    public Uri RealtimeServiceBaseAddress { get; set; } = new("http://realtime-service");
    public string SourceService { get; set; } = AppDomain.CurrentDomain.FriendlyName;
    public string ExchangeName { get; set; } = "argus.integration.events";
}

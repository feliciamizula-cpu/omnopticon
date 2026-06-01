using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Argus.Contracts.Workers;

namespace Argus.Web.Services;

public sealed class WorkersApiClient(HttpClient http)
{
    // The realtime API serializes enums (e.g. WorkerTypeHealthStatus) as strings via
    // JsonStringEnumConverter, so the client must deserialize the same way (and be case-insensitive).
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<WorkersPageSummaryDto> GetSummaryAsync(CancellationToken ct = default)
    {
        return await http.GetFromJsonAsync<WorkersPageSummaryDto>("/worker-types/summary", JsonOptions, ct)
               ?? throw new InvalidOperationException("Workers summary response was empty.");
    }

    public async Task<WorkerTypeSummaryDto> GetWorkerTypeAsync(string workerType, CancellationToken ct = default)
    {
        return await http.GetFromJsonAsync<WorkerTypeSummaryDto>
               ($"/worker-types/{Uri.EscapeDataString(workerType)}", JsonOptions, ct)
               ?? throw new InvalidOperationException("Worker type response was empty.");
    }

    public async Task<IReadOnlyCollection<WorkerInstanceDto>> GetInstancesAsync(string workerType, CancellationToken ct = default)
    {
        return await http.GetFromJsonAsync<IReadOnlyCollection<WorkerInstanceDto>>
               ($"/worker-types/{Uri.EscapeDataString(workerType)}/instances", JsonOptions, ct)
               ?? [];
    }

    public async Task<WorkerScaleCommandDto> ScaleUpAsync(string workerType, CancellationToken ct = default)
    {
        var response = await http.PostAsync($"/worker-types/{Uri.EscapeDataString(workerType)}/scale-up", null, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<WorkerScaleCommandDto>(JsonOptions, ct)
               ?? throw new InvalidOperationException("Scale-up response was empty.");
    }

    public async Task<WorkerScaleCommandDto> ScaleDownAsync(string workerType, CancellationToken ct = default)
    {
        var response = await http.PostAsync($"/worker-types/{Uri.EscapeDataString(workerType)}/scale-down", null, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<WorkerScaleCommandDto>(JsonOptions, ct)
               ?? throw new InvalidOperationException("Scale-down response was empty.");
    }

    public async Task<WorkerScaleCommandDto> ScaleAsync(string workerType, ScaleWorkerTypeRequest request, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync($"/worker-types/{Uri.EscapeDataString(workerType)}/scale", request, JsonOptions, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<WorkerScaleCommandDto>(JsonOptions, ct)
               ?? throw new InvalidOperationException("Scale response was empty.");
    }

    public async Task<WorkerTypeScaleSettingsDto> UpdateSettingsAsync(
        string workerType,
        UpdateWorkerScaleSettingsRequest request,
        CancellationToken ct = default)
    {
        var response = await http.PutAsJsonAsync($"/worker-types/{Uri.EscapeDataString(workerType)}/scale-settings", request, JsonOptions, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<WorkerTypeScaleSettingsDto>(JsonOptions, ct)
               ?? throw new InvalidOperationException("Scale settings response was empty.");
    }

    public async Task<WorkerScaleCommandDto> PauseAsync(string workerType, CancellationToken ct = default)
    {
        var response = await http.PostAsync($"/worker-types/{Uri.EscapeDataString(workerType)}/pause", null, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<WorkerScaleCommandDto>(JsonOptions, ct)
               ?? throw new InvalidOperationException("Pause response was empty.");
    }

    public async Task<WorkerScaleCommandDto> ResumeAsync(string workerType, CancellationToken ct = default)
    {
        var response = await http.PostAsync($"/worker-types/{Uri.EscapeDataString(workerType)}/resume", null, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<WorkerScaleCommandDto>(JsonOptions, ct)
               ?? throw new InvalidOperationException("Resume response was empty.");
    }
}

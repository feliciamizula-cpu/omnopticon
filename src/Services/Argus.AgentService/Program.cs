using Argus.AgentService;
using Argus.AgentService.Agents;
using Argus.AgentService.Data;
using Argus.AgentService.ProviderUsage;
using Argus.AgentService.ProviderUsage.Adapters;
using Argus.AgentService.Stores;
using Argus.BuildingBlocks.EventBus;
using Argus.Contracts.Agents;
using Argus.ServiceDefaults;
using Dapper;
using Microsoft.EntityFrameworkCore;

Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

var builder = WebApplication.CreateBuilder(args);

builder.AddBasicServiceDefaults();

if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("argusdb")))
{
    builder.Services.AddDbContext<AgentDbContext>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("argusdb")));
    builder.Services.AddArgusEfCoreOutbox<AgentDbContext>();
    builder.Services.AddArgusInboxConsumer<AgentDbContext>();
    builder.Services.AddHealthChecks()
        .AddNpgSql(builder.Configuration.GetConnectionString("argusdb")!, name: "argusdb", tags: ["db", "sql", "postgres"]);
    builder.Services.AddScoped<IAgentStore, EfAgentStore>();
    builder.Services.AddScoped<ProviderUsageService>();
    builder.Services.AddSingleton<IProviderUsageAdapter, OpenAiUsageAdapter>();
    builder.Services.AddSingleton<IProviderUsageAdapter, AnthropicUsageAdapter>();
    builder.Services.AddSingleton<IProviderUsageAdapter, OpenRouterUsageAdapter>();
    builder.Services.AddSingleton<IProviderUsageAdapter, DeepSeekUsageAdapter>();
    builder.Services.AddSingleton<IProviderUsageAdapter, GeminiUsageAdapter>();
    builder.Services.AddSingleton<IProviderUsageAdapter, QwenDashScopeUsageAdapter>();
    builder.Services.AddSingleton<IProviderUsageAdapter, CodexCliUsageAdapter>();
    builder.Services.AddSingleton<IProviderUsageAdapter, ClaudeCodeUsageAdapter>();
    builder.Services.AddSingleton<IProviderUsageAdapter, ManualUsageAdapter>();
    builder.Services.AddHostedService<ProviderUsageMonitor>();
    builder.Services.AddScoped<AgentSelectionService>();
    builder.Services.AddScoped<TaskExecutionService>();
    builder.Services.AddHostedService<TaskSchedulerService>();
}
else
{
    builder.Services.AddSingleton<IAgentStore, InMemoryAgentStore>();
    builder.Services.AddScoped<AgentSelectionService>();
    builder.Services.AddScoped<TaskExecutionService>();
    builder.Services.AddHostedService<TaskSchedulerService>();
}

builder.AddArgusIntegrationEvents(options => options.SourceService = "Argus.AgentService");
builder.Services.AddHttpClient();
builder.Services.AddProblemDetails();

var app = builder.Build();

await app.InitializeAgentStoreAsync();
app.MapDefaultEndpoints();

AgentEndpoints.MapRoutes(app);
ProviderUsageEndpoints.MapRoutes(app);

app.Run();

internal static class AgentStoreInitialization
{
    public static async Task InitializeAgentStoreAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetService<IAgentStore>();
        if (store is not null)
        {
            await store.InitializeAsync();
        }
    }
}

internal static class AgentEndpoints
{
    public static void MapRoutes(WebApplication app)
    {
        // Agent endpoints
        app.MapGet("/agents", ListAgents);
        app.MapPost("/agents", CreateAgent);
        app.MapGet("/agents/{agentId:guid}", GetAgent);
        app.MapPut("/agents/{agentId:guid}", UpdateAgent);
        app.MapDelete("/agents/{agentId:guid}", DeleteAgent);
        app.MapPatch("/agents/{agentId:guid}/pause", PauseAgent);
        app.MapPatch("/agents/{agentId:guid}/resume", ResumeAgent);
        app.MapPost("/agents/{agentId:guid}/assign/{taskId}", AssignTask);

        // Task endpoints
        app.MapGet("/agent-tasks", ListTasks);
        app.MapPost("/agent-tasks", CreateTask);
        app.MapGet("/agent-tasks/{taskId}", GetTask);
        app.MapPut("/agent-tasks/{taskId}", UpdateTask);
        app.MapDelete("/agent-tasks/{taskId}", DeleteTask);
        app.MapPost("/agent-tasks/{taskId}/run", RunTask);
        app.MapPost("/agent-events/{triggerEvent}/dispatch", DispatchTrigger);

        // Agent-produced artifacts
        app.MapGet("/code-reviews", ListCodeReviews);
        app.MapPost("/code-reviews", CreateCodeReview);
        app.MapGet("/system-reports", ListSystemReports);
        app.MapPost("/system-reports", CreateSystemReport);

        // Chat endpoints
        app.MapGet("/agent-chat/history", GetChatHistory);
        app.MapPost("/agent-chat", SendChat);
    }

    // Agent handlers
    private static async Task<IResult> ListAgents(IAgentStore store, CancellationToken ct)
    {
        var agents = await store.ListAgentsAsync(ct);
        return Results.Ok(new { items = agents, count = agents.Count });
    }

    private static async Task<IResult> CreateAgent(CreateAgentRequest request, IAgentStore store, CancellationToken ct)
    {
        var agent = await store.CreateAgentAsync(request, ct);
        return Results.Created($"/agents/{agent.AgentId}", agent);
    }

    private static async Task<IResult> GetAgent(Guid agentId, IAgentStore store, CancellationToken ct)
    {
        var agent = await store.GetAgentAsync(agentId, ct);
        return agent is not null ? Results.Ok(agent) : Results.NotFound();
    }

    private static async Task<IResult> UpdateAgent(Guid agentId, UpdateAgentRequest request, IAgentStore store, CancellationToken ct)
    {
        var agent = await store.UpdateAgentAsync(agentId, request, ct);
        return agent is not null ? Results.Ok(agent) : Results.NotFound();
    }

    private static async Task<IResult> DeleteAgent(Guid agentId, IAgentStore store, CancellationToken ct)
    {
        var deleted = await store.DeleteAgentAsync(agentId, ct);
        return deleted ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> PauseAgent(Guid agentId, IAgentStore store, CancellationToken ct)
    {
        var agent = await store.UpdateAgentAsync(agentId, new UpdateAgentRequest(Status: "paused"), ct);
        return agent is not null ? Results.Ok(agent) : Results.NotFound();
    }

    private static async Task<IResult> ResumeAgent(Guid agentId, IAgentStore store, CancellationToken ct)
    {
        var agent = await store.UpdateAgentAsync(agentId, new UpdateAgentRequest(Status: "active"), ct);
        return agent is not null ? Results.Ok(agent) : Results.NotFound();
    }

    private static async Task<IResult> AssignTask(Guid agentId, string taskId, IAgentStore store, CancellationToken ct)
    {
        var task = await store.UpdateTaskAsync(taskId, new UpdateAgentTaskRequest(AssignedTo: agentId.ToString()), ct);
        return task is not null ? Results.Ok(task) : Results.NotFound();
    }

    // Task handlers
    private static async Task<IResult> ListTasks(string? status, string? priority, IAgentStore store, CancellationToken ct)
    {
        var tasks = await store.ListTasksAsync(status, priority, ct);
        return Results.Ok(new { items = tasks, count = tasks.Count });
    }

    private static async Task<IResult> CreateTask(CreateAgentTaskRequest request, IAgentStore store, CancellationToken ct)
    {
        var task = await store.CreateTaskAsync(request, ct);
        return Results.Created($"/agent-tasks/{task.TaskId}", task);
    }

    private static async Task<IResult> GetTask(string taskId, IAgentStore store, CancellationToken ct)
    {
        var task = await store.GetTaskAsync(taskId, ct);
        return task is not null ? Results.Ok(task) : Results.NotFound();
    }

    private static async Task<IResult> UpdateTask(string taskId, UpdateAgentTaskRequest request, IAgentStore store, CancellationToken ct)
    {
        var task = await store.UpdateTaskAsync(taskId, request, ct);
        return task is not null ? Results.Ok(task) : Results.NotFound();
    }

    private static async Task<IResult> DeleteTask(string taskId, IAgentStore store, CancellationToken ct)
    {
        var deleted = await store.DeleteTaskAsync(taskId, ct);
        return deleted ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> RunTask(string taskId, TaskExecutionService executor, CancellationToken ct)
    {
        var task = await executor.ExecuteAsync(taskId, ct);
        return task is not null ? Results.Ok(task) : Results.NotFound();
    }

    private static async Task<IResult> DispatchTrigger(
        string triggerEvent,
        IAgentStore store,
        TaskExecutionService executor,
        CancellationToken ct)
    {
        var tasks = await store.ListTasksAsync(status: "scheduled", cancellationToken: ct);
        var matching = tasks
            .Where(t => string.Equals(t.TriggerEvent, triggerEvent, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var task in matching)
            await executor.ExecuteAsync(task.TaskId, ct);

        return Results.Ok(new { triggerEvent, dispatched = matching.Count });
    }

    private static async Task<IResult> ListCodeReviews(IServiceProvider sp, int? take, CancellationToken ct)
    {
        var db = sp.GetService<AgentDbContext>();
        if (db is null)
            return Results.Ok(new { items = Array.Empty<CodeReviewDto>(), count = 0 });

        var limit = Math.Clamp(take ?? 100, 1, 500);
        var reviews = await db.CodeReviews
            .AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .Take(limit)
            .Select(r => r.ToDto())
            .ToListAsync(ct);

        return Results.Ok(new { items = reviews, count = reviews.Count });
    }

    private static async Task<IResult> CreateCodeReview(
        CreateCodeReviewRequest request,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var db = sp.GetService<AgentDbContext>();
        if (db is null)
            return Results.StatusCode(StatusCodes.Status501NotImplemented);

        var record = new CodeReviewRecord
        {
            ReviewId = Guid.NewGuid(),
            SourceTaskId = request.SourceTaskId,
            AgentId = request.AgentId,
            ReviewContent = request.ReviewContent,
            CommitRef = request.CommitRef,
            Status = "completed",
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.CodeReviews.Add(record);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/code-reviews/{record.ReviewId}", record.ToDto());
    }

    private static async Task<IResult> ListSystemReports(IServiceProvider sp, int? take, CancellationToken ct)
    {
        var db = sp.GetService<AgentDbContext>();
        if (db is null)
            return Results.Ok(new { items = Array.Empty<SystemReportDto>(), count = 0 });

        var limit = Math.Clamp(take ?? 100, 1, 500);
        var reports = await db.SystemReports
            .AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .Take(limit)
            .Select(r => r.ToDto())
            .ToListAsync(ct);

        return Results.Ok(new { items = reports, count = reports.Count });
    }

    private static async Task<IResult> CreateSystemReport(
        CreateSystemReportRequest request,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var db = sp.GetService<AgentDbContext>();
        if (db is null)
            return Results.StatusCode(StatusCodes.Status501NotImplemented);

        var record = new SystemReportRecord
        {
            ReportId = Guid.NewGuid(),
            AgentId = request.AgentId,
            ReportContent = request.ReportContent,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.SystemReports.Add(record);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/system-reports/{record.ReportId}", record.ToDto());
    }

    // Chat handlers
    private static async Task<IResult> GetChatHistory(IAgentStore store, CancellationToken ct)
    {
        var history = await store.GetChatHistoryAsync(50, ct);
        return Results.Ok(new { items = history, count = history.Count });
    }

    private static async Task<IResult> SendChat(SendChatRequest request, IAgentStore store, IServiceProvider sp, CancellationToken ct)
    {
        var usageService = sp.GetService<ProviderUsageService>();

        // Persist user message
        await store.SaveChatMessageAsync("user", request.Message, ct);

        // Get context
        var agents = await store.ListAgentsAsync(ct);
        var tasks = await store.ListTasksAsync(cancellationToken: ct);

        // Build prompt with current state
        var systemPrompt = BuildSystemPrompt(agents, tasks);
        var contextPrompt = $"{systemPrompt}\n\nCurrent request: {request.Message}";

        if (usageService is not null)
            await usageService.RecordInvocationStartedAsync(request.Tool, request.Model, null, null, ct);

        // Invoke CLI tool
        string aiReply;
        try
        {
            aiReply = await InvokeCliTool(request.Tool, request.Model, contextPrompt, ct);
            if (usageService is not null)
                await usageService.RecordInvocationCompletedAsync(request.Tool, request.Model, 0, aiReply, null, ct);
        }
        catch (Exception ex)
        {
            if (usageService is not null)
                await usageService.RecordInvocationCompletedAsync(request.Tool, request.Model, 1, null, ex.Message, ct);
            return Results.Problem($"CLI tool '{request.Tool}' error: {ex.Message}", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // Parse actions from response
        var actions = new List<AgentAction>();
        var displayReply = await ExtractAndExecuteActions(aiReply, actions, store, ct);

        // Persist assistant message
        await store.SaveChatMessageAsync("assistant", displayReply, ct);

        // Return response with updated history
        var updatedHistory = await store.GetChatHistoryAsync(50, ct);
        var response = new ChatResponse(displayReply, updatedHistory.ToArray(), actions.ToArray());

        return Results.Ok(response);
    }

    private static string BuildSystemPrompt(IReadOnlyList<AgentDto> agents, IReadOnlyList<AgentTaskDto> tasks)
    {
        return $@"You are the Argus AI agent coordinator managing a role-based AI development team.

Current Agents ({agents.Count}):
{string.Join("\n", agents.Select(a => $"- {a.Name} ({a.Role}): {string.Join(", ", a.Responsibilities)}"))}

Pending Tasks ({tasks.Count(t => t.Status == "pending")}):
{string.Join("\n", tasks.Where(t => t.Status == "pending").Take(5).Select(t => $"- Task {t.TaskId}: {t.Description}"))}

You can create new agents or tasks by outputting ACTION lines in your response:
ACTION:CREATE_AGENT name=""AgentName"" role=""development|devops"" responsibilities=""item1,item2,item3""
ACTION:CREATE_TASK description=""Task description"" priority=""low|normal|high"" targetRole=""junior_developer|developer|senior_developer|devops|system_architect"" assignedto=""agent-id-optional""
ACTION:ASSIGN_TASK taskid=""001"" agentid=""agent-id""
ACTION:UPDATE_AGENT_STATUS agentid=""agent-id"" status=""active|paused|inactive""

Output ACTION lines on separate lines before or after your reply. Only output action lines that are valid.
Respond helpfully to user requests. Be concise.";
    }

    private static async Task<string> InvokeCliTool(string tool, string model, string prompt, CancellationToken ct)
    {
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        AddCliArguments(process.StartInfo, tool, model, prompt);
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(ct);

        if (!await System.Threading.Tasks.Task.Run(() => process.WaitForExit(60000), ct))
        {
            process.Kill();
            throw new TimeoutException($"CLI tool '{tool}' timed out after 60s");
        }

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"CLI tool '{tool}' exited with code {process.ExitCode}: {error}");
        }

        return output;
    }

    private static void AddCliArguments(System.Diagnostics.ProcessStartInfo psi, string tool, string model, string prompt)
    {
        switch (tool.ToLowerInvariant())
        {
            case "claude":
                psi.FileName = "claude";
                psi.ArgumentList.Add("--model");
                psi.ArgumentList.Add(model);
                psi.ArgumentList.Add("-p");
                psi.ArgumentList.Add(prompt);
                break;
            case "opencode":
                psi.FileName = "opencode";
                psi.ArgumentList.Add("run");
                psi.ArgumentList.Add("--model");
                psi.ArgumentList.Add(model);
                psi.ArgumentList.Add("--prompt-text");
                psi.ArgumentList.Add(prompt);
                break;
            case "codex":
                psi.FileName = "codex";
                psi.ArgumentList.Add("--model");
                psi.ArgumentList.Add(model);
                psi.ArgumentList.Add(prompt);
                break;
            case "openai":
                psi.FileName = "openai";
                psi.ArgumentList.Add("api");
                psi.ArgumentList.Add("chat.completions.create");
                psi.ArgumentList.Add("-m");
                psi.ArgumentList.Add(model);
                psi.ArgumentList.Add("-g");
                psi.ArgumentList.Add("user");
                psi.ArgumentList.Add(prompt);
                break;
            case "gemini":
                psi.FileName = "gemini";
                psi.ArgumentList.Add("--model");
                psi.ArgumentList.Add(model);
                psi.ArgumentList.Add("--prompt");
                psi.ArgumentList.Add(prompt);
                break;
            default:
                throw new ArgumentException($"Unknown tool: {tool}");
        }
    }

    private static async Task<string> ExtractAndExecuteActions(string response, List<AgentAction> actions, IAgentStore store, CancellationToken ct)
    {
        var lines = response.Split('\n');
        var replyLines = new List<string>();
        var actionLines = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("ACTION:", StringComparison.OrdinalIgnoreCase))
                actionLines.Add(line);
            else
                replyLines.Add(line);
        }

        // Execute each action
        foreach (var actionLine in actionLines)
        {
            try
            {
                await ExecuteAction(actionLine, actions, store, ct);
            }
            catch
            {
                // Skip invalid actions
            }
        }

        return string.Join("\n", replyLines).Trim();
    }

    private static async Task ExecuteAction(string actionLine, List<AgentAction> actions, IAgentStore store, CancellationToken ct)
    {
        var action = actionLine[7..].Trim(); // Skip "ACTION:"

        if (action.StartsWith("CREATE_AGENT", StringComparison.OrdinalIgnoreCase))
        {
            var name = ExtractParam(action, "name");
            var role = ExtractParam(action, "role");
            var respsStr = ExtractParam(action, "responsibilities");
            var resps = respsStr?.Split(',').Select(r => r.Trim()).ToArray() ?? [];

            var agent = await store.CreateAgentAsync(
                new CreateAgentRequest(name!, role!, resps, "opencode", "claude-sonnet-4-6"), ct);
            actions.Add(new AgentAction("create_agent", $"Created agent '{agent.Name}'"));
        }
        else if (action.StartsWith("CREATE_TASK", StringComparison.OrdinalIgnoreCase))
        {
            var desc = ExtractParam(action, "description");
            var priority = ExtractParam(action, "priority") ?? "normal";
            var assignedTo = ExtractParam(action, "assignedto");
            var targetRole = ExtractParam(action, "targetRole");

            var task = await store.CreateTaskAsync(
                new CreateAgentTaskRequest(desc!, priority, assignedTo, targetRole), ct);
            actions.Add(new AgentAction("create_task", $"Created task #{task.TaskId}: {desc}"));
        }
        else if (action.StartsWith("ASSIGN_TASK", StringComparison.OrdinalIgnoreCase))
        {
            var taskId = ExtractParam(action, "taskid");
            var agentId = ExtractParam(action, "agentid");

            await store.UpdateTaskAsync(taskId!, new UpdateAgentTaskRequest(AssignedTo: agentId), ct);
            actions.Add(new AgentAction("assign_task", $"Assigned task {taskId} to {agentId}"));
        }
        else if (action.StartsWith("UPDATE_AGENT_STATUS", StringComparison.OrdinalIgnoreCase))
        {
            var agentId = ExtractParam(action, "agentid");
            var status = ExtractParam(action, "status");

            if (Guid.TryParse(agentId, out var guid))
            {
                await store.UpdateAgentAsync(guid, new UpdateAgentRequest(Status: status), ct);
                actions.Add(new AgentAction("update_agent_status", $"Updated agent status to {status}"));
            }
        }
    }

    private static string? ExtractParam(string action, string paramName)
    {
        var pattern = $@"{paramName}=""([^""]*)""";
        var match = System.Text.RegularExpressions.Regex.Match(action, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
}

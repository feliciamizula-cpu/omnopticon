#!/usr/bin/env pwsh
param(
    [Parameter(Mandatory=$true)]
    [ValidateSet('list', 'add', 'take', 'done', 'status', 'init')]
    [string]$Action,

    [Parameter(Mandatory=$false)]
    [string]$TaskId,

    [Parameter(Mandatory=$false)]
    [string]$Description,

    [Parameter(Mandatory=$false)]
    [string]$Priority = "medium",

    [Parameter(Mandatory=$false)]
    [string]$AgentId = "agent-1"
)

$StateFile = Join-Path $PSScriptRoot ".agent-tasks.json"
$LockFile = Join-Path $PSScriptRoot ".agent-tasks.lock"

function Read-StateWithLock {
    $lock = $null
    try {
        $lock = [System.IO.File]::Open($LockFile, 'Create', 'ReadWrite', 'None')
        if (Test-Path $StateFile) {
            $content = Get-Content $StateFile -Raw
            return $content | ConvertFrom-Json
        }
        return $null
    }
    finally {
        if ($lock) { $lock.Close(); Remove-Item $LockFile -ErrorAction SilentlyContinue }
    }
}

function Write-StateWithLock($state) {
    $lock = $null
    try {
        $lock = [System.IO.File]::Open($LockFile, 'Create', 'ReadWrite', 'None')
        $state | ConvertTo-Json -Depth 10 | Set-Content $StateFile

        Set-Location $PSScriptRoot
        git add .agent-tasks.json
        $status = git status --porcelain
        if ($status) {
            git commit -m "chore: update agent coordination state [skip ci]" 2>$null
            git push 2>$null
        }
    }
    finally {
        if ($lock) { $lock.Close(); Remove-Item $LockFile -ErrorAction SilentlyContinue }
    }
}

switch ($Action) {
    'init' {
        $lock = $null
        try {
            $lock = [System.IO.File]::Open($LockFile, 'Create', 'ReadWrite', 'None')
            $state = @{
                tasks = @()
                agents = @(
                    @{ id = "agent-1"; name = "Agent 1"; status = "active"; currentTask = $null; lastUpdated = (Get-Date).ToString("o") },
                    @{ id = "agent-2"; name = "Agent 2"; status = "inactive"; currentTask = $null; lastUpdated = "" }
                )
                version = "1.0"
            }
            $state | ConvertTo-Json -Depth 10 | Set-Content $StateFile

            Set-Location $PSScriptRoot
            git add .agent-tasks.json
            git commit -m "chore: update agent coordination state [skip ci]" 2>$null
            git push 2>$null

            Write-Host "Initialized coordination state with 2 agents"
        }
        finally {
            if ($lock) { $lock.Close(); Remove-Item $LockFile -ErrorAction SilentlyContinue }
        }
    }

    'list' {
        if (Test-Path $StateFile) {
            $content = Get-Content $StateFile -Raw
            $state = $content | ConvertFrom-Json
            Write-Host "=== Tasks ===" -ForegroundColor Cyan
            foreach ($task in $state.tasks) {
                $color = switch ($task.status) {
                    "pending" { "Yellow" }
                    "in_progress" { "Green" }
                    "completed" { "Gray" }
                    default { "White" }
                }
                Write-Host "[$($task.id)] $($task.status.ToUpper().PadRight(12)) $($task.priority.PadRight(8)) $($task.description)" -ForegroundColor $color
            }
            Write-Host "`n=== Agents ===" -ForegroundColor Cyan
            foreach ($agent in $state.agents) {
                $taskInfo = if ($agent.currentTask) { "working on: $($agent.currentTask)" } else { "idle" }
                Write-Host "$($agent.name) ($($agent.id)): $($agent.status) - $taskInfo" -ForegroundColor White
            }
        } else {
            Write-Host "No state file. Run: agent-coord.ps1 init" -ForegroundColor Red
        }
    }

    'add' {
        if (-not $Description) {
            Write-Host "Error: -Description required for 'add'" -ForegroundColor Red
            exit 1
        }
        $state = Read-StateWithLock
        if (-not $state) {
            Write-Host "No state file. Run: agent-coord.ps1 init" -ForegroundColor Red
            exit 1
        }
        $newId = ($state.tasks.Count + 1).ToString("D3")
        $task = @{
            id = $newId
            description = $Description
            priority = $Priority
            status = "pending"
            assignedTo = $null
            createdAt = (Get-Date).ToString("o")
        }
        $state.tasks += $task
        Write-StateWithLock $state
        Write-Host "Added task [$newId]: $Description" -ForegroundColor Green
    }

    'take' {
        if (-not $TaskId) {
            Write-Host "Error: -TaskId required for 'take'" -ForegroundColor Red
            exit 1
        }
        $state = Read-StateWithLock
        if (-not $state) {
            Write-Host "No state file. Run: agent-coord.ps1 init" -ForegroundColor Red
            exit 1
        }
        $task = $state.tasks | Where-Object { $_.id -eq $TaskId } | Select-Object -First 1
        if (-not $task) {
            Write-Host "Task [$TaskId] not found" -ForegroundColor Red
            exit 1
        }
        if ($task.status -eq "in_progress") {
            Write-Host "Task [$TaskId] is already in progress" -ForegroundColor Yellow
            exit 1
        }
        $task.status = "in_progress"
        $task.assignedTo = $AgentId
        $agent = $state.agents | Where-Object { $_.id -eq $AgentId } | Select-Object -First 1
        if ($agent) {
            $agent.currentTask = $TaskId
            $agent.lastUpdated = (Get-Date).ToString("o")
        }
        Write-StateWithLock $state
        Write-Host "Agent $AgentId took task [$TaskId]" -ForegroundColor Green
    }

    'done' {
        if (-not $TaskId) {
            Write-Host "Error: -TaskId required for 'done'" -ForegroundColor Red
            exit 1
        }
        $state = Read-StateWithLock
        if (-not $state) {
            Write-Host "No state file. Run: agent-coord.ps1 init" -ForegroundColor Red
            exit 1
        }
        $task = $state.tasks | Where-Object { $_.id -eq $TaskId } | Select-Object -First 1
        if (-not $task) {
            Write-Host "Task [$TaskId] not found" -ForegroundColor Red
            exit 1
        }
        $task.status = "completed"
        $task.completedAt = (Get-Date).ToString("o")
        $agent = $state.agents | Where-Object { $_.currentTask -eq $TaskId } | Select-Object -First 1
        if ($agent) {
            $agent.currentTask = $null
            $agent.lastUpdated = (Get-Date).ToString("o")
        }
        Write-StateWithLock $state
        Write-Host "Completed task [$TaskId]" -ForegroundColor Green
    }

    'status' {
        if (Test-Path $StateFile) {
            $content = Get-Content $StateFile -Raw
            $state = $content | ConvertFrom-Json
            $pending = ($state.tasks | Where-Object { $_.status -eq "pending" }).Count
            $inProgress = ($state.tasks | Where-Object { $_.status -eq "in_progress" }).Count
            $completed = ($state.tasks | Where-Object { $_.status -eq "completed" }).Count
            Write-Host "Status: $pending pending, $inProgress in progress, $completed completed" -ForegroundColor Cyan
        } else {
            Write-Host "No state file" -ForegroundColor Red
        }
    }
}
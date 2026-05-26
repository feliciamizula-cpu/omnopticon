using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    private static readonly string LogFilePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "ai_logs.txt");
    private static readonly string ProjectRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
    private static long _lastPosition = 0;

    static async Task Main(string[] args)
    {
        Console.Clear();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                    AGENT LOG MONITOR - Omnopticon                          ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine("Watching: " + Path.GetFullPath(Path.Combine(ProjectRoot, "ai_logs.txt")));
        Console.WriteLine("Press Ctrl+C to exit...");
        Console.WriteLine();
        Console.WriteLine(new string('═', 90));
        Console.WriteLine();

        string actualLogPath = FindLogFile();
        if (string.IsNullOrEmpty(actualLogPath))
        {
            Console.WriteLine("[INIT] No ai_logs.txt found. Will create when agents start logging.");
            Console.WriteLine();
            actualLogPath = Path.Combine(ProjectRoot, "ai_logs.txt");
        }
        else
        {
            Console.WriteLine("[INIT] Found existing log file. Reading recent entries...");
            Console.WriteLine();
            _lastPosition = new FileInfo(actualLogPath).Length;
        }

        _ = Task.Run(() => WatchFile(actualLogPath));

        await Task.Delay(Timeout.Infinite);
    }

    static string FindLogFile()
    {
        string[] possiblePaths = new[]
        {
            Path.Combine(ProjectRoot, "ai_logs.txt"),
            "ai_logs.txt"
        };

        foreach (var path in possiblePaths)
        {
            string fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
                return fullPath;
        }

        return possiblePaths[0];
    }

    static async Task WatchFile(string logFilePath)
    {
        while (true)
        {
            try
            {
                if (File.Exists(logFilePath))
                {
                    var fileInfo = new FileInfo(logFilePath);
                    
                    if (fileInfo.Length < _lastPosition)
                    {
                        _lastPosition = 0;
                        Console.WriteLine("[RESET] Log file was truncated, reading from start...");
                        Console.WriteLine();
                    }

                    if (fileInfo.Length > _lastPosition)
                    {
                        using var stream = new FileStream(logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        stream.Seek(_lastPosition, SeekOrigin.Begin);
                        
                        using var reader = new StreamReader(stream);
                        string? line;
                        while ((line = await reader.ReadLineAsync()) != null)
                        {
                            if (!string.IsNullOrWhiteSpace(line))
                            {
                                DisplayLogEntry(line);
                            }
                        }
                        
                        _lastPosition = stream.Position;
                    }
                }
                else
                {
                    Console.WriteLine("[WAITING] Log file not yet created. Waiting for agent activity...");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Watch error: {ex.Message}");
            }

            await Task.Delay(2000);
        }
    }

    static void DisplayLogEntry(string line)
    {
        var (timestamp, agentId, task, status, message) = ParseLogEntry(line);

        string statusColor = status.ToUpperInvariant() switch
        {
            "STARTED" => "\x1b[92m",
            "PROGRESS" => "\x1b[94m",
            "COMPLETED" => "\x1b[92m",
            "LOCKING" => "\x1b[93m",
            "RELEASED" => "\x1b[96m",
            "BLOCKED" => "\x1b[91m",
            "CONFLICT" => "\x1b[91m",
            "ERROR" => "\x1b[91m",
            "WAITING" => "\x1b[90m",
            _ => "\x1b[97m"
        };

        string resetColor = "\x1b[0m";

        Console.WriteLine($"{timestamp} {statusColor}[{status}]{resetColor} {agentId}");
        Console.WriteLine($"  Task: {task}");
        Console.WriteLine($"  {message}");
        Console.WriteLine();
    }

    static (string timestamp, string agentId, string task, string status, string message) ParseLogEntry(string line)
    {
        string timestamp = "";
        string agentId = "";
        string task = "";
        string status = "";
        string message = line;

        var timestampMatch = Regex.Match(line, @"\[([^\]]+)\]");
        if (timestampMatch.Success)
            timestamp = timestampMatch.Groups[1].Value;

        var agentIdMatch = Regex.Match(line, @"AGENT_ID:\s*(\S+)");
        if (agentIdMatch.Success)
            agentId = agentIdMatch.Groups[1].Value;

        var taskMatch = Regex.Match(line, @"TASK:\s*(\S+)");
        if (taskMatch.Success)
            task = taskMatch.Groups[1].Value;

        var statusMatch = Regex.Match(line, @"STATUS:\s*(\S+)");
        if (statusMatch.Success)
            status = statusMatch.Groups[1].Value;

        var messageMatch = Regex.Match(line, @"MESSAGE:\s*(.+)");
        if (messageMatch.Success)
            message = messageMatch.Groups[1].Value;

        return (timestamp, agentId, task, status, message);
    }
}
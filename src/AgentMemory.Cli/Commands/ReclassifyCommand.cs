using System.Text.Json;

namespace AgentMemory.Cli.Commands;

public static class ReclassifyCommand
{
    public static async Task<int> ExecuteAsync(string[] args)
    {
        var serverUrl = "http://localhost:5098";
        long? captureId = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--server":
                case "-s":
                    if (i + 1 < args.Length) serverUrl = args[++i];
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    return 0;
                default:
                    if (long.TryParse(args[i], out var id)) captureId = id;
                    break;
            }
        }

        if (captureId is null)
        {
            Console.WriteLine("Error: a capture id is required.");
            PrintHelp();
            return 1;
        }

        Console.WriteLine($"AgentMemory — Reclassify capture {captureId}");
        Console.WriteLine(new string('-', 50));

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(35) };
            var response = await http.PostAsync($"{serverUrl}/admin/captures/{captureId}/reclassify", null);

            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Error: Server returned {(int)response.StatusCode} {response.ReasonPhrase}");
                Console.WriteLine(json);
                return 1;
            }

            var result = JsonSerializer.Deserialize<ReclassifyResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (result?.Memory is null)
            {
                Console.WriteLine($"Capture {captureId} reclassified — score did not clear the save threshold, no memory stored.");
                return 0;
            }

            Console.WriteLine($"Capture {captureId} reclassified:");
            Console.WriteLine($"  Memory id: {result.Memory.Id}");
            Console.WriteLine($"  Category:  {result.Memory.Category}");
            Console.WriteLine($"  Score:     {result.Memory.Score:F2}");
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"Error connecting to server: {ex.Message}");
            return 1;
        }

        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: dotnet run -- re-classify <capture-id> [options]");
        Console.WriteLine();
        Console.WriteLine("Re-runs classification, threshold check, and embedding for an already-stored");
        Console.WriteLine("capture. Adds a new memory row if the (re-)classification clears the");
        Console.WriteLine("configured save threshold.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --server, -s <url>    Server URL (default: http://localhost:5098)");
        Console.WriteLine("  --help, -h            Show this help");
    }

    private class ReclassifyResponse
    {
        public long CaptureId { get; set; }
        public string Status { get; set; } = string.Empty;
        public ReclassifyMemory? Memory { get; set; }
    }

    private class ReclassifyMemory
    {
        public long Id { get; set; }
        public string Category { get; set; } = string.Empty;
        public double Score { get; set; }
    }
}

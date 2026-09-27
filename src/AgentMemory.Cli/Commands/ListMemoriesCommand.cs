using System.Text.Json;

namespace AgentMemory.Cli.Commands;

public static class ListMemoriesCommand
{
    public static async Task<int> ExecuteAsync(string[] args)
    {
        // Parse optional flags
        var serverUrl = "http://localhost:5098";
        var limit = 10;
        var category = string.Empty;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--server":
                case "-s":
                    if (i + 1 < args.Length) serverUrl = args[++i];
                    break;
                case "--limit":
                case "-n":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var n)) limit = n;
                    break;
                case "--category":
                case "-c":
                    if (i + 1 < args.Length) category = args[++i];
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    return 0;
            }
        }

        // Build the URL for the memories endpoint
        // (This is a direct DB query via a simple endpoint, not an MCP tool)
        // For now, query via the server's DB directly since we don't have a public endpoint yet.
        // This is an admin CLI — it talks directly to the same DB.
        Console.WriteLine("AgentMemory — Recent Memories CLI");
        Console.WriteLine(new string('-', 50));
        Console.WriteLine($"Server: {serverUrl}");
        Console.WriteLine($"Limit: {limit}");
        if (!string.IsNullOrEmpty(category)) Console.WriteLine($"Category: {category}");
        Console.WriteLine();

        try
        {
            var queryParams = $"?limit={limit}";
            if (!string.IsNullOrEmpty(category)) queryParams += $"&category={Uri.EscapeDataString(category)}";

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var response = await http.GetAsync($"{serverUrl}/admin/memories{queryParams}");

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Error: Server returned {(int)response.StatusCode} {response.ReasonPhrase}");
                return 1;
            }

            var json = await response.Content.ReadAsStringAsync();
            var memories = JsonSerializer.Deserialize<List<MemoryEntry>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (memories == null || memories.Count == 0)
            {
                Console.WriteLine("No memories found.");
                return 0;
            }

            foreach (var mem in memories)
            {
                Console.WriteLine($"  [{mem.Id}] {mem.Category} — score: {mem.Score:F2}");
                Console.WriteLine($"       {Truncate(mem.Content, 80)}");
                Console.WriteLine($"       {mem.CreatedAt:yyyy-MM-dd HH:mm:ss UTC}");
                Console.WriteLine();
            }

            Console.WriteLine($"Total: {memories.Count} memory/memories");
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"Error connecting to server: {ex.Message}");
            return 1;
        }

        return 0;
    }

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength] + "...";

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: dotnet run -- list-memories [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --server, -s <url>    Server URL (default: http://localhost:5098)");
        Console.WriteLine("  --limit, -n <number>  Max memories to show (default: 10)");
        Console.WriteLine("  --category, -c <cat>  Filter by category");
        Console.WriteLine("  --help, -h            Show this help");
    }

    private class MemoryEntry
    {
        public long Id { get; set; }
        public string Category { get; set; } = string.Empty;
        public double Score { get; set; }
        public string Content { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
    }
}
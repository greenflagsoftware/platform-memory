using System.Text.Json;

namespace AgentMemory.Cli.Commands;

public static class MigrateCommand
{
    public static async Task<int> ExecuteAsync(string[] args)
    {
        var serverUrl = "http://localhost:5098";

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
            }
        }

        Console.WriteLine("AgentMemory — Apply pending migrations");
        Console.WriteLine(new string('-', 50));
        Console.WriteLine($"Server: {serverUrl}");
        Console.WriteLine();

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var response = await http.PostAsync($"{serverUrl}/admin/migrate", null);

            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Error: Server returned {(int)response.StatusCode} {response.ReasonPhrase}");
                Console.WriteLine(json);
                return 1;
            }

            var result = JsonSerializer.Deserialize<MigrateResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (result?.Applied is null || result.Applied.Length == 0)
            {
                Console.WriteLine("Schema is already up to date — no pending migrations.");
                return 0;
            }

            Console.WriteLine($"Applied {result.Applied.Length} migration(s):");
            foreach (var migration in result.Applied)
            {
                Console.WriteLine($"  - {migration}");
            }
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
        Console.WriteLine("Usage: dotnet run -- migrate [options]");
        Console.WriteLine();
        Console.WriteLine("Applies any pending EF Core migrations against the server's database.");
        Console.WriteLine("The server also migrates automatically on startup — this is for applying");
        Console.WriteLine("migrations on demand without restarting it.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --server, -s <url>    Server URL (default: http://localhost:5098)");
        Console.WriteLine("  --help, -h            Show this help");
    }

    private class MigrateResponse
    {
        public string Status { get; set; } = string.Empty;
        public string[]? Applied { get; set; }
    }
}

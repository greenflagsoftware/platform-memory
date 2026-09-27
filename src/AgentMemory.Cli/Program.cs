using AgentMemory.Cli.Commands;

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

switch (command)
{
    case "list-memories":
        return await ListMemoriesCommand.ExecuteAsync(args[1..]);

    case "re-classify":
    case "reclassify":
        return await ReclassifyCommand.ExecuteAsync(args[1..]);

    case "migrate":
        return await MigrateCommand.ExecuteAsync(args[1..]);

    case "help":
    case "--help":
    case "-h":
    default:
        PrintHelp();
        return 0;
}

static void PrintHelp()
{
    Console.WriteLine("AgentMemory CLI — Admin commands");
    Console.WriteLine();
    Console.WriteLine("Usage: dotnet run -- <command> [options]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  list-memories        List recent memories from the server");
    Console.WriteLine("  re-classify <id>     Re-run classification/embedding for a stored capture");
    Console.WriteLine("  migrate              Apply pending EF Core migrations on demand");
    Console.WriteLine("  help                 Show this help");
    Console.WriteLine();
    Console.WriteLine("Run 'dotnet run -- <command> --help' for command-specific options.");
}
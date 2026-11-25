using System.CommandLine;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using slnsync;

var configArgument = new Argument<FileInfo>("config")
{
    Description = "Path to an existing XML configuration file."
};

var watchOption = new Option<bool>("--watch")
{
    Description = "Keep running and re-sync when solution files change."
};

var rootCommand = new RootCommand("Keeps an aggregate solution in sync with source solutions.")
{
    configArgument,
    watchOption
};

rootCommand.SetAction(async (parse, cancellationToken) =>
{
    var configFile = parse.GetValue(configArgument)!;
    if (!File.Exists(configFile.FullName))
        throw new FileNotFoundException("Configuration file not found.", configFile.FullName);

    var sync = new Synchronizer(ReadConfig(configFile));

    if (!parse.GetValue(watchOption))
    {
        await sync.FromSourcesAsync(null, cancellationToken);
        Console.WriteLine($"Synced {sync.AggregatePath}");
        return 0;
    }

    using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop.Cancel();
    };

    var changes = Channel.CreateUnbounded<Change>(new UnboundedChannelOptions { SingleReader = true });
    using var watcher = sync.Watch(change => changes.Writer.TryWrite(change));

    Console.WriteLine("Watching for changes (Ctrl+C to stop).");

    async Task Apply(Change change)
    {
        if (change.FromAggregate)
        {
            await sync.FromAggregateAsync(stop.Token);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Updated sources from {sync.AggregatePath}");
        }
        else
        {
            await sync.FromSourcesAsync(change.SourcePath, stop.Token);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Updated {sync.AggregatePath}");
        }
    }

    async Task ApplySafely(Change change)
    {
        try
        {
            await Apply(change);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {ex.Message}");
        }
    }

    try
    {
        await ApplySafely(new Change(false, null));
        while (true)
        {
            var change = await changes.Reader.ReadAsync(stop.Token);
            await ApplySafely(change);
        }
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested)
    {
        Console.WriteLine("Stopped.");
    }

    return 0;
});

return await rootCommand.Parse(args).InvokeAsync();

static Config ReadConfig(FileInfo file)
{
    if (!file.Extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
        throw new NotSupportedException($"Unsupported configuration format: {file.Extension}. Expected .xml.");

    var config = new ConfigurationBuilder().AddXmlFile(file.FullName).Build().Get<Config>()
                 ?? throw new InvalidOperationException("Could not bind configuration.");
    if (string.IsNullOrWhiteSpace(config.SolutionPath))
        throw new InvalidOperationException("SolutionPath is required in the config file.");
    if (config.ProjectPaths.Count == 0)
        throw new InvalidOperationException("ProjectPaths must contain at least one entry.");
    if (config.ProjectPaths.Any(p => string.IsNullOrWhiteSpace(p.Path) || string.IsNullOrWhiteSpace(p.Folder)))
        throw new InvalidOperationException("Each ProjectPaths entry requires a Path and Folder.");

    var directory = file.DirectoryName!;
    return new Config
    {
        SolutionPath = Path.GetFullPath(config.SolutionPath, directory),
        ProjectPaths =
        [
            .. config.ProjectPaths
                .Select(p => p with { Path = Path.GetFullPath(p.Path, directory) })
        ]
    };
}
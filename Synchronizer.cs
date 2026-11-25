using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.VisualStudio.SolutionPersistence;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace slnsync;

internal sealed record Change(bool FromAggregate, string? SourcePath);

internal sealed class Synchronizer(Config config)
{
    private readonly ConcurrentDictionary<string, string> _fingerprints = new(StringComparer.OrdinalIgnoreCase);

    public string AggregatePath => config.SolutionPath;

    public async Task FromSourcesAsync(string? changedSource, CancellationToken cancellationToken)
    {
        var (serializer, aggregate) = await OpenAsync(AggregatePath);
        var aggregateDir = DirectoryOf(AggregatePath);

        foreach (var source in config.ProjectPaths)
        {
            if (changedSource is not null && !Same(source.Path, changedSource)) continue;
            if (!File.Exists(source.Path)) continue;

            var (_, model) = await OpenAsync(source.Path);
            var folder = aggregate.FindFolder($"/{source.Folder}/")
                         ?? aggregate.AddFolder($"/{source.Folder}/");

            foreach (var project in aggregate.SolutionProjects.Where(p => p.Parent == folder).ToArray())
                aggregate.RemoveProject(project);

            var sourceDir = DirectoryOf(source.Path);
            foreach (var project in model.SolutionProjects)
            {
                var full = Full(sourceDir, project.FilePath);
                if (aggregate.SolutionProjects.Any(p => Same(Full(aggregateDir, p.FilePath), full))) continue;
                aggregate.AddProject(Path.GetRelativePath(aggregateDir, full), project.Type, folder);
            }
        }

        await SaveAsync(AggregatePath, serializer, aggregate, cancellationToken);
    }

    public async Task FromAggregateAsync(CancellationToken cancellationToken)
    {
        var (_, aggregate) = await OpenAsync(AggregatePath);
        var aggregateDir = DirectoryOf(AggregatePath);

        foreach (var source in config.ProjectPaths)
        {
            var folder = aggregate.FindFolder($"/{source.Folder}/");
            var projects = folder is null
                ? []
                : aggregate.SolutionProjects.Where(p => p.Parent == folder).ToArray();

            var (serializer, model) = await OpenAsync(source.Path);
            foreach (var project in model.SolutionProjects.ToArray())
                model.RemoveProject(project);

            var sourceDir = DirectoryOf(source.Path);
            foreach (var project in projects)
                model.AddProject(Path.GetRelativePath(sourceDir, Full(aggregateDir, project.FilePath)), project.Type);

            await SaveAsync(source.Path, serializer, model, cancellationToken);
        }
    }

    public IDisposable Watch(Action<Change> onChange)
    {
        var paths = new[] { AggregatePath }
            .Concat(config.ProjectPaths.Select(p => p.Path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var watchers = paths
            .Select(DirectoryOf)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .Select(directory => new FileSystemWatcher(directory) { NotifyFilter = NotifyFilters.LastWrite })
            .ToArray();

        FileSystemEventHandler handler = (_, e) =>
        {
            var path = Path.GetFullPath(e.FullPath);
            if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
            if (_fingerprints.TryGetValue(path, out var known) && known == Fingerprint(path)) return;
            onChange(new Change(Same(path, AggregatePath), path));
        };

        foreach (var watcher in watchers)
        {
            watcher.Changed += handler;
            watcher.EnableRaisingEvents = true;
        }

        return new Watchers(watchers);
    }

    private async Task<(ISolutionSerializer Serializer, SolutionModel Model)> OpenAsync(string path)
    {
        var serializer = SolutionSerializers.GetSerializerByMoniker(path) ?? SolutionSerializers.SlnXml;
        var model = File.Exists(path) ? await serializer.OpenAsync(path, CancellationToken.None) : new SolutionModel();
        _fingerprints[Path.GetFullPath(path)] = Fingerprint(path);
        return (serializer, model);
    }

    private async Task SaveAsync(string path, ISolutionSerializer serializer, SolutionModel model,
        CancellationToken cancellationToken)
    {
        await serializer.SaveAsync(path, model, cancellationToken);
        _fingerprints[Path.GetFullPath(path)] = Fingerprint(path);
    }

    private static string Fingerprint(string path)
    {
        return File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "";
    }

    private static string Full(string directory, string path)
    {
        return Path.GetFullPath(path, directory);
    }

    private static string DirectoryOf(string path)
    {
        return Path.GetDirectoryName(Path.GetFullPath(path))!;
    }

    private static bool Same(string a, string b)
    {
        return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Watchers(FileSystemWatcher[] watchers) : IDisposable
    {
        public void Dispose()
        {
            foreach (var watcher in watchers)
                watcher.Dispose();
        }
    }
}
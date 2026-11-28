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
            var root = aggregate.FindFolder($"/{source.Folder}/")
                       ?? aggregate.AddFolder($"/{source.Folder}/");

            ClearFolder(aggregate, root);

            var sourceDir = DirectoryOf(source.Path);
            foreach (var project in model.SolutionProjects)
            {
                var full = Full(sourceDir, project.FilePath);
                if (aggregate.SolutionProjects.Any(p => Same(Full(aggregateDir, p.FilePath), full))) continue;
                var folder = AggregateFolder(aggregate, root, project.Parent);
                aggregate.AddProject(Path.GetRelativePath(aggregateDir, full), project.Type, folder);
            }

            foreach (var (sourceFolder, file) in Files(model))
            {
                var full = Full(sourceDir, file);
                var folder = AggregateFolder(aggregate, root, sourceFolder);
                folder.AddFile(Path.GetRelativePath(aggregateDir, full));
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
            var root = aggregate.FindFolder($"/{source.Folder}/");
            var projects = root is null
                ? []
                : aggregate.SolutionProjects.Where(p => IsInFolder(p.Parent, root)).ToArray();
            var files = root is null
                ? []
                : aggregate.SolutionFolders
                    .Where(f => IsInFolder(f, root))
                    .SelectMany(f => Files(f).Select(file => (Folder: f, File: file)))
                    .ToArray();

            var (serializer, model) = await OpenAsync(source.Path);
            foreach (var project in model.SolutionProjects.ToArray())
                model.RemoveProject(project);
            foreach (var folder in model.SolutionFolders)
                foreach (var file in (folder.Files ?? []).ToArray())
                    folder.RemoveFile(file);

            var sourceDir = DirectoryOf(source.Path);
            foreach (var project in projects)
            {
                var folder = SourceFolder(model, root!, project.Parent);
                model.AddProject(Path.GetRelativePath(sourceDir, Full(aggregateDir, project.FilePath)), project.Type, folder);
            }

            foreach (var (aggregateFolder, file) in files)
            {
                var folder = SourceFolder(model, root!, aggregateFolder);
                if (folder is null) continue;
                folder.AddFile(Path.GetRelativePath(sourceDir, Full(aggregateDir, file)));
            }

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

    private static void ClearFolder(SolutionModel solution, SolutionFolderModel folder)
    {
        foreach (var target in solution.SolutionFolders.Where(f => IsInFolder(f, folder)).ToArray())
            foreach (var file in Files(target).ToArray())
                target.RemoveFile(file);

        foreach (var child in solution.SolutionFolders.Where(f => ReferenceEquals(f.Parent, folder)).ToArray())
            solution.RemoveFolder(child);

        foreach (var project in solution.SolutionProjects.Where(p => ReferenceEquals(p.Parent, folder)).ToArray())
            solution.RemoveProject(project);
    }

    private static SolutionFolderModel AggregateFolder(SolutionModel aggregate, SolutionFolderModel root,
        SolutionFolderModel? sourceFolder)
    {
        return sourceFolder is null
            ? root
            : aggregate.AddFolder(root.Path + sourceFolder.Path.TrimStart('/'));
    }

    private static SolutionFolderModel? SourceFolder(SolutionModel model, SolutionFolderModel root,
        SolutionFolderModel? aggregateFolder)
    {
        if (aggregateFolder is null || ReferenceEquals(aggregateFolder, root) || !IsInFolder(aggregateFolder, root))
            return null;
        if (!aggregateFolder.Path.StartsWith(root.Path, StringComparison.Ordinal)) return null;

        var path = aggregateFolder.Path[(root.Path.Length - 1)..];
        return model.FindFolder(path) ?? model.AddFolder(path);
    }

    private static IEnumerable<string> Files(SolutionFolderModel folder)
    {
        return folder.Files ?? [];
    }

    private static IEnumerable<(SolutionFolderModel Folder, string File)> Files(SolutionModel model)
    {
        foreach (var folder in model.SolutionFolders)
            foreach (var file in Files(folder))
                yield return (folder, file);
    }

    private static bool IsInFolder(SolutionFolderModel? folder, SolutionFolderModel root)
    {
        for (var current = folder; current is not null; current = current.Parent)
            if (ReferenceEquals(current, root)) return true;

        return false;
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
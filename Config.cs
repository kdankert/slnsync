namespace slnsync;

public sealed class Config
{
    public string SolutionPath { get; set; } = "";

    public List<ProjectRef> ProjectPaths { get; set; } = [];
}

public sealed record ProjectRef(string Path, string Folder);
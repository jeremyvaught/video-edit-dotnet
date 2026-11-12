using System.Text.Json;
using System.Text.Json.Serialization;
using VideoEdit.Models;

namespace VideoEdit.Services;

public sealed class ProjectStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _projectPath;
    private readonly string _lockPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProjectStore(VideoEditOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ProjectDirectory))
        {
            throw VideoEditException.Validation(
                "Set VideoEdit:ProjectDirectory in appsettings.Local.json or VideoEdit__ProjectDirectory before opening a project.");
        }

        ProjectDirectory = options.ProjectDirectory;
        _projectPath = Path.Combine(ProjectDirectory, "edit", "project.json");
        _lockPath = Path.Combine(ProjectDirectory, "edit", "project.lock");
    }

    public string ProjectDirectory { get; }

    public string ProjectPath => _projectPath;

    public bool Exists() => File.Exists(_projectPath);

    public ProjectDocument SaveBoundRender(BoundRenderSettings render)
    {
        _gate.Wait();
        try
        {
            using var fileLock = Acquire();
            var project = File.Exists(_projectPath) ? ReadUnlocked() : new ProjectDocument();
            project.BoundRender = render;
            WriteUnlocked(project);
            project.LiveRender = RenderIdentity.FromLive(project, ProjectDirectory);
            return project;
        }
        finally
        {
            _gate.Release();
        }
    }

    public ProjectDocument Load()
    {
        _gate.Wait();
        try
        {
            using var fileLock = Acquire();
            return ReadUnlocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    public ProjectDocument Mutate(int expectedRevision, Func<ProjectDocument, bool> mutate)
    {
        _gate.Wait();
        try
        {
            using var fileLock = Acquire();
            var project = File.Exists(_projectPath) ? ReadUnlocked() : new ProjectDocument();
            if (project.Revision != expectedRevision)
            {
                throw VideoEditException.StaleWrite(expectedRevision, project.Revision);
            }

            var changed = mutate(project);
            if (!changed)
            {
                return project;
            }

            ProjectValidator.Validate(project);
            project.Revision = expectedRevision + 1;
            WriteUnlocked(project);
            return project;
        }
        finally
        {
            _gate.Release();
        }
    }

    public T WithWriteLock<T>(Func<ProjectDocument, T> work)
    {
        _gate.Wait();
        try
        {
            using var fileLock = Acquire();
            var project = File.Exists(_projectPath) ? ReadUnlocked() : new ProjectDocument();
            return work(project);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ProjectDocument SaveNew(ProjectDocument project)
    {
        _gate.Wait();
        try
        {
            using var fileLock = Acquire();
            if (File.Exists(_projectPath))
            {
                throw VideoEditException.Conflict("A project file already exists.");
            }

            ProjectValidator.Validate(project);
            project.Revision = Math.Max(1, project.Revision);
            WriteUnlocked(project);
            return project;
        }
        finally
        {
            _gate.Release();
        }
    }

    private ProjectDocument ReadUnlocked()
    {
        var json = File.ReadAllText(_projectPath);
        var project = JsonSerializer.Deserialize<ProjectDocument>(json, JsonOptions)
            ?? throw VideoEditException.Validation("Project file is empty.");
        project.LiveRender = RenderIdentity.FromLive(project, ProjectDirectory);
        return project;
    }

    private void WriteUnlocked(ProjectDocument project)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_projectPath)!);
        var json = JsonSerializer.Serialize(project, JsonOptions);
        var temp = _projectPath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _projectPath, overwrite: true);
    }

    private FileStream Acquire()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_lockPath)!);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(
                    _lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(50);
            }
        }
    }
}

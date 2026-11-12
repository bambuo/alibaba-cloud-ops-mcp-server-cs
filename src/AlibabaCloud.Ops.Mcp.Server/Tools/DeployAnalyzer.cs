using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AlibabaCloud.Ops.Mcp.Server.Tools;

/// <summary>扫描探针文件，识别项目的部署方式、包管理器与技术栈。</summary>
public static partial class DeployAnalyzer
{
    private static readonly (string File, string? PackageManager, string? Framework)[] Probes =
    [
        ("package.json", "npm", "nodejs"),
        ("package-lock.json", "npm", "nodejs"),
        ("yarn.lock", "yarn", "nodejs"),
        ("pnpm-lock.yaml", "pnpm", "nodejs"),
        (".nvmrc", "nodejs", null),
        ("requirements.txt", "pip", "python"),
        ("Pipfile", "pipenv", "python"),
        ("pyproject.toml", "poetry", "python"),
        ("setup.py", "setuptools", "python"),
        ("environment.yml", "conda", "python"),
        (".python-version", "python", null),
        ("pom.xml", "maven", "java"),
        ("build.gradle", "gradle", "java"),
        ("build.gradle.kts", "gradle", "java"),
        ("go.mod", "go", "go"),
        ("go.sum", "go", "go"),
        ("Gopkg.toml", "dep", "go"),
        ("Cargo.toml", "cargo", "rust"),
        ("Cargo.lock", "cargo", "rust"),
        ("composer.json", "composer", "php"),
        ("Gemfile", "bundler", "ruby"),
        ("Dockerfile", "docker", "docker"),
        ("docker-compose.yml", "docker-compose", "docker"),
        ("docker-compose.yaml", "docker-compose", "docker"),
        (".dockerignore", "docker", "docker"),
        ("Makefile", "make", null),
        ("CMakeLists.txt", "cmake", "cpp"),
    ];

    /// <summary>分析项目根目录并返回结构化的识别结果。</summary>
    public static JsonObject Analyze(DirectoryInfo root)
    {
        var packageManagers = new List<string>();
        var frameworks = new List<string>();
        var runtimeVersions = new JsonObject();
        var configFiles = new JsonObject();

        foreach (var (file, packageManager, framework) in Probes)
        {
            var path = Path.Combine(root.FullName, file);
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                continue;
            }

            if (packageManager is not null && !packageManagers.Contains(packageManager))
            {
                packageManagers.Add(packageManager);
            }

            if (framework is not null && !frameworks.Contains(framework))
            {
                frameworks.Add(framework);
            }

            configFiles[file] = path;
        }

        ReadNodeVersion(root, runtimeVersions);
        if (File.Exists(Path.Combine(root.FullName, "requirements.txt"))
            || File.Exists(Path.Combine(root.FullName, "pyproject.toml")))
        {
            ReadPythonVersion(root, runtimeVersions);
        }

        ReadGoVersion(root, runtimeVersions);
        ReadDockerImage(root, runtimeVersions);

        var deploymentMethods = new List<string>();
        if (packageManagers.Any(x => x is "npm" or "yarn" or "pnpm"))
        {
            deploymentMethods.Add("npm");
        }

        if (packageManagers.Any(x => x is "pip" or "pipenv" or "poetry"))
        {
            deploymentMethods.Add("python");
        }

        if (packageManagers.Any(x => x is "maven" or "gradle"))
        {
            deploymentMethods.Add("java");
        }

        if (packageManagers.Contains("go"))
        {
            deploymentMethods.Add("go");
        }

        if (packageManagers.Contains("docker"))
        {
            deploymentMethods.Add("docker");
        }

        var detected = packageManagers.Count > 0 || frameworks.Count > 0;
        if (!detected)
        {
            deploymentMethods.Add("unknown");
        }

        return new JsonObject
        {
            ["directory"] = root.FullName,
            ["deployment_methods"] = ToArray(deploymentMethods),
            ["package_managers"] = ToArray(packageManagers),
            ["frameworks"] = ToArray(frameworks),
            ["runtime_versions"] = runtimeVersions,
            ["config_files"] = configFiles,
            ["detected"] = detected,
        };
    }

    /// <summary>读取 package.json 的 engines.node。</summary>
    private static void ReadNodeVersion(DirectoryInfo root, JsonObject versions)
    {
        var path = Path.Combine(root.FullName, "package.json");
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("engines", out var engines)
                && engines.ValueKind == JsonValueKind.Object
                && engines.TryGetProperty("node", out var node)
                && node.ValueKind != JsonValueKind.Null)
            {
                versions["node"] = node.ValueKind == JsonValueKind.String ? node.GetString() : node.GetRawText();
            }
        }
        catch (Exception)
        {
            // 不可读的 package.json 不应让整体分析失败
        }
    }

    /// <summary>读取 .python-version。</summary>
    private static void ReadPythonVersion(DirectoryInfo root, JsonObject versions)
    {
        var path = Path.Combine(root.FullName, ".python-version");
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            versions["python"] = File.ReadAllText(path).Trim();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>从 go.mod 提取 Go 版本。</summary>
    private static void ReadGoVersion(DirectoryInfo root, JsonObject versions)
    {
        var path = Path.Combine(root.FullName, "go.mod");
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var match = GoVersionRegex().Match(File.ReadAllText(path));
            if (match.Success)
            {
                versions["go"] = match.Groups[1].Value;
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>从 Dockerfile 的 FROM 行提取镜像。</summary>
    private static void ReadDockerImage(DirectoryInfo root, JsonObject versions)
    {
        var path = Path.Combine(root.FullName, "Dockerfile");
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var content = File.ReadAllText(path);
            var node = NodeImageRegex().Match(content);
            if (node.Success)
            {
                versions["node_docker"] = node.Groups[1].Value;
                return;
            }

            var python = PythonImageRegex().Match(content);
            if (python.Success)
            {
                versions["python_docker"] = python.Groups[1].Value;
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>字符串集合转 JsonArray。</summary>
    private static JsonArray ToArray(IEnumerable<string> values) =>
        new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    [GeneratedRegex(@"go\s+(\d+\.\d+)")]
    /// <summary>匹配 go.mod 中的 Go 版本声明。</summary>
    private static partial Regex GoVersionRegex();

    [GeneratedRegex(@"FROM node:?(\S+)")]
    /// <summary>匹配 Dockerfile 中的 Node 基础镜像。</summary>
    private static partial Regex NodeImageRegex();

    [GeneratedRegex(@"FROM\s+python:?(\S+)")]
    /// <summary>匹配 Dockerfile 中的 Python 基础镜像。</summary>
    private static partial Regex PythonImageRegex();
}

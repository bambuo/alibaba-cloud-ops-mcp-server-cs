using System.Formats.Tar;
using System.IO.Compression;

namespace AlibabaCloud.Ops.Mcp.Server.Tools;

/// <summary>部署产物分析结果：文件类型、归档格式与成员清单。</summary>
public sealed record DeployFileAnalysis(
    string FileType,
    string? ArchiveType,
    string FileName,
    IReadOnlyList<string> FilesInArchive,
    string? ExtractedDirName);

/// <summary>归档中识别出的可执行文件与构建描述文件。</summary>
public sealed record ExecutableFiles(
    IReadOnlyList<string> JarFiles,
    IReadOnlyList<string> PyFiles,
    IReadOnlyList<string> JsFiles,
    IReadOnlyList<string> GoBinaries,
    IReadOnlyList<string> ShellScripts,
    string? PackageJson,
    string? RequirementsTxt,
    string? Dockerfile);

/// <summary>归档分析与启停命令生成规则。</summary>
public static class DeployRules
{
    /// <summary>识别产物类型并读取 tar/tar.gz/zip 的成员列表。</summary>
    public static DeployFileAnalysis AnalyzeFile(string filePath)
    {
        var path = Path.GetFullPath(filePath);
        if (!File.Exists(path))
        {
            return new DeployFileAnalysis("unknown", null, Path.GetFileName(path), [], null);
        }

        var fileName = Path.GetFileName(path);
        var lower = fileName.ToLowerInvariant();
        var files = new List<string>();
        string? archiveType = null;

        try
        {
            if (lower.EndsWith(".tar.gz", StringComparison.Ordinal) || lower.EndsWith(".tgz", StringComparison.Ordinal))
            {
                archiveType = "tar.gz";
                files = ReadTar(path, gzip: true);
            }
            else if (lower.EndsWith(".tar", StringComparison.Ordinal))
            {
                archiveType = "tar";
                files = ReadTar(path, gzip: false);
            }
            else if (lower.EndsWith(".zip", StringComparison.Ordinal))
            {
                archiveType = "zip";
                using var archive = ZipFile.OpenRead(path);
                files = archive.Entries.Select(entry => entry.FullName).ToList();
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            return new DeployFileAnalysis("unknown", null, fileName, [], null);
        }

        return archiveType is null
            ? new DeployFileAnalysis("file", null, fileName, [], null)
            : new DeployFileAnalysis("archive", archiveType, fileName, files, ExtractTopLevelDir(files));
    }

    /// <summary>仅当所有成员位于同一顶层目录时返回该目录名。</summary>
    public static string? ExtractTopLevelDir(IReadOnlyList<string> members)
    {
        if (members.Count == 0)
        {
            return null;
        }

        var topLevel = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            var first = member.Split('/', 2)[0];
            if (!string.IsNullOrEmpty(first))
            {
                topLevel.Add(first);
            }
        }

        return topLevel.Count == 1 ? topLevel.First() : null;
    }

    /// <summary>按语言筛选 jar/py/js/二进制与构建描述文件。</summary>
    public static ExecutableFiles FindExecutables(IReadOnlyList<string> files, string deployLanguage)
    {
        var jars = new List<string>();
        var python = new List<string>();
        var js = new List<string>();
        var goBinaries = new List<string>();
        var shells = new List<string>();
        string? packageJson = null;
        string? requirements = null;
        string? dockerfile = null;

        foreach (var path in files)
        {
            var name = path.Split('/')[^1];
            var lower = name.ToLowerInvariant();

            if (lower.EndsWith(".jar", StringComparison.Ordinal))
            {
                jars.Add(path);
            }
            else if (lower.EndsWith(".py", StringComparison.Ordinal))
            {
                python.Add(path);
            }
            else if (lower == "package.json")
            {
                packageJson = path;
            }
            else if (lower.EndsWith(".js", StringComparison.Ordinal))
            {
                js.Add(path);
            }
            else if (lower.EndsWith(".sh", StringComparison.Ordinal))
            {
                shells.Add(path);
            }
            else if (lower == "requirements.txt")
            {
                requirements = path;
            }
            else if (lower == "dockerfile")
            {
                dockerfile = path;
            }
            else if (deployLanguage == "golang" && IsPotentialGoBinary(lower))
            {
                goBinaries.Add(path);
            }
        }

        return new ExecutableFiles(jars, python, js, goBinaries, shells, packageJson, requirements, dockerfile);
    }

    /// <summary>按语言生成解压与后台启动命令，无法生成时返回 null。</summary>
    public static string? GenerateStartCommand(
        string filePath,
        string deployLanguage,
        DeployFileAnalysis analysis,
        string? extractedDirName,
        string? applicationName,
        int? port)
    {
        var fileName = Path.GetFileName(filePath);
        var isArchive = analysis.FileType == "archive";
        var extractCommand = string.Empty;
        var workDir = string.Empty;

        if (isArchive)
        {
            extractCommand = analysis.ArchiveType switch
            {
                "tar.gz" => $"[ -f {fileName} ] && tar -xzf {fileName} || exit 1",
                "tar" => $"[ -f {fileName} ] && tar -xf {fileName} || exit 1",
                "zip" => $"[ -f {fileName} ] && unzip -o {fileName} || exit 1",
                _ => string.Empty,
            };

            if (!string.IsNullOrEmpty(extractedDirName))
            {
                workDir = $" && [ -d {extractedDirName} ] && cd {extractedDirName} || exit 1";
            }
        }

        var executables = FindExecutables(analysis.FilesInArchive, deployLanguage);
        string startCommand;

        switch (deployLanguage)
        {
            case "java":
            {
                var jar = executables.JarFiles.FirstOrDefault();
                var jarName = jar is null ? "app.jar" : jar.Split('/')[^1];
                var jarPath = isArchive && !string.IsNullOrEmpty(extractedDirName)
                    ? $"{extractedDirName}/{jarName}"
                    : jarName;
                startCommand = $"[ -f {jarPath} ] && nohup java -jar {jarPath} > /root/app.log 2>&1 &";
                break;
            }

            case "python":
            {
                var preferred = new[] { "main.py", "app.py", "run.py", "server.py", "application.py" };
                var pyFile = PickPreferred(executables.PyFiles, preferred) ?? "app.py";
                var pyPath = isArchive && !string.IsNullOrEmpty(extractedDirName)
                    ? $"{extractedDirName}/{pyFile.Split('/')[^1]}"
                    : pyFile.Split('/')[^1];
                startCommand = $"[ -f {pyPath} ] && nohup python {pyPath} > /root/app.log 2>&1 &";
                break;
            }

            case "nodejs":
            {
                if (executables.PackageJson is not null)
                {
                    var startScript = executables.ShellScripts
                        .FirstOrDefault(script => script.Contains("start", StringComparison.OrdinalIgnoreCase));
                    startCommand = startScript is null
                        ? "command -v npm >/dev/null 2>&1 && [ -f package.json ] && npm install && nohup npm start > /root/app.log 2>&1 &"
                        : $"command -v npm >/dev/null 2>&1 && [ -f package.json ] && npm install && [ -f {startScript.Split('/')[^1]} ] && chmod +x {startScript.Split('/')[^1]} && nohup ./{startScript.Split('/')[^1]} > /root/app.log 2>&1 &";
                    break;
                }

                var jsFile = PickPreferred(executables.JsFiles, ["index.js", "app.js", "server.js", "main.js"]);
                if (jsFile is null)
                {
                    return null;
                }

                var jsPath = isArchive && !string.IsNullOrEmpty(extractedDirName)
                    ? $"{extractedDirName}/{jsFile.Split('/')[^1]}"
                    : jsFile.Split('/')[^1];
                startCommand = $"[ -f {jsPath} ] && nohup node {jsPath} > /root/app.log 2>&1 &";
                break;
            }

            case "golang":
            {
                var commonNames = new[] { "app", "main", "server", "application" };
                string? binary = null;
                foreach (var candidate in commonNames)
                {
                    var match = analysis.FilesInArchive
                        .Select(path => path.Split('/')[^1])
                        .FirstOrDefault(name => name == candidate || name.StartsWith(candidate, StringComparison.Ordinal));
                    if (match is not null)
                    {
                        binary = match;
                        break;
                    }
                }

                binary ??= Path.GetFileNameWithoutExtension(fileName);
                var binaryPath = isArchive && !string.IsNullOrEmpty(extractedDirName)
                    ? $"{extractedDirName}/{binary}"
                    : binary;
                startCommand = $"[ -f {binaryPath} ] && chmod +x {binaryPath} && nohup ./{binaryPath} > /root/app.log 2>&1 & || true";
                break;
            }

            case "docker":
            {
                if (executables.Dockerfile is null)
                {
                    return null;
                }

                var imageName = NormalizeName(applicationName);
                var portMapping = port is > 0 ? $"-p {port}:{port} " : string.Empty;
                startCommand =
                    $"command -v docker >/dev/null 2>&1 && docker stop {imageName} 2>/dev/null || true && docker rm {imageName} 2>/dev/null || true && [ -f Dockerfile ] && docker build -t {imageName}:latest . && docker run -d --name {imageName} {portMapping}{imageName}:latest";
                break;
            }

            default:
                return null;
        }

        if (!isArchive)
        {
            return startCommand;
        }

        return string.IsNullOrEmpty(workDir)
            ? $"{extractCommand} && {startCommand}"
            : $"{extractCommand}{workDir} && {startCommand}";
    }

    /// <summary>按语言生成停止命令，docker 按容器名处理。</summary>
    public static string GenerateStopCommand(string deployLanguage, string? applicationName)
    {
        if (deployLanguage == "docker")
        {
            var containerName = NormalizeName(applicationName);
            return $"docker stop {containerName} 2>/dev/null || true && docker rm {containerName} 2>/dev/null || true";
        }

        return deployLanguage switch
        {
            "java" => "pkill -f 'java -jar' || true",
            "python" => "pkill -f 'python.*\\.py' || true",
            "nodejs" => "pkill -f 'node.*\\.js' || pkill -f 'npm start' || true",
            "golang" => "pkill -f './app' || pkill -f './main' || pkill -f './server' || true",
            _ => "pkill -f 'app' || true",
        };
    }

    /// <summary>按优先级挑选入口文件，没有命中时取第一个。</summary>
    private static string? PickPreferred(IReadOnlyList<string> files, IReadOnlyList<string> preferred)
    {
        foreach (var name in preferred)
        {
            var match = files.FirstOrDefault(file => file.EndsWith(name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return files.FirstOrDefault();
    }

    /// <summary>排除文档与带扩展名的文件后判断是否为 Go 二进制。</summary>
    private static bool IsPotentialGoBinary(string fileName)
    {
        var nonBinary = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "readme", "license", "makefile", "dockerfile", "changelog",
            "contributing", "authors", "version", "manifest", "config",
            "gitignore", "dockerignore", "editorconfig", "env",
        };

        var hasExtension = !fileName.StartsWith('.') && fileName.Contains('.');
        if (hasExtension)
        {
            return false;
        }

        return !nonBinary.Contains(fileName);
    }

    /// <summary>应用名转小写并把空格、下划线替换为连字符。</summary>
    private static string NormalizeName(string? applicationName) =>
        (applicationName ?? "app").ToLowerInvariant().Replace(' ', '-').Replace('_', '-');

    /// <summary>读取 tar 或 tar.gz 的成员名。</summary>
    private static List<string> ReadTar(string path, bool gzip)
    {
        using var file = File.OpenRead(path);
        using Stream stream = gzip ? new GZipStream(file, CompressionMode.Decompress) : file;
        using var reader = new TarReader(stream);
        var names = new List<string>();
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            names.Add(entry.Name);
        }

        return names;
    }
}

using System.IO.Compression;
using AlibabaCloud.Ops.Mcp.Server.Tools;

namespace AlibabaCloud.Ops.Mcp.Server.Tests;

public sealed class DeployRulesTests
{
    [Theory]
    [InlineData(new[] { "demo/app.jar", "demo/start.sh" }, "demo")]
    [InlineData(new[] { "a.txt", "b/c.txt" }, null)]
    [InlineData(new string[] { }, null)]
    public void Extracts_single_top_level_directory(string[] members, string? expected)
    {
        Assert.Equal(expected, DeployRules.ExtractTopLevelDir(members));
    }

    [Fact]
    public void Analyzes_tar_gz_archive()
    {
        var path = Path.Combine(Path.GetTempPath(), $"deploy-{Guid.NewGuid():N}.tar.gz");
        try
        {
            using (var file = File.Create(path))
            using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.SmallestSize))
            using (var writer = new System.Formats.Tar.TarWriter(gzip, System.Formats.Tar.TarEntryFormat.Pax))
            {
                writer.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, "demo/main.py"));
                writer.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, "demo/requirements.txt"));
            }

            var analysis = DeployRules.AnalyzeFile(path);

            Assert.Equal("archive", analysis.FileType);
            Assert.Equal("tar.gz", analysis.ArchiveType);
            Assert.Equal("demo", analysis.ExtractedDirName);
            Assert.Contains("demo/main.py", analysis.FilesInArchive);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Analyzes_zip_archive()
    {
        var path = Path.Combine(Path.GetTempPath(), $"deploy-{Guid.NewGuid():N}.zip");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                archive.CreateEntry("demo/app.jar");
                archive.CreateEntry("demo/config.json");
            }

            var analysis = DeployRules.AnalyzeFile(path);
            Assert.Equal("archive", analysis.FileType);
            Assert.Equal("zip", analysis.ArchiveType);
            Assert.Equal("demo", analysis.ExtractedDirName);
            Assert.Contains("demo/app.jar", analysis.FilesInArchive);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Generates_java_start_command_for_archive()
    {
        var analysis = new DeployFileAnalysis("archive", "tar.gz", "app.tar.gz",
            ["demo/app.jar"], "demo");

        var command = DeployRules.GenerateStartCommand("/tmp/app.tar.gz", "java", analysis, "demo", "demo-app", 8080);

        Assert.Contains("tar -xzf app.tar.gz", command);
        Assert.Contains("cd demo", command);
        Assert.Contains("nohup java -jar demo/app.jar", command);
    }

    [Fact]
    public void Generates_python_start_command_with_preferred_entry()
    {
        var analysis = new DeployFileAnalysis("archive", "zip", "app.zip",
            ["demo/main.py", "demo/util.py"], "demo");

        var command = DeployRules.GenerateStartCommand("/tmp/app.zip", "python", analysis, "demo", "app", 8000);

        Assert.Contains("unzip -o app.zip", command);
        Assert.Contains("nohup python demo/main.py", command);
    }

    [Fact]
    public void Generates_node_command_with_npm_install()
    {
        var analysis = new DeployFileAnalysis("archive", "tar.gz", "app.tgz",
            ["app/package.json", "app/start.sh"], "app");

        var command = DeployRules.GenerateStartCommand("/tmp/app.tgz", "nodejs", analysis, "app", "web", 3000);

        Assert.Contains("npm install", command);
        Assert.Contains("chmod +x start.sh", command);
    }

    [Fact]
    public void Generates_docker_command_with_port_mapping()
    {
        var analysis = new DeployFileAnalysis("archive", "tar.gz", "app.tar.gz",
            ["app/Dockerfile"], "app");

        var command = DeployRules.GenerateStartCommand("/tmp/app.tar.gz", "docker", analysis, "app", "My_App", 8080);

        Assert.Contains("docker build -t my-app:latest", command);
        Assert.Contains("docker run -d --name my-app -p 8080:8080", command);
    }

    [Fact]
    public void Docker_requires_dockerfile()
    {
        var analysis = new DeployFileAnalysis("archive", "zip", "app.zip", ["app/main.go"], "app");

        Assert.Null(DeployRules.GenerateStartCommand("/tmp/app.zip", "docker", analysis, "app", "app", 80));
    }

    [Fact]
    public void Generates_node_command_from_index_js_without_package_json()
    {
        var analysis = new DeployFileAnalysis("archive", "zip", "app.zip", ["app/index.js"], "app");

        var command = DeployRules.GenerateStartCommand("/tmp/app.zip", "nodejs", analysis, "app", "web", 3000);

        Assert.Contains("nohup node app/index.js", command);
    }

    [Fact]
    public void Generates_go_command_for_binary()
    {
        var analysis = new DeployFileAnalysis("archive", "tar.gz", "app.tar.gz", ["demo/server"], "demo");

        var command = DeployRules.GenerateStartCommand("/tmp/app.tar.gz", "golang", analysis, "demo", "app", 80);

        Assert.Contains("chmod +x demo/server", command);
    }

    [Fact]
    public void Java_without_jar_falls_back_to_app_jar()
    {
        var analysis = new DeployFileAnalysis("file", null, "demo.jar", [], null);

        var command = DeployRules.GenerateStartCommand("/tmp/demo.jar", "java", analysis, null, "app", 80);

        Assert.Contains("nohup java -jar app.jar", command);
    }

    [Fact]
    public void Unknown_language_returns_null()
    {
        var analysis = new DeployFileAnalysis("file", null, "app.bin", [], null);

        Assert.Null(DeployRules.GenerateStartCommand("/tmp/app.bin", "rust", analysis, null, "app", 80));
    }

    [Fact]
    public void Finds_executables_in_archive_listing()
    {
        var files = new[] { "app/Dockerfile", "app/requirements.txt", "app/start.sh", "app/app.jar", "app/main.py" };

        var executables = DeployRules.FindExecutables(files, "python");

        Assert.Equal("app/Dockerfile", executables.Dockerfile);
        Assert.Equal("app/requirements.txt", executables.RequirementsTxt);
        Assert.Single(executables.ShellScripts);
        Assert.Single(executables.JarFiles);
        Assert.Single(executables.PyFiles);
    }

    [Theory]
    [InlineData("java", "pkill -f 'java -jar' || true")]
    [InlineData("python", "pkill -f 'python.*\\.py' || true")]
    [InlineData("golang", "pkill -f './app' || pkill -f './main' || pkill -f './server' || true")]
    [InlineData("nodejs", "pkill -f 'node.*\\.js' || pkill -f 'npm start' || true")]
    [InlineData("rust", "pkill -f 'app' || true")]
    public void Generates_stop_command(string language, string expected)
    {
        Assert.Equal(expected, DeployRules.GenerateStopCommand(language, "app"));
    }

    [Fact]
    public void Generates_extraction_for_plain_tar()
    {
        var analysis = new DeployFileAnalysis("archive", "tar", "app.tar", ["demo/server"], "demo");

        var command = DeployRules.GenerateStartCommand("/tmp/app.tar", "golang", analysis, "demo", "app", 80);

        Assert.Contains("tar -xf app.tar", command);
        Assert.DoesNotContain("tar -xzf", command);
    }

    [Fact]
    public void Unknown_archive_type_skips_extraction_step()
    {
        var analysis = new DeployFileAnalysis("archive", "rar", "app.rar", ["demo/main.py"], null);

        var command = DeployRules.GenerateStartCommand("/tmp/app.rar", "python", analysis, null, "app", 80);

        Assert.DoesNotContain("tar -x", command);
        Assert.DoesNotContain("unzip", command);
        Assert.Contains("nohup python main.py", command);
    }

    [Fact]
    public void Node_without_package_json_or_js_file_returns_null()
    {
        var analysis = new DeployFileAnalysis("archive", "tar.gz", "app.tar.gz", ["demo/readme.txt"], "demo");

        Assert.Null(DeployRules.GenerateStartCommand("/tmp/app.tar.gz", "nodejs", analysis, "demo", "app", 80));
    }

    [Fact]
    public void Docker_command_omits_port_mapping_when_port_unknown()
    {
        var analysis = new DeployFileAnalysis("archive", "tar.gz", "app.tar.gz", ["app/Dockerfile"], "app");

        var command = DeployRules.GenerateStartCommand("/tmp/app.tar.gz", "docker", analysis, "app", "app", null);

        Assert.DoesNotContain("-p ", command);
        Assert.Contains("docker run -d --name app app:latest", command);
    }

    [Fact]
    public void Analyzes_tgz_extension()
    {
        var path = Path.Combine(Path.GetTempPath(), $"deploy-{Guid.NewGuid():N}.tgz");
        try
        {
            using (var file = File.Create(path))
            using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.SmallestSize))
            using (var writer = new System.Formats.Tar.TarWriter(gzip, System.Formats.Tar.TarEntryFormat.Pax))
            {
                writer.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, "app/index.js"));
            }

            var analysis = DeployRules.AnalyzeFile(path);

            Assert.Equal("tar.gz", analysis.ArchiveType);
            Assert.Equal("app", analysis.ExtractedDirName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Analyzes_plain_tar_archive()
    {
        var path = Path.Combine(Path.GetTempPath(), $"deploy-{Guid.NewGuid():N}.tar");
        try
        {
            using (var file = File.Create(path))
            using (var writer = new System.Formats.Tar.TarWriter(file, System.Formats.Tar.TarEntryFormat.Pax))
            {
                writer.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, "app/main.go"));
            }

            var analysis = DeployRules.AnalyzeFile(path);

            Assert.Equal("tar", analysis.ArchiveType);
            Assert.Equal("app", analysis.ExtractedDirName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Corrupt_archive_still_reported_as_archive()
    {
        var path = Path.Combine(Path.GetTempPath(), $"deploy-{Guid.NewGuid():N}.zip");
        try
        {
            File.WriteAllText(path, "not a zip");

            var analysis = DeployRules.AnalyzeFile(path);

            Assert.Equal("archive", analysis.FileType);
            Assert.Empty(analysis.FilesInArchive);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Python_without_entry_file_uses_default_name()
    {
        var analysis = new DeployFileAnalysis("archive", "tar.gz", "app.tar.gz", ["demo/readme.txt"], "demo");

        var command = DeployRules.GenerateStartCommand("/tmp/app.tar.gz", "python", analysis, "demo", "app", 80);

        Assert.Contains("nohup python demo/app.py", command);
    }

    [Fact]
    public void Node_with_package_json_but_no_start_script_uses_npm_start()
    {
        var analysis = new DeployFileAnalysis("archive", "tar.gz", "app.tar.gz", ["demo/package.json"], "demo");

        var command = DeployRules.GenerateStartCommand("/tmp/app.tar.gz", "nodejs", analysis, "demo", "app", 80);

        Assert.Contains("nohup npm start", command);
        Assert.DoesNotContain("chmod +x", command);
    }

    [Fact]
    public void Go_without_binary_uses_archive_file_name()
    {
        var analysis = new DeployFileAnalysis("archive", "tar.gz", "service.tar.gz", ["demo/readme.md"], "demo");

        var command = DeployRules.GenerateStartCommand("/tmp/service.tar.gz", "golang", analysis, "demo", "app", 80);

        Assert.Contains("chmod +x demo/service", command);
    }

    [Fact]
    public void Non_archive_python_command_has_no_extraction_step()
    {
        var analysis = new DeployFileAnalysis("file", null, "main.py", [], null);

        var command = DeployRules.GenerateStartCommand("/tmp/main.py", "python", analysis, null, "app", 80);

        Assert.StartsWith("[ -f app.py ]", command);
        Assert.DoesNotContain("tar -xzf", command);
    }

    [Fact]
    public void Missing_file_is_unknown()
    {
        var analysis = DeployRules.AnalyzeFile("/definitely/missing/app.tar.gz");

        Assert.Equal("unknown", analysis.FileType);
    }

    [Fact]
    public void Go_binary_candidates_exclude_documentation_files()
    {
        var executables = DeployRules.FindExecutables(["demo/README", "demo/app"], "golang");

        Assert.Single(executables.GoBinaries);
        Assert.Equal("demo/app", executables.GoBinaries[0]);
    }

    [Fact]
    public void Docker_start_command_requires_archive_listing()
    {
        var analysis = new DeployFileAnalysis("file", null, "Dockerfile", [], null);

        Assert.Null(DeployRules.GenerateStartCommand("/tmp/Dockerfile", "docker", analysis, null, "web", 80));
    }

    [Fact]
    public void Docker_stop_uses_container_name()
    {
        Assert.Equal(
            "docker stop my-app 2>/dev/null || true && docker rm my-app 2>/dev/null || true",
            DeployRules.GenerateStopCommand("docker", "My_App"));
    }
}

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlibabaCloud.Ops.Mcp.Server.Auth;
using AlibabaCloud.Ops.Mcp.Server.Cloud;
using Aliyun.OSS;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AlibabaCloud.Ops.Mcp.Server.Tools;

/// <summary>代码部署编排：校验实例、上传产物、创建应用分组并发起 OOS 部署。</summary>
[McpServerToolType]
public sealed class CodeDeployTools(
    GenericApiCaller caller,
    ICredentialProvider credentialProvider,
    ILogger<CodeDeployTools> logger)
{
    private const string ManagementRegion = "cn-hangzhou";
    private static string? _projectRoot;

    private static readonly Dictionary<string, string> PackageMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["docker"] = "ACS-Extension-DockerCE-1853370294850618",
        ["java"] = "ACS-Extension-java-1853370294850618",
        ["python"] = "ACS-Extension-python-1853370294850618",
        ["nodejs"] = "ACS-Extension-node-1853370294850618",
        ["golang"] = "ACS-Extension-golang-1853370294850618",
        ["nginx"] = "ACS-Extension-nginx-1853370294850618",
        ["git"] = "ACS-Extension-Git-1853370294850618",
    };

    /// <summary>部署应用到目标实例组。</summary>
    [McpServerTool(Name = "OOS_CodeDeploy")]
    [Description("将应用部署到阿里云ECS实例。使用阿里云OOS（运维编排服务）的CodeDeploy功能实现自动化部署。")]
    public async Task<CallToolResult> CodeDeploy(
        [Description("name of the application")] string name,
        [Description("Region ID for deployment")] string deploy_region_id,
        [Description("name of the application group")] string application_group_name,
        [Description("OSS object name")] string object_name,
        [Description("Local file path to upload. If the file is not in .code_deploy/release directory, it will be copied there.")] string file_path,
        [Description("Deploy language, like:docker, java, python, nodejs, golang")] string deploy_language,
        [Description("Application listening port")] int port,
        [Description("Root path of the project. The .code_deploy directory will be created in this path.")] string? project_path = null,
        [Description("**OPTIONAL - DO NOT PROVIDE UNLESS USER EXPLICITLY REQUIRES IT** Application start command script.")] string? application_start = null,
        [Description("**OPTIONAL - DO NOT PROVIDE UNLESS USER EXPLICITLY REQUIRES IT** Application stop command script.")] string? application_stop = null,
        [Description("AlibabaCloud ECS instance ID List. If empty or not provided, user will be prompted to create ECS instances.")] List<string>? instance_ids = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var projectRoot = ResolveProjectRoot(project_path, file_path);
            _projectRoot = projectRoot;
            var releaseDirectory = Path.Combine(projectRoot, ".code_deploy", "release");
            Directory.CreateDirectory(releaseDirectory);

            if (instance_ids is null || instance_ids.Count == 0)
            {
                return ToolResults.Text(BuildInstanceRequired(deploy_region_id, port));
            }

            var existing = await DescribeInstancesAsync(deploy_region_id, instance_ids, cancellationToken);
            var missing = instance_ids.Where(id => !ContainsInstance(existing, id)).ToList();
            if (missing.Count > 0)
            {
                return ToolResults.Text(BuildInstanceNotFound(deploy_region_id, instance_ids, missing));
            }

            var sourcePath = Path.GetFullPath(file_path);
            if (!File.Exists(sourcePath))
            {
                return ToolResults.Error($"NotFound: {sourcePath}");
            }

            var releasePath = Path.Combine(releaseDirectory, Path.GetFileName(sourcePath));
            if (!string.Equals(sourcePath, Path.GetFullPath(releasePath), StringComparison.Ordinal))
            {
                File.Copy(sourcePath, releasePath, overwrite: true);
            }

            if (string.IsNullOrWhiteSpace(application_start) || string.IsNullOrWhiteSpace(application_stop))
            {
                var analysis = DeployRules.AnalyzeFile(releasePath);
                application_start ??= DeployRules.GenerateStartCommand(
                    releasePath, deploy_language, analysis, analysis.ExtractedDirName, name, port);
                application_stop ??= DeployRules.GenerateStopCommand(deploy_language, name);
            }

            if (string.IsNullOrWhiteSpace(application_start))
            {
                return ToolResults.Error("START_COMMAND_REQUIRED: provide application_start manually");
            }

            var bucket = await EnsureBucketAsync(name, cancellationToken);
            var versionId = Upload(bucket, object_name, releasePath);

            await EnsureApplicationAsync(name, cancellationToken);
            var groupExists = await ApplicationGroupExistsAsync(name, application_group_name, cancellationToken);
            if (!groupExists)
            {
                await CreateApplicationGroupAsync(name, application_group_name, deploy_region_id, cancellationToken);
            }

            await TagInstancesAsync(deploy_region_id, name, application_group_name, instance_ids, cancellationToken);

            JsonObject deployParameters;
            string? revisionId = null;
            if (groupExists)
            {
                revisionId = await CreateRevisionAsync(name, bucket, object_name, versionId, deploy_region_id,
                    application_start, application_stop, cancellationToken);
                deployParameters = new JsonObject
                {
                    ["StartExecutionParameters"] = JsonSerializer.Serialize(new
                    {
                        Parameters = JsonSerializer.Serialize(new
                        {
                            applicationName = name,
                            applicationGroupName = application_group_name,
                            deployRevisionId = revisionId,
                            deployMethod = "all",
                            batchNumber = 2,
                            batchPauseOption = "Automatic",
                        }),
                        Mode = "FailurePause",
                    }),
                };
            }
            else
            {
                deployParameters = BuildNewGroupParameters(name, application_group_name, bucket, object_name, versionId,
                    deploy_region_id, port, instance_ids, application_start, application_stop, deploy_language);
            }

            var deployArguments = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["RegionId"] = JsonSerializer.SerializeToElement(ManagementRegion),
                ["ApplicationName"] = JsonSerializer.SerializeToElement(name),
                ["Name"] = JsonSerializer.SerializeToElement(application_group_name),
                ["DeployParameters"] = JsonSerializer.SerializeToElement(deployParameters.ToJsonString()),
            };
            if (revisionId is not null)
            {
                deployArguments["RevisionId"] = JsonSerializer.SerializeToElement(revisionId);
            }

            var response = await caller.CallAsync("oos", "DeployApplicationGroup", deployArguments, cancellationToken);
            SaveApplicationInfo(projectRoot, name, application_group_name, deploy_region_id, port, instance_ids);

            var serviceLink = $"https://ecs.console.aliyun.com/app/detail?tabKey=overview&appName={name}&groupName={application_group_name}";
            var instanceId = instance_ids.FirstOrDefault();
            var securityGroupLink = instanceId is null
                ? $"https://ecs.console.aliyun.com/securityGroup/region/{deploy_region_id}"
                : $"https://ecs.console.aliyun.com/server/{instanceId}/group?regionId={deploy_region_id}#/";
            return ToolResults.Text(new JsonObject
            {
                ["response"] = response?.DeepClone(),
                ["service_link"] = serviceLink,
                ["security_group_link"] = securityGroupLink,
                ["port"] = port,
                ["deploy_region_id"] = deploy_region_id,
                ["bucket_name"] = bucket,
                ["oss_bucket_link"] = $"https://oss.console.aliyun.com/bucket/oss-cn-hangzhou/{bucket}/object",
                ["security_group_instructions"] = SecurityGroupInstructions(port, serviceLink, securityGroupLink),
            });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "CodeDeploy failed for {Application}", name);
            return ToolResults.Error(exception);
        }
    }

    /// <summary>查询应用分组与部署状态。</summary>

    [McpServerTool(Name = "OOS_GetDeployStatus")]
    [Description("查询应用分组的部署状态")]
    public async Task<CallToolResult> GetDeployStatus(
        [Description("name of the application")] string name,
        [Description("name of the application group")] string application_group_name,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var arguments = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["RegionId"] = JsonSerializer.SerializeToElement(ManagementRegion),
                ["ApplicationName"] = JsonSerializer.SerializeToElement(name),
                ["Name"] = JsonSerializer.SerializeToElement(application_group_name),
            };
            var response = await caller.CallAsync("oos", "GetApplicationGroup", arguments, cancellationToken);
            var body = ToolResults.Find(response, "body");
            var group = ToolResults.Find(body, "ApplicationGroup");
            var status = ToolResults.Find(group, "Status")?.GetValue<string>();
            var executionId = ToolResults.Find(group, "ExecutionId")?.GetValue<string>();

            JsonNode? executionInfo = null;
            if (!string.IsNullOrWhiteSpace(executionId))
            {
                try
                {
                    var pollArguments = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["RegionId"] = JsonSerializer.SerializeToElement(ManagementRegion),
                        ["ExecutionId"] = JsonSerializer.SerializeToElement(executionId),
                    };
                    var executions = await caller.CallAsync("oos", "ListExecutions", pollArguments, cancellationToken);
                    executionInfo = OosExecutor.Body(executions)?.DeepClone();
                }
                catch (Exception exception)
                {
                    logger.LogDebug("ListExecutions failed for {ExecutionId}: {Message}", executionId, exception.Message);
                }
            }

            return ToolResults.Text(new JsonObject
            {
                ["info"] = body?.DeepClone(),
                ["status"] = status,
                ["execution_id"] = executionId,
                ["deploy_execution_info"] = executionInfo,
            });
        }
        catch (Exception exception)
        {
            return ToolResults.Error(exception);
        }
    }

    /// <summary>查询最近一次部署的执行信息。</summary>

    [McpServerTool(Name = "OOS_GetLastDeploymentInfo")]
    [Description("获取上次部署的应用信息")]
    public Task<CallToolResult> GetLastDeploymentInfo(
        [Description("Optional random string, unused")] string? random_string = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var root = _projectRoot ?? Directory.GetCurrentDirectory();
            var statePath = Path.Combine(root, ".code_deploy", "application.json");
            if (!File.Exists(statePath))
            {
                return Task.FromResult(ToolResults.Text(new JsonObject
                {
                    ["message"] = "No information found about the last deployment",
                    ["info"] = new JsonObject(),
                }));
            }

            var info = JsonNode.Parse(File.ReadAllText(statePath))?["last_deployment"]?.DeepClone() ?? new JsonObject();
            return Task.FromResult(ToolResults.Text(new JsonObject
            {
                ["message"] = "Successfully retrieved last deployment information",
                ["info"] = info,
            }));
        }
        catch (Exception exception)
        {
            return Task.FromResult(ToolResults.Error(exception));
        }
    }

    /// <summary>调用 ECS DescribeInstances 查询原始响应。</summary>
    private async Task<JsonNode?> DescribeInstancesCoreAsync(string regionId, IReadOnlyList<string> instanceIds, CancellationToken cancellationToken)
    {
        var arguments = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["RegionId"] = JsonSerializer.SerializeToElement(regionId),
            ["InstanceIds"] = JsonSerializer.SerializeToElement(JsonSerializer.Serialize(instanceIds)),
        };
        return await caller.CallAsync("ecs", "DescribeInstances", arguments, cancellationToken);
    }

    /// <summary>查询实例列表，供归属校验使用。</summary>
    private async Task<JsonNode?> DescribeInstancesAsync(string regionId, IReadOnlyList<string> instanceIds, CancellationToken cancellationToken) =>
        await DescribeInstancesCoreAsync(regionId, instanceIds, cancellationToken);

    /// <summary>在响应文本中判断实例 ID 是否存在。</summary>
    private static bool ContainsInstance(JsonNode? response, string instanceId) =>
        response?.ToJsonString().Contains(instanceId, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>确保确定性命名的部署 Bucket 存在并返回名称。</summary>
    private async Task<string> EnsureBucketAsync(string applicationName, CancellationToken cancellationToken)
    {
        var credentials = credentialProvider.Resolve();
        var client = new OssClient("https://oss-cn-hangzhou.aliyuncs.com",
            credentials.AccessKeyId, credentials.AccessKeySecret, credentials.SecurityToken);

        var normalized = new string(applicationName.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        var bucket = $"app-{normalized}-code-deploy";
        if (bucket.Length > 63)
        {
            bucket = bucket[..63];
        }

        if (!client.DoesBucketExist(bucket))
        {
            client.CreateBucket(bucket);
            logger.LogInformation("Created OSS bucket {Bucket}", bucket);
        }

        return bucket;
    }

    /// <summary>上传产物到 OSS，返回版本号。</summary>
    private string Upload(string bucket, string objectName, string filePath)
    {
        var credentials = credentialProvider.Resolve();
        var client = new OssClient("https://oss-cn-hangzhou.aliyuncs.com",
            credentials.AccessKeyId, credentials.AccessKeySecret, credentials.SecurityToken);
        using var stream = File.OpenRead(filePath);
        var result = client.PutObject(bucket, objectName, stream);
        return result.VersionId ?? string.Empty;
    }

    /// <summary>应用不存在时创建。</summary>
    private async Task EnsureApplicationAsync(string name, CancellationToken cancellationToken)
    {
        if (await ExistsAsync("GetApplication", name, null, cancellationToken))
        {
            return;
        }

        var arguments = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["RegionId"] = JsonSerializer.SerializeToElement(ManagementRegion),
            ["Name"] = JsonSerializer.SerializeToElement(name),
        };
        await caller.CallAsync("oos", "CreateApplication", arguments, cancellationToken);
    }

    /// <summary>判断应用分组是否已存在。</summary>
    private async Task<bool> ApplicationGroupExistsAsync(string name, string groupName, CancellationToken cancellationToken) =>
        await ExistsAsync("GetApplicationGroup", name, groupName, cancellationToken);

    /// <summary>以 Get 接口探测资源是否存在，云端报错视为不存在。</summary>
    private async Task<bool> ExistsAsync(string api, string name, string? groupName, CancellationToken cancellationToken)
    {
        try
        {
            var arguments = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["RegionId"] = JsonSerializer.SerializeToElement(ManagementRegion),
                ["ApplicationName"] = JsonSerializer.SerializeToElement(name),
                ["Name"] = JsonSerializer.SerializeToElement(groupName ?? name),
            };
            await caller.CallAsync("oos", api, arguments, cancellationToken);
            return true;
        }
        catch (CloudApiException exception) when (IsMissingEntity(exception))
        {
            return false;
        }
    }

    /// <summary>仅把 EntityNotExists.* 视为不存在，其余云端错误继续上抛。</summary>
    internal static bool IsMissingEntity(CloudApiException exception) =>
        exception.Message.Contains("EntityNotExists", StringComparison.OrdinalIgnoreCase);

    /// <summary>创建应用分组并指定部署地域。</summary>
    private async Task CreateApplicationGroupAsync(string name, string groupName, string deployRegion, CancellationToken cancellationToken)
    {
        var arguments = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["RegionId"] = JsonSerializer.SerializeToElement(ManagementRegion),
            ["ApplicationName"] = JsonSerializer.SerializeToElement(name),
            ["Name"] = JsonSerializer.SerializeToElement(groupName),
            ["DeployRegionId"] = JsonSerializer.SerializeToElement(deployRegion),
        };
        await caller.CallAsync("oos", "CreateApplicationGroup", arguments, cancellationToken);
    }

    /// <summary>按应用/分组给实例打标，供分组识别。</summary>
    private async Task TagInstancesAsync(
        string regionId, string name, string groupName, IReadOnlyList<string> instanceIds, CancellationToken cancellationToken)
    {
        var arguments = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["RegionId"] = JsonSerializer.SerializeToElement(regionId),
            ["ResourceType"] = JsonSerializer.SerializeToElement("Instance"),
            ["Tag.1.Key"] = JsonSerializer.SerializeToElement($"app-{name}"),
            ["Tag.1.Value"] = JsonSerializer.SerializeToElement(groupName),
        };

        for (var i = 0; i < instanceIds.Count; i++)
        {
            arguments[$"ResourceId.{i + 1}"] = JsonSerializer.SerializeToElement(instanceIds[i]);
        }

        await caller.CallAsync("ecs", "TagResources", arguments, cancellationToken);
    }

    /// <summary>为已有分组创建 OSS 部署版本，返回 RevisionId。</summary>
    private async Task<string> CreateRevisionAsync(
        string name, string bucket, string objectName, string versionId, string deployRegion,
        string applicationStart, string applicationStop, CancellationToken cancellationToken)
    {
        var location = JsonSerializer.Serialize(new
        {
            regionId = ManagementRegion,
            bucketName = bucket,
            objectName,
            versionId,
            isInternal = deployRegion.Equals(ManagementRegion, StringComparison.OrdinalIgnoreCase) ? "true" : "false",
        });
        var hooks = JsonSerializer.Serialize(new
        {
            workingDir = "/root",
            applicationStart,
            applicationStop,
        });

        var arguments = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["RegionId"] = JsonSerializer.SerializeToElement(ManagementRegion),
            ["ApplicationName"] = JsonSerializer.SerializeToElement(name),
            ["Description"] = JsonSerializer.SerializeToElement(string.Empty),
            ["RevisionType"] = JsonSerializer.SerializeToElement("Oss"),
            ["Location"] = JsonSerializer.SerializeToElement(location),
            ["Hooks"] = JsonSerializer.SerializeToElement(hooks),
        };
        var response = await caller.CallAsync("oos", "CreateDeployRevision", arguments, cancellationToken);
        return ToolResults.Find(response, "RevisionId")?.GetValue<string>() ?? string.Empty;
    }

    /// <summary>生成新分组 oss-revision 模板所需参数。</summary>
    private static JsonObject BuildNewGroupParameters(
        string name, string groupName, string bucket, string objectName, string versionId,
        string deployRegion, int port, IReadOnlyList<string> instanceIds,
        string applicationStart, string applicationStop, string deployLanguage)
    {
        var packageName = PackageMap.TryGetValue(deployLanguage, out var mapped)
            ? mapped
            : PackageMap["docker"];

        return new JsonObject
        {
            ["Parameters"] = new JsonObject
            {
                ["CreateEcsOption"] = instanceIds.Count > 0 ? "ExistECS" : "NewECS",
                ["InstanceId"] = instanceIds.FirstOrDefault(),
                ["ApplicationName"] = name,
                ["Description"] = string.Empty,
                ["ZoneId"] = "cn-hangzhou-b",
                ["Port"] = port,
                ["RevisionType"] = "Oss",
                ["RegionIdOSS"] = ManagementRegion,
                ["BucketName"] = bucket,
                ["ObjectName"] = objectName,
                ["VersionId"] = versionId,
                ["IsInternalOSS"] = deployRegion.Equals(ManagementRegion, StringComparison.OrdinalIgnoreCase),
                ["ApplicationGroupName"] = groupName,
                ["WorkingDir"] = "/root",
                ["ApplicationStart"] = applicationStart,
                ["ApplicationStop"] = applicationStop,
                ["PackageName"] = packageName,
            },
            ["TemplateName"] = "oss-revision",
            ["ServiceId"] = "service-af8acc2d6f4044f4b5ea",
        };
    }

    /// <summary>未提供实例时返回引导用户创建实例的提示。</summary>
    private static JsonObject BuildInstanceRequired(string regionId, int port)
    {
        var ecsLink = $"https://ecs-buy.aliyun.com/ecs#/custom/prepay/{regionId}?orderSource=buyWizard-console-list";
        var securityGroupLink = $"https://ecs.console.aliyun.com/securityGroup?regionId={regionId}";
        var portInfo = port != 0 ? $"port {port}" : "application port";
        return new JsonObject
        {
            ["error"] = "ECS_INSTANCE_REQUIRED",
            ["message"] = "ECS instance ID not provided. Please create ECS instances first before deployment.",
            ["region_id"] = regionId,
            ["ecs_purchase_link"] = ecsLink,
            ["security_group_link"] = securityGroupLink,
            ["instructions"] = $"""
                ## ECS Instance Creation Required

                **Deployment Region**: {regionId}

                ### Step 1: Create ECS Instances
                Please visit the following link to create ECS instances:
                [{ecsLink}]({ecsLink})

                After creation, please provide the ECS instance ID list.

                ### Step 2: Configure Security Group (Post-deployment Operation)
                After deployment, you need to open {portInfo} for the ECS instance's security group. Please visit:
                [{securityGroupLink}]({securityGroupLink})

                Add inbound rules in the security group rules:
                - Port range: {port}/{port} (if port is specified)
                - Protocol type: TCP
                - Authorized object: 0.0.0.0/0 (or restrict access source as needed)
                """,
        };
    }

    /// <summary>返回实例归属校验失败明细。</summary>
    private static JsonObject BuildInstanceNotFound(string regionId, IReadOnlyList<string> provided, IReadOnlyList<string> missing) => new()
    {
        ["error"] = "ECS_INSTANCE_NOT_FOUND",
        ["message"] = $"Some ECS instances do not exist in region {regionId}.",
        ["region_id"] = regionId,
        ["missing_instance_ids"] = new JsonArray(missing.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
        ["provided_instance_ids"] = new JsonArray(provided.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
        ["instructions"] = $"""
            ## ECS Instance Validation Failed

            **Deployment Region**: {regionId}

            **Missing Instance IDs**: {string.Join(", ", missing)}

            **All Provided Instance IDs**: {string.Join(", ", provided)}

            Please verify that:
            1. The instance IDs are correct
            2. The instances exist in region {regionId}
            3. You have permission to access these instances

            You can check your instances at:
            https://ecs.console.aliyun.com/?regionId={regionId}#/server/instance
            """,
    };

    /// <summary>生成部署成功后的安全组配置指引。</summary>
    private static string SecurityGroupInstructions(int port, string serviceLink, string securityGroupLink) => $"""
        ## Deployment Successful!

        **Service Link**: [View Deployment Details]({serviceLink})

        ### Important: Configure Security Group Rules

        After the application is deployed, you need to open port **{port}** for the ECS instance's security group, otherwise the application will not be accessible from outside.

        **Security Group Management Link**: [{securityGroupLink}]({securityGroupLink})

        **Configuration Steps**:
        1. Visit the security group management link above
        2. Find the security group to which your ECS instance belongs
        3. Click "Configure Rules" → "Add Security Group Rule"
        4. Configure inbound rule:
           - **Port range**: {port}/{port}
           - **Protocol type**: TCP
           - **Authorized object**: 0.0.0.0/0 (allow all sources, or restrict access source as needed)
           - **Description**: Application port {port}

        After configuration, the application can be accessed via the ECS instance's public IP and port {port}.
        """;

    /// <summary>推断项目根目录，默认取产物所在目录。</summary>
    private static string ResolveProjectRoot(string? projectPath, string filePath)
    {
        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            return Path.GetFullPath(projectPath);
        }

        var current = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        var indicators = new[] { "package.json", "pom.xml", "requirements.txt", "go.mod", "Cargo.toml", ".git" };
        for (var i = 0; i < 5 && current is not null; i++)
        {
            if (indicators.Any(indicator => File.Exists(Path.Combine(current.FullName, indicator))
                                           || Directory.Exists(Path.Combine(current.FullName, indicator))))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Path.GetDirectoryName(Path.GetFullPath(filePath))!;
    }

    /// <summary>把应用与分组信息写入 .code_deploy/application.json。</summary>
    private static void SaveApplicationInfo(
        string projectRoot, string name, string groupName, string deployRegion, int port, IReadOnlyList<string> instanceIds)
    {
        var directory = Path.Combine(projectRoot, ".code_deploy");
        Directory.CreateDirectory(directory);
        var payload = new JsonObject
        {
            ["last_deployment"] = new JsonObject
            {
                ["application_name"] = name,
                ["application_group_name"] = groupName,
                ["deploy_region_id"] = deployRegion,
                ["port"] = port,
                ["instance_ids"] = new JsonArray(instanceIds.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                ["deploy_time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            },
        };
        File.WriteAllText(Path.Combine(directory, "application.json"), payload.ToJsonString());
    }
}

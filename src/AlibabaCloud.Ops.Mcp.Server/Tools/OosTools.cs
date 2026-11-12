using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlibabaCloud.Ops.Mcp.Server.Cloud;
using AlibabaCloud.Ops.Mcp.Server.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AlibabaCloud.Ops.Mcp.Server.Tools;

/// <summary>通过 OOS 模板执行 ECS/RDS 常用运维操作。</summary>
[McpServerToolType]
public sealed class OosTools(OosExecutor executor, ServerOptions options)
{

    /// <summary>通过云助手在 ECS 实例执行命令。</summary>

    [McpServerTool(Name = "OOS_RunCommand")]
    [Description("批量在多台ECS实例上运行云助手命令，适用于需要同时管理多台ECS实例的场景，如应用程序管理和资源标记操作等。")]
    public Task<CallToolResult> RunCommand(
        [Description("Content of the command executed on the ECS instance")] string Command,
        [Description("AlibabaCloud ECS instance ID List")] List<string> InstanceIds,
        [Description("AlibabaCloud region ID")] string RegionId = "cn-hangzhou",
        [Description("The type of command executed on the ECS instance, optional value：RunShellScript，RunPythonScript，RunPerlScript，RunBatScript，RunPowerShellScript")] string CommandType = "RunShellScript",
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(RegionId, "ACS-ECS-BulkyRunCommand", new JsonObject
        {
            ["regionId"] = RegionId,
            ["resourceType"] = "ALIYUN::ECS::Instance",
            ["targets"] = new JsonObject
            {
                ["ResourceIds"] = ToArray(InstanceIds),
                ["RegionId"] = RegionId,
                ["Type"] = "ResourceIds",
                ["Parameters"] = new JsonObject { ["RegionId"] = RegionId, ["Status"] = "Running" },
            },
            ["commandType"] = CommandType,
            ["commandContent"] = Command,
        }, cancellationToken);

    /// <summary>批量启动 ECS 实例。</summary>

    [McpServerTool(Name = "OOS_StartInstances")]
    [Description("批量启动ECS实例，适用于需要同时管理和启动多台ECS实例的场景，例如应用部署和高可用性场景。")]
    public Task<CallToolResult> StartInstances(
        [Description("AlibabaCloud ECS instance ID List")] List<string> InstanceIds,
        [Description("AlibabaCloud region ID")] string RegionId = "cn-hangzhou",
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(RegionId, "ACS-ECS-BulkyStartInstances", Targeted(RegionId, InstanceIds), cancellationToken);

    /// <summary>批量停止 ECS 实例。</summary>

    [McpServerTool(Name = "OOS_StopInstances")]
    [Description("批量停止ECS实例，适用于需要同时管理和停止多台ECS实例的场景。")]
    public Task<CallToolResult> StopInstances(
        [Description("AlibabaCloud ECS instance ID List")] List<string> InstanceIds,
        [Description("AlibabaCloud region ID")] string RegionId = "cn-hangzhou",
        [Description("Is forced shutdown required")] bool ForeceStop = false,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(RegionId, "ACS-ECS-BulkyStopInstances",
            Targeted(RegionId, InstanceIds, ("forceStop", ForeceStop)), cancellationToken);

    /// <summary>批量重启 ECS 实例。</summary>

    [McpServerTool(Name = "OOS_RebootInstances")]
    [Description("批量重启ECS实例，适用于需要同时管理和重启多台ECS实例的场景。")]
    public Task<CallToolResult> RebootInstances(
        [Description("AlibabaCloud ECS instance ID List")] List<string> InstanceIds,
        [Description("AlibabaCloud region ID")] string RegionId = "cn-hangzhou",
        [Description("Is forced shutdown required")] bool ForeceStop = false,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(RegionId, "ACS-ECS-BulkyRebootInstances",
            Targeted(RegionId, InstanceIds, ("forceStop", ForeceStop)), cancellationToken);

    /// <summary>批量创建 ECS 实例。</summary>

    [McpServerTool(Name = "OOS_RunInstances")]
    [Description("批量创建ECS实例，适用于需要同时创建多台ECS实例的场景，例如应用部署和高可用性场景。")]
    public Task<CallToolResult> RunInstances(
        [Description("Image ID")] string ImageId,
        [Description("Instance Type")] string InstanceType,
        [Description("SecurityGroup ID")] string SecurityGroupId,
        [Description("VSwitch ID")] string VSwitchId,
        [Description("AlibabaCloud region ID")] string RegionId = "cn-hangzhou",
        [Description("The maximum outbound bandwidth of the instance. Unit: Mbit/s. Valid values: 0 to 100. If you want to open the public network for the ECS instance, you need to set a value > 0")] int InternetMaxBandwidthOut = 0,
        [Description("Number of ECS instances")] int Amount = 1,
        [Description("Instance Name")] string InstanceName = "",
        [Description("The category of the system disk. Valid values: cloud_efficiency, cloud_ssd, cloud_essd, cloud_auto, cloud, cloud_essd_entry")] string SystemDiskCategory = "cloud_essd",
        [Description("The size of the system disk. Unit: GiB. Valid values: 20 to 500. Default value: 40 or the image size, whichever is greater")] string SystemDiskSize = "",
        [Description("The name of the system disk")] string SystemDiskName = "",
        [Description("The description of the system disk")] string SystemDiskDescription = "",
        [Description("The performance level of the ESSD used as the system disk. Valid values: PL0, PL1, PL2, PL3")] string SystemDiskPerformanceLevel = "PL1",
        [Description("The private IP address of the instance. For VPC type ECS instances, the private IP address must be selected from the available IP range of the VSwitch")] string PrivateIpAddress = "",
        [Description("The ID of the automatic snapshot policy to apply to the system disk")] string SystemDiskAutoSnapshotPolicyId = "",
        [Description("Data disk configuration in JSON format. Example: [{\"Size\":\"100\",\"DiskName\":\"data-disk-1\",\"Description\":\"\",\"Category\":\"cloud_essd\",\"PerformanceLevel\":\"PL1\",\"AutoSnapshotPolicyId\":\"\"}]")] string DataDiskParameters = "",
        [Description("The tag of an ECS instance, for example: [{\"Key\":\"oos\",\"Value\":\"test\"}]")] List<TagItem>? Tags = null,
        [Description("Resource group ID")] string ResourceGroupId = "",
        [Description("The description of the ECS instances")] string Description = "",
        [Description("The host name of the ECS instance")] string HostName = "",
        [Description("The ID of the zone where the ECS instances are deployed")] string ZoneId = "",
        CancellationToken cancellationToken = default)
    {
        var parameters = new JsonObject
        {
            ["regionId"] = RegionId,
            ["imageId"] = ImageId,
            ["instanceType"] = InstanceType,
            ["securityGroupId"] = SecurityGroupId,
            ["vSwitchId"] = VSwitchId,
            ["internetMaxBandwidthOut"] = InternetMaxBandwidthOut,
            ["amount"] = Amount,
            ["instanceName"] = InstanceName,
        };

        AddIfSet(parameters, "systemDiskCategory", SystemDiskCategory, "cloud_essd");
        AddIfSet(parameters, "systemDiskSize", SystemDiskSize);
        AddIfSet(parameters, "systemDiskName", SystemDiskName);
        AddIfSet(parameters, "systemDiskDescription", SystemDiskDescription);
        AddIfSet(parameters, "systemDiskPerformanceLevel", SystemDiskPerformanceLevel, "PL1");
        AddIfSet(parameters, "privateIpAddress", PrivateIpAddress);
        AddIfSet(parameters, "systemDiskAutoSnapshotPolicyId", SystemDiskAutoSnapshotPolicyId);
        AddIfSet(parameters, "resourceGroupId", ResourceGroupId);
        AddIfSet(parameters, "description", Description);
        AddIfSet(parameters, "hostName", HostName);
        AddIfSet(parameters, "zoneId", ZoneId);

        if (Tags is { Count: > 0 })
        {
            parameters["tags"] = JsonSerializer.SerializeToNode(Tags);
        }

        if (!string.IsNullOrWhiteSpace(DataDiskParameters))
        {
            try
            {
                using var document = JsonDocument.Parse(DataDiskParameters);
                if (document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() > 0)
                {
                    parameters["dataDiskParameters"] = JsonNode.Parse(DataDiskParameters);
                }
            }
            catch (JsonException)
            {
            }
        }

        return ExecuteAsync(RegionId, "ACS-ECS-RunInstances", parameters, cancellationToken);
    }

    /// <summary>重置 ECS 实例登录密码。</summary>

    [McpServerTool(Name = "OOS_ResetPassword")]
    [Description("批量修改ECS实例的密码，请注意，本操作将会重启ECS实例")]
    public Task<CallToolResult> ResetPassword(
        [Description("AlibabaCloud ECS instance ID List")] List<string> InstanceIds,
        [Description("The password of the ECS instance must be 8-30 characters and must contain only the following characters: lowercase letters, uppercase letters, numbers, and special characters only.（）~！@#$%^&*-_+=（40：<>，？/")] string Password,
        [Description("AlibabaCloud region ID")] string RegionId = "cn-hangzhou",
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(RegionId, "ACS-ECS-BulkyResetPassword",
            Targeted(RegionId, InstanceIds, ("password", Password)), cancellationToken);

    /// <summary>更换 ECS 实例系统盘。</summary>

    [McpServerTool(Name = "OOS_ReplaceSystemDisk")]
    [Description("批量替换ECS实例的系统盘，更换操作系统")]
    public Task<CallToolResult> ReplaceSystemDisk(
        [Description("AlibabaCloud ECS instance ID List")] List<string> InstanceIds,
        [Description("Image ID")] string ImageId,
        [Description("AlibabaCloud region ID")] string RegionId = "cn-hangzhou",
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(RegionId, "ACS-ECS-BulkyReplaceSystemDisk",
            Targeted(RegionId, InstanceIds, ("imageId", ImageId)), cancellationToken);

    /// <summary>批量启动 RDS 实例。</summary>

    [McpServerTool(Name = "OOS_StartRDSInstances")]
    [Description("批量启动RDS实例，适用于需要同时管理和启动多台RDS实例的场景，例如应用部署和高可用性场景。")]
    public Task<CallToolResult> StartRdsInstances(
        [Description("AlibabaCloud ECS instance ID List")] List<string> InstanceIds,
        [Description("AlibabaCloud region ID")] string RegionId = "cn-hangzhou",
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(RegionId, "ACS-RDS-BulkyStartInstances", Targeted(RegionId, InstanceIds, resourceType: "ALIYUN::RDS::Instance"), cancellationToken);

    /// <summary>批量停止 RDS 实例。</summary>

    [McpServerTool(Name = "OOS_StopRDSInstances")]
    [Description("批量停止RDS实例，适用于需要同时管理和停止多台RDS实例的场景。")]
    public Task<CallToolResult> StopRdsInstances(
        [Description("AlibabaCloud RDS instance ID List")] List<string> InstanceIds,
        [Description("AlibabaCloud region ID")] string RegionId = "cn-hangzhou",
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(RegionId, "ACS-RDS-BulkyStopInstances", Targeted(RegionId, InstanceIds, resourceType: "ALIYUN::RDS::Instance"), cancellationToken);

    /// <summary>批量重启 RDS 实例。</summary>

    [McpServerTool(Name = "OOS_RebootRDSInstances")]
    [Description("批量重启RDS实例，适用于需要同时管理和重启多台RDS实例的场景。")]
    public Task<CallToolResult> RebootRdsInstances(
        [Description("AlibabaCloud RDS instance ID List")] List<string> InstanceIds,
        [Description("AlibabaCloud region ID")] string RegionId = "cn-hangzhou",
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(RegionId, "ACS-RDS-BulkyRestartInstances", Targeted(RegionId, InstanceIds, resourceType: "ALIYUN::RDS::Instance"), cancellationToken);

    /// <summary>启动模板执行并把成功或失败转成工具结果。</summary>
    private async Task<CallToolResult> ExecuteAsync(
        string regionId,
        string templateName,
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await executor.StartExecutionAsync(
                regionId, templateName, parameters, TimeSpan.FromSeconds(options.OosTimeoutSeconds), cancellationToken);
            return ToolResults.Text(result);
        }
        catch (Exception exception)
        {
            return ToolResults.Error(exception);
        }
    }

    /// <summary>组装 RegionId 与目标实例列表参数。</summary>
    private static JsonObject Targeted(
        string regionId,
        IReadOnlyList<string> instanceIds,
        (string Key, object Value)? extra = null,
        string resourceType = "ALIYUN::ECS::Instance")
    {
        var parameters = new JsonObject
        {
            ["regionId"] = regionId,
            ["resourceType"] = resourceType,
            ["targets"] = new JsonObject
            {
                ["ResourceIds"] = ToArray(instanceIds),
                ["RegionId"] = regionId,
                ["Type"] = "ResourceIds",
            },
        };

        if (extra is { } pair)
        {
            parameters[pair.Key] = JsonSerializer.SerializeToNode(pair.Value);
        }

        return parameters;
    }

    /// <summary>字符串集合转 JsonArray。</summary>
    private static JsonArray ToArray(IEnumerable<string> values) =>
        new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    /// <summary>值非空白时写入可选参数，空白时回落到上游签名默认值。</summary>
    private static void AddIfSet(JsonObject target, string name, string value, string? fallback = null)
    {
        var effective = string.IsNullOrWhiteSpace(value) ? fallback : value;
        if (!string.IsNullOrWhiteSpace(effective))
        {
            target[name] = effective;
        }
    }
}

/// <summary>实例标签键值对。</summary>
public sealed record TagItem(string Key, string Value);

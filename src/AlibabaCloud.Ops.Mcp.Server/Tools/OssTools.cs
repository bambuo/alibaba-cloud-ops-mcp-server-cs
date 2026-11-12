using System.ComponentModel;
using System.Text.Json.Nodes;
using AlibabaCloud.Ops.Mcp.Server.Auth;
using Aliyun.OSS;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AlibabaCloud.Ops.Mcp.Server.Tools;

/// <summary>OSS Bucket 与对象管理工具。</summary>
[McpServerToolType]
public sealed class OssTools(ICredentialProvider credentialProvider)
{
    /// <summary>列出地域内 Bucket，可按前缀过滤。</summary>
    [McpServerTool(Name = "OSS_ListBuckets")]
    [Description("列出指定区域的所有OSS存储空间。")]
    public Task<CallToolResult> ListBuckets(
        [Description("AlibabaCloud region ID")] string RegionId = "cn-hangzhou",
        [Description("AlibabaCloud OSS Bucket Name prefix")] string? Prefix = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(RegionId, client =>
        {
            var result = client.ListBuckets();
            var buckets = new JsonArray();
            foreach (var bucket in result)
            {
                if (!string.IsNullOrEmpty(Prefix)
                    && !bucket.Name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                buckets.Add(new JsonObject
                {
                    ["name"] = bucket.Name,
                    ["creation_date"] = bucket.CreationDate.ToString("O"),
                    ["location"] = bucket.Location,
                });
            }

            return buckets;
        });

    /// <summary>分页列出 Bucket 内对象。</summary>

    [McpServerTool(Name = "OSS_ListObjects")]
    [Description("获取指定OSS存储空间中的所有文件信息。")]
    public Task<CallToolResult> ListObjects(
        [Description("AlibabaCloud OSS Bucket Name")] string BucketName,
        [Description("AlibabaCloud region ID")] string RegionId = "cn-hangzhou",
        [Description("AlibabaCloud OSS Bucket Name prefix")] string? Prefix = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(RegionId, client =>
        {
            if (string.IsNullOrWhiteSpace(BucketName))
            {
                throw new ArgumentException("Bucket name is required");
            }

            var objects = new JsonArray();
            string? marker = null;
            do
            {
                var listing = client.ListObjects(new ListObjectsRequest(BucketName)
                {
                    Prefix = Prefix,
                    Marker = marker,
                });

                foreach (var item in listing.ObjectSummaries)
                {
                    objects.Add(new JsonObject
                    {
                        ["key"] = item.Key,
                        ["size"] = item.Size,
                        ["last_modified"] = item.LastModified.ToString("O"),
                        ["etag"] = item.ETag,
                        ["storage_class"] = item.StorageClass,
                    });
                }

                marker = listing.IsTruncated ? listing.NextMarker : null;
            }
            while (!string.IsNullOrEmpty(marker));

            return objects;
        });

    /// <summary>创建 Bucket，可指定存储类型与冗余。</summary>

    [McpServerTool(Name = "OSS_PutBucket")]
    [Description("创建一个新的OSS存储空间。")]
    public Task<CallToolResult> PutBucket(
        [Description("AlibabaCloud OSS Bucket Name")] string BucketName,
        [Description("AlibabaCloud region ID")] string RegionId = "cn-hangzhou",
        [Description("The Storage Type of AlibabaCloud OSS Bucket: Standard, IA, Archive, ColdArchive, DeepColdArchive")] string StorageClass = "Standard",
        [Description("The data disaster recovery type: LRS or ZRS")] string DataRedundancyType = "LRS",
        CancellationToken cancellationToken = default) =>
        RunAsync(RegionId, client =>
        {
            var request = new CreateBucketRequest(BucketName)
            {
                StorageClass = Enum.TryParse<Aliyun.OSS.StorageClass>(StorageClass, true, out var storage)
                    ? storage
                    : Aliyun.OSS.StorageClass.Standard,
                DataRedundancyType = Enum.TryParse<Aliyun.OSS.DataRedundancyType>(DataRedundancyType, true, out var redundancy)
                    ? redundancy
                    : Aliyun.OSS.DataRedundancyType.LRS,
            };

            var bucket = client.CreateBucket(request);
            return new JsonObject
            {
                ["name"] = bucket.Name,
                ["location"] = bucket.Location,
                ["creation_date"] = bucket.CreationDate.ToString("O"),
            };
        });

    /// <summary>删除指定 Bucket。</summary>

    [McpServerTool(Name = "OSS_DeleteBucket")]
    [Description("删除指定的OSS存储空间。")]
    public Task<CallToolResult> DeleteBucket(
        [Description("AlibabaCloud OSS Bucket Name")] string BucketName,
        [Description("AlibabaCloud region ID")] string RegionId = "cn-hangzhou",
        CancellationToken cancellationToken = default) =>
        RunAsync(RegionId, client =>
        {
            client.DeleteBucket(BucketName);
            return new JsonObject { ["bucket"] = BucketName, ["deleted"] = true };
        });

    /// <summary>解析凭证、构造区域端点并执行操作，异常转错误结果。</summary>
    private Task<CallToolResult> RunAsync(string regionId, Func<OssClient, JsonNode?> action)
    {
        try
        {
            var credentials = credentialProvider.Resolve();
            var endpoint = $"https://oss-{regionId.ToLowerInvariant()}.aliyuncs.com";
            var client = new OssClient(endpoint, credentials.AccessKeyId, credentials.AccessKeySecret, credentials.SecurityToken);
            return Task.FromResult(ToolResults.Text(action(client)));
        }
        catch (Exception exception)
        {
            return Task.FromResult(ToolResults.Error(exception));
        }
    }
}

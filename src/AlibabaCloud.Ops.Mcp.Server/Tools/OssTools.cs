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
            var buckets = new JsonArray();
            string? marker = null;
            do
            {
                var request = new ListBucketsRequest { Prefix = Prefix, Marker = marker };
                var page = client.ListBuckets(request);
                foreach (var bucket in page.Buckets)
                {
                    // .NET SDK 的 ListBuckets 只解析 name/location/creation_date，其余字段按桶补齐
                    var entry = TryGetBucketInfo(client, bucket.Name);
                    buckets.Add(new JsonObject
                    {
                        ["name"] = bucket.Name,
                        ["location"] = bucket.Location,
                        ["creation_date"] = UtcText(bucket.CreationDate),
                        ["storage_class"] = entry is null ? null : entry.StorageClass.ToString(),
                        ["extranet_endpoint"] = entry?.ExtranetEndpoint,
                        ["intranet_endpoint"] = entry?.IntranetEndpoint,
                        ["region"] = entry?.Location?.Replace("oss-", string.Empty),
                        ["resource_group_id"] = null,
                    });
                }

                marker = page.IsTruncated == true ? page.NextMaker : null;
            }
            while (!string.IsNullOrEmpty(marker));

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
                        ["object_type"] = null,
                        ["size"] = item.Size,
                        ["etag"] = item.ETag,
                        ["last_modified"] = UtcText(item.LastModified),
                        ["storage_class"] = item.StorageClass,
                        ["owner"] = item.Owner is null ? null : new JsonObject
                        {
                            ["id"] = item.Owner.Id,
                            ["display_name"] = item.Owner.DisplayName,
                        },
                        ["restore_info"] = null,
                        ["transition_time"] = null,
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
        [Description("The Storage Type of AlibabaCloud OSS Bucket, The value range is as follows: Standard (default): standard storage, IA: infrequent access, Archive: archive storage, ColdArchive: cold archive storage, DeepColdArchive: deep cold archive storage")] string StorageClass = "Standard",
        [Description("The data disaster recovery type of AlibabaCloud OSS Bucket, LRS (default): Locally redundant LRS, which stores your data redundantly on different storage devices in the same availability zone. ZRS: Intra-city redundant ZRS, which uses a multi-availability zone (AZ) mechanism to store your data redundantly in three availability zones in the same region.")] string DataRedundancyType = "LRS",
        CancellationToken cancellationToken = default) =>
        RunAsync(RegionId, client =>
        {
            var request = BuildCreateBucketRequest(BucketName, StorageClass, DataRedundancyType);
            client.CreateBucket(request);
            // .NET SDK 的 CreateBucket 不返回响应元数据，成功即 200
            return ResultFields(null, 200, null);
        });

    /// <summary>构造建桶请求；非法存储/冗余类型直接报错，不静默回落默认值。</summary>
    internal static CreateBucketRequest BuildCreateBucketRequest(string bucketName, string storageClass, string dataRedundancyType)
    {
        var request = new CreateBucketRequest(bucketName);
        if (!string.IsNullOrWhiteSpace(storageClass))
        {
            request.StorageClass = Enum.Parse<Aliyun.OSS.StorageClass>(storageClass);
        }

        if (!string.IsNullOrWhiteSpace(dataRedundancyType))
        {
            request.DataRedundancyType = Enum.Parse<Aliyun.OSS.DataRedundancyType>(dataRedundancyType);
        }

        return request;
    }

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
            // .NET SDK 的 DeleteBucket 无返回值，成功即 204
            return ResultFields(null, 204, null);
        });

    /// <summary>按上游 ResultModel 字段顺序输出请求结果。</summary>
    private static JsonObject ResultFields(string? requestId, int statusCode, IDictionary<string, string>? headers)
    {
        var headerNode = new JsonObject();
        if (headers is not null)
        {
            foreach (var (key, value) in headers)
            {
                headerNode[key] = value;
            }
        }

        return new JsonObject
        {
            ["status"] = statusCode switch { 200 => "OK", 201 => "Created", 202 => "Accepted", 204 => "No Content", _ => "" },
            ["status_code"] = statusCode,
            ["request_id"] = requestId ?? string.Empty,
            ["headers"] = headerNode,
        };
    }

    /// <summary>尽力读取桶详情；无权限或失败时返回 null，不影响列举本身。</summary>
    private static BucketInfoEntry? TryGetBucketInfo(OssClient client, string bucketName)
    {
        try
        {
            return client.GetBucketInfo(bucketName).Bucket;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>时间统一归一为 UTC ISO-8601。</summary>
    private static string UtcText(DateTime value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

    /// <summary>解析凭证、构造区域端点并执行操作，异常转错误结果。</summary>
    private Task<CallToolResult> RunAsync(string regionId, Func<OssClient, JsonNode?> action)
    {
        try
        {
            var credentials = credentialProvider.ResolveForOss();
            var endpoint = $"https://oss-{regionId.ToLowerInvariant()}.aliyuncs.com";
            var client = new OssClient(endpoint, credentials.AccessKeyId, credentials.AccessKeySecret,
                credentials.SecurityToken);
            return Task.FromResult(ToolResults.Text(action(client)));
        }
        catch (Exception exception)
        {
            return Task.FromResult(ToolResults.Error(exception));
        }
    }
}

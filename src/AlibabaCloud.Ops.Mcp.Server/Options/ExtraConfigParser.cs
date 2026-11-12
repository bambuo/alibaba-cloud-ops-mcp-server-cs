using System.Text.Json;

namespace AlibabaCloud.Ops.Mcp.Server.Options;

/// <summary>解析 `--extra-config`：只接受「服务码 → API 名数组」的 JSON 对象。</summary>
public static class ExtraConfigParser
{
    /// <summary>解析并校验 JSON，非法结构抛 <see cref="OptionException"/>。</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new OptionException("InvalidArgument: --extra-config expects a JSON object");
            }

            var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Array)
                {
                    throw new OptionException($"InvalidArgument: --extra-config.{property.Name} expects an array of API names");
                }

                var apis = property.Value.EnumerateArray()
                    .Select(x => x.GetString())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                result[property.Name] = apis;
            }

            return result;
        }
        catch (JsonException exception)
        {
            throw new OptionException($"InvalidArgument: --extra-config expects a JSON object ({exception.Message})");
        }
    }

    /// <summary>启动阶段预校验，避免运行期才发现配置非法。</summary>
    public static void Validate(string json) => Parse(json);
}

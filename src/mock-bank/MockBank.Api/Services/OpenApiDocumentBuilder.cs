// ===== OpenAPI 文档生成器：基于 ApiExplorer 端点元数据 + CLR 反射生成 OpenAPI 3.0 文档（零第三方依赖）=====

using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using MockBank.Api.Contracts;

namespace MockBank.Api.Services;

/// <summary>
/// OpenAPI 3.0 文档生成器。数据来源为 ASP.NET Core 内置的 ApiExplorer 元数据
/// （端点路径、参数、响应类型）与端点自挂载的 <see cref="ApiOperationMetadata"/>，
/// schema 由 CLR 类型反射生成。项目不引入任何第三方 OpenAPI 依赖。
/// </summary>
/// <param name="apiDescriptionProvider">内置 ApiExplorer 提供器。</param>
public sealed class OpenApiDocumentBuilder(IApiDescriptionGroupCollectionProvider apiDescriptionProvider)
{
    /// <summary>服务标题。</summary>
    public const string DocumentTitle = "模拟银行核心系统 API（AI Banking Agent Mock Core）";

    /// <summary>文档版本。</summary>
    public const string DocumentVersion = "v1";

    private static readonly JsonNamingPolicy NamingPolicy = JsonNamingPolicy.CamelCase;

    /// <summary>生成完整的 OpenAPI 3.0 文档。</summary>
    /// <returns>可直接序列化为 JSON 的文档根节点。</returns>
    public JsonObject Build()
    {
        var schemas = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var paths = new JsonObject();

        foreach (var api in apiDescriptionProvider.ApiDescriptionGroups.Items.SelectMany(g => g.Items))
        {
            var meta = Metadata(api);
            var relativePath = NormalizePath(api.RelativePath);
            if (relativePath is null || api.HttpMethod is null || meta is null || meta.Hidden)
            {
                continue;
            }

            var operation = new JsonObject
            {
                ["operationId"] = meta.OperationId,
                ["tags"] = new JsonArray(meta.Tags.Select(t => (JsonNode?)t).ToArray()),
                ["summary"] = meta.Summary,
                ["description"] = meta.Description,
            };

            AddParameters(relativePath, api, operation, schemas);
            AddRequestBody(api, meta, operation, schemas);
            AddResponses(api, operation, schemas);

            if (paths[relativePath] is not JsonObject pathItem)
            {
                pathItem = new JsonObject();
                paths[relativePath] = pathItem;
            }

            pathItem[api.HttpMethod.ToLowerInvariant()] = operation;
        }

        var schemaComponents = new JsonObject();
        foreach (var (name, schema) in schemas.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            schemaComponents[name] = schema;
        }

        return new JsonObject
        {
            ["openapi"] = "3.0.1",
            ["info"] = new JsonObject
            {
                ["title"] = DocumentTitle,
                ["version"] = DocumentVersion,
                ["description"] = "面向 AI Banking Agent 演示的模拟银行核心系统。所有金额单位为元，"
                    + "错误响应统一为 { code, message, details, traceId }。"
                    + "转账与理财认购端点支持通过 X-Mock-Scenario 请求头注入故障（"
                    + "success / insufficient_funds / daily_limit_exceeded / account_frozen / timeout / downstream_error）。",
            },
            ["servers"] = new JsonArray(new JsonObject { ["url"] = "http://localhost:5200" }),
            ["paths"] = paths,
            ["components"] = new JsonObject { ["schemas"] = schemaComponents },
        };
    }

    private static ApiOperationMetadata? Metadata(ApiDescription api) =>
        api.ActionDescriptor.EndpointMetadata.OfType<ApiOperationMetadata>().FirstOrDefault();

    /// <summary>把 ApiExplorer 给出的相对路径规范化为 OpenAPI 要求的“以 / 开头、无结尾 /”形式。</summary>
    /// <param name="relativePath">ApiExplorer 输出的相对路径。</param>
    /// <returns>规范化后的路径；为空时返回 null。</returns>
    private static string? NormalizePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var path = "/" + relativePath.Trim('/');
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    private static void AddParameters(string relativePath, ApiDescription api, JsonObject operation, Dictionary<string, JsonObject> schemas)
    {
        var parameters = new JsonArray();
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in api.ParameterDescriptions)
        {
            var inPath = parameter.Source == BindingSource.Path;
            var inQuery = parameter.Source == BindingSource.Query;
            if (!inPath && !inQuery)
            {
                continue;
            }

            declared.Add(parameter.Name ?? string.Empty);
            parameters.Add(new JsonObject
            {
                ["name"] = parameter.Name,
                ["in"] = inPath ? "path" : "query",
                ["required"] = inPath || parameter.IsRequired,
                ["schema"] = BuildSchema(parameter.Type, schemas, parameter.Name ?? "param"),
            });
        }

        // 兜底：ApiExplorer 未识别出的路由参数按字符串补齐，避免文档出现无效路径。
        foreach (var name in RouteParameterNames(relativePath))
        {
            if (declared.Add(name))
            {
                parameters.Add(new JsonObject
                {
                    ["name"] = name,
                    ["in"] = "path",
                    ["required"] = true,
                    ["schema"] = new JsonObject { ["type"] = "string" },
                });
            }
        }

        if (parameters.Count > 0)
        {
            operation["parameters"] = parameters;
        }
    }

    private static void AddRequestBody(
        ApiDescription api,
        ApiOperationMetadata meta,
        JsonObject operation,
        Dictionary<string, JsonObject> schemas)
    {
        var bodyType = meta.RequestBodyType
            ?? api.ParameterDescriptions.FirstOrDefault(p => p.Source == BindingSource.Body)?.Type;
        if (bodyType is null)
        {
            return;
        }

        operation["requestBody"] = new JsonObject
        {
            ["required"] = true,
            ["content"] = new JsonObject
            {
                ["application/json"] = new JsonObject
                {
                    ["schema"] = BuildSchema(bodyType, schemas, "body"),
                },
            },
        };
    }

    private static void AddResponses(ApiDescription api, JsonObject operation, Dictionary<string, JsonObject> schemas)
    {
        var responses = new JsonObject();
        foreach (var response in api.SupportedResponseTypes)
        {
            var statusCode = response.StatusCode.ToString(CultureInfo.InvariantCulture);
            if (responses[statusCode] is not JsonObject existing)
            {
                existing = new JsonObject();
                responses[statusCode] = existing;
            }

            existing["description"] = DescribeStatusCode(response.StatusCode);
            if (response.Type is not null && response.Type != typeof(void))
            {
                existing["content"] = new JsonObject
                {
                    ["application/json"] = new JsonObject
                    {
                        ["schema"] = BuildSchema(response.Type, schemas, "response"),
                    },
                };
            }
        }

        operation["responses"] = responses.Count > 0
            ? responses
            : new JsonObject { ["200"] = new JsonObject { ["description"] = "成功" } };
    }

    /// <summary>根据 CLR 类型生成 JSON Schema；复杂类型注册到 components.schemas 并以 $ref 引用。</summary>
    /// <param name="type">目标类型。</param>
    /// <param name="schemas">已注册的 schema 集合。</param>
    /// <param name="nameHint">类型名无法推导时使用的提示名。</param>
    /// <returns>schema 节点。</returns>
    private static JsonNode BuildSchema(Type? type, Dictionary<string, JsonObject> schemas, string nameHint)
    {
        var nullable = false;
        if (type is not null && Nullable.GetUnderlyingType(type) is { } underlying)
        {
            type = underlying;
            nullable = true;
        }

        type ??= typeof(object);
        var isReference = !type.IsValueType;

        if (type == typeof(string) || type == typeof(object))
        {
            return new JsonObject { ["type"] = "string", ["nullable"] = nullable || isReference };
        }

        if (type == typeof(bool))
        {
            return new JsonObject { ["type"] = "boolean" };
        }

        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte))
        {
            return new JsonObject { ["type"] = "integer", ["format"] = "int64" };
        }

        if (type == typeof(decimal))
        {
            return new JsonObject { ["type"] = "number", ["format"] = "decimal" };
        }

        if (type == typeof(double) || type == typeof(float))
        {
            return new JsonObject { ["type"] = "number", ["format"] = "double" };
        }

        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
        {
            return new JsonObject { ["type"] = "string", ["format"] = "date-time" };
        }

        if (type == typeof(Guid))
        {
            return new JsonObject { ["type"] = "string", ["format"] = "uuid" };
        }

        if (type.IsEnum)
        {
            var enumName = SchemaName(type);
            if (!schemas.ContainsKey(enumName))
            {
                schemas[enumName] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(Enum.GetNames(type).Select(n => (JsonNode?)n).ToArray()),
                };
            }

            return Ref(enumName);
        }

        var elementType = ElementTypeOf(type);
        if (elementType is not null)
        {
            return new JsonObject
            {
                ["type"] = "array",
                ["items"] = BuildSchema(elementType, schemas, nameHint + "Item"),
            };
        }

        var name = SchemaName(type);
        if (!schemas.ContainsKey(name))
        {
            // 先占位再填充，避免自引用类型导致无限递归。
            schemas[name] = new JsonObject { ["type"] = "object" };
            var properties = new JsonObject();
            var required = new JsonArray();

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                properties[NamingPolicy.ConvertName(property.Name)] = BuildSchema(property.PropertyType, schemas, property.Name);

                if (property.PropertyType.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) is null)
                {
                    required.Add(NamingPolicy.ConvertName(property.Name));
                }
            }

            schemas[name]["properties"] = properties;
            if (required.Count > 0)
            {
                schemas[name]["required"] = required;
            }
        }

        return Ref(name);
    }

    private static JsonObject Ref(string name) => new() { ["$ref"] = $"#/components/schemas/{name}" };

    private static IEnumerable<string> RouteParameterNames(string? relativePath) =>
        System.Text.RegularExpressions.Regex.Matches(relativePath ?? string.Empty, @"\{([^}?]+)\}")
            .Select(m => m.Groups[1].Value.Split(':')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static Type? ElementTypeOf(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType();
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(IEnumerable<>)
                || definition == typeof(IReadOnlyList<>)
                || definition == typeof(IReadOnlyCollection<>)
                || definition == typeof(IList<>)
                || definition == typeof(ICollection<>)
                || definition == typeof(List<>))
            {
                return type.GetGenericArguments()[0];
            }
        }

        return null;
    }

    private static string SchemaName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var name = type.Name;
        var tick = name.IndexOf('`');
        if (tick > 0)
        {
            name = name[..tick];
        }

        return $"{name}Of{string.Join("And", type.GetGenericArguments().Select(SchemaName))}";
    }

    private static string DescribeStatusCode(int statusCode) => statusCode switch
    {
        200 => "成功",
        400 => "请求参数或业务校验失败",
        404 => "资源不存在",
        503 => "下游系统不可用",
        _ => $"HTTP {statusCode}",
    };
}

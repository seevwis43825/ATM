// ===== 槽位解析工具 =====
// Agent 从上游接收的槽位经 JSON 反序列化后是 JsonElement，
// 直接强转会失败。此处统一处理，避免每个插件重复实现。

namespace BankingAgent.Base.Agents;

using System.Text.Json;
using BankingAgent.PluginSdk;

/// <summary>槽位读取工具。插件 Agent 应优先使用它，而不是自行强转。</summary>
public static class SlotReader
{
    /// <summary>读取字符串槽位。</summary>
    public static string? String(AgentRequest request, string key)
    {
        if (!request.Slots.TryGetValue(key, out var value) || value is null) return null;

        return value switch
        {
            string s => s,
            // JsonElement 来自已释放的 JsonDocument 时会抛异常，防御性降级为空
            JsonElement je => TryRead(je, out var r) ? r : null,
            _ => value.ToString()
        };
    }

    /// <summary>安全读取 JsonElement，捕获文档已释放的情况。</summary>
    private static bool TryRead(JsonElement je, out string result)
    {
        try
        {
            result = je.ValueKind == JsonValueKind.String
                ? je.GetString() ?? ""
                : je.ToString();
            return true;
        }
        catch (ObjectDisposedException)
        {
            result = "";
            return false;
        }
    }

    /// <summary>读取十进制槽位。支持 JSON 数字与字符串。</summary>
    public static decimal? Decimal(AgentRequest request, string key)
    {
        if (!request.Slots.TryGetValue(key, out var value) || value is null) return null;

        return value switch
        {
            decimal d => d,
            int i => i,
            long l => l,
            double dbl => (decimal)dbl,
            string s when decimal.TryParse(s, out var parsed) => parsed,
            JsonElement je => TryReadDecimal(je),
            _ => null
        };
    }

    /// <summary>安全读取 JsonElement 数值。</summary>
    private static decimal? TryReadDecimal(JsonElement je)
    {
        try
        {
            if (je.ValueKind == JsonValueKind.Number) return je.GetDecimal();
            if (je.ValueKind == JsonValueKind.String
                && decimal.TryParse(je.GetString(), out var parsed))
            {
                return parsed;
            }
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>读取整数槽位。</summary>
    public static int? Int(AgentRequest request, string key)
    {
        if (!request.Slots.TryGetValue(key, out var value) || value is null) return null;

        return value switch
        {
            int i => i,
            long l => (int)l,
            decimal d => (int)d,
            string s when int.TryParse(s, out var parsed) => parsed,
            JsonElement je => TryReadInt(je),
            _ => null
        };
    }

    private static int? TryReadInt(JsonElement je)
    {
        try
        {
            if (je.ValueKind == JsonValueKind.Number) return je.GetInt32();
            if (je.ValueKind == JsonValueKind.String
                && int.TryParse(je.GetString(), out var parsed))
            {
                return parsed;
            }
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>读取布尔槽位。</summary>
    public static bool? Bool(AgentRequest request, string key)
    {
        if (!request.Slots.TryGetValue(key, out var value) || value is null) return null;

        return value switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            JsonElement je => TryReadBool(je),
            _ => null
        };
    }

    private static bool? TryReadBool(JsonElement je)
    {
        try
        {
            if (je.ValueKind == JsonValueKind.True) return true;
            if (je.ValueKind == JsonValueKind.False) return false;
            if (je.ValueKind == JsonValueKind.String
                && bool.TryParse(je.GetString(), out var parsed))
            {
                return parsed;
            }
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }
}

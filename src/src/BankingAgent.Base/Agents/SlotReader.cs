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
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            JsonElement je when je.ValueKind is JsonValueKind.Number
                or JsonValueKind.True or JsonValueKind.False => je.ToString(),
            _ => value.ToString()
        };
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
            JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetDecimal(),
            JsonElement je when je.ValueKind == JsonValueKind.String
                && decimal.TryParse(je.GetString(), out var parsed) => parsed,
            string s when decimal.TryParse(s, out var parsed) => parsed,
            _ => null
        };
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
            JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetInt32(),
            JsonElement je when je.ValueKind == JsonValueKind.String
                && int.TryParse(je.GetString(), out var parsed) => parsed,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => null
        };
    }

    /// <summary>读取布尔槽位。</summary>
    public static bool? Bool(AgentRequest request, string key)
    {
        if (!request.Slots.TryGetValue(key, out var value) || value is null) return null;

        return value switch
        {
            bool b => b,
            JsonElement je when je.ValueKind == JsonValueKind.True => true,
            JsonElement je when je.ValueKind == JsonValueKind.False => false,
            JsonElement je when je.ValueKind == JsonValueKind.String
                && bool.TryParse(je.GetString(), out var parsed) => parsed,
            string s when bool.TryParse(s, out var parsed) => parsed,
            _ => null
        };
    }
}

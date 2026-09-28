// ===== 插件契约校验器 =====
//
// 用途：在 CI 与本地开发中检查每个插件程序集是否满足接入契约。
//
// 为什么需要它：插件在宿主里是"动态发现 + 反射加载"的，
// 违规写法（文件名不匹配、缺入口、Id 重复、分区名非法、引用其他插件）
// 都不会导致编译失败，只会表现为「插件没加载」或运行期行为异常 ——
// 排查成本高。把规则前移到静态校验，能在提交前拦下。
//
// 用法：
//   dotnet run --project PluginValidator -- <插件目录> [更多目录...]
//   dotnet run --project PluginValidator -- --json <目录>
//
// 退出码：0 = 无 ERROR；1 = 存在 ERROR（可用于 CI 门禁）。
//
// 实现要点：用 MetadataLoadContext 做「仅反射」检查，不执行插件代码。
// 关键：插件类型与它实现的契约接口必须从**同一个** MetadataLoadContext
// 解析，否则 IsAssignableFrom 会因为类型标识不同而恒为 false。

using System.Reflection;
using System.Text.Json;

namespace BankingAgent.Tools.PluginValidator;

internal static class Program
{
    private const string SdkAssemblyName = "BankingAgent.Plugin.Sdk";
    private const string BaseAssemblyName = "BankingAgent.Base";
    private const string HostAssemblyName = "BankingAgent.Host";
    private const string PluginFilePrefix = "BankingAgent.Plugin.";

    private static int Main(string[] args)
    {
        var json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
        var dirs = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();

        if (dirs.Count == 0)
        {
            Console.Error.WriteLine("用法: PluginValidator <插件目录> [更多目录...] [--json]");
            Console.Error.WriteLine("示例: PluginValidator bin/Release/net8.0/plugins");
            return 2;
        }

        var findings = new List<Finding>();
        var candidatePaths = new List<string>();

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir))
            {
                findings.Add(Finding.Error(dir, "目录不存在"));
                continue;
            }

            candidatePaths.AddRange(Directory.EnumerateFiles(dir, "*.dll"));
        }

        if (candidatePaths.Count == 0)
        {
            findings.Add(Finding.Error(string.Join(", ", dirs),
                "目录下没有任何 DLL。请先构建解决方案（dotnet build）。"));
        }

        // 前置检查：契约程序集必须可解析。
        // 否则所有接口检查都会被跳过，校验器会"全绿"通过 —— 那比没有校验更危险。
        if (!CanResolveContract(candidatePaths))
        {
            findings.Add(Finding.Error(string.Join(", ", dirs),
                $"无法解析契约程序集 {SdkAssemblyName}。请把包含 SDK/Base 的输出目录" +
                "（通常是宿主的 bin/<配置>/<tfm>）一并传入，或先执行 dotnet build。"));
            return Report(manifests: [], findings, json);
        }

        // ===== 发现阶段：按「结构」而非「文件名」挑选插件 =====
        // 这一点很关键。若只扫描 BankingAgent.Plugin.*.dll，那么一个
        // 命名写错的插件会被静默跳过 —— 而"命名写错会导致宿主扫不到"
        // 恰恰是最需要报出来的问题。因此先扫全部 DLL，
        // 凡实现了 IPluginEntryPoint 的都纳入检查，再由规则去判定命名。
        var manifests = new List<ManifestInfo>();
        foreach (var asmPath in candidatePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(asmPath);

            // 框架与宿主自身不是插件，跳过（Base 不实现 IPluginEntryPoint，
            // 但显式跳过更清晰，也避免把第三方依赖误报成插件）
            if (name.Equals(SdkAssemblyName, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(BaseAssemblyName, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(HostAssemblyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (LooksLikePluginEntryPoint(asmPath))
            {
                manifests.Add(Inspect(asmPath, findings));
            }
        }

        if (manifests.Count == 0)
        {
            findings.Add(Finding.Error(string.Join(", ", dirs),
                $"未发现实现了 IPluginEntryPoint 的程序集。请确认已构建（dotnet build）。"));
        }

        CheckCrossPlugin(manifests, findings);

        return Report(manifests, findings, json);
    }

    // ============================================================
    // 单个程序集检查
    // ============================================================

    /// <summary>
    /// 快速判断某 DLL 是否实现了 IPluginEntryPoint（据此认定它是插件）。
    /// 解析失败一律返回 false：损坏文件交给依赖扫描去报，不在这里炸掉。
    /// </summary>
    private static bool LooksLikePluginEntryPoint(string asmPath)
    {
        try
        {
            var (mlc, asm) = LoadForInspection(asmPath);
            var contract = ResolveContract(mlc, SdkAssemblyName, "BankingAgent.PluginSdk.IPluginEntryPoint");
            if (contract is null) return false;

            return asm.GetTypes().Any(t => IsConcreteImplementer(t, contract));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 检查在给定候选目录集合下，契约程序集能否被解析。
    /// 用作「不静默通过」的保险丝。
    /// </summary>
    private static bool CanResolveContract(IReadOnlyList<string> candidatePaths)
    {
        var probe = candidatePaths.FirstOrDefault();
        if (probe is null) return false;

        try
        {
            var (mlc, _) = LoadForInspection(probe);
            return ResolveContract(mlc, SdkAssemblyName, "BankingAgent.PluginSdk.IPluginEntryPoint") is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>统一输出与退出码计算。</summary>
    private static int Report(IReadOnlyList<ManifestInfo> manifests, List<Finding> findings, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                pluginCount = manifests.Count,
                plugins = manifests.Select(m => new
                {
                    m.Path, m.EntryPointType, m.Agents, m.PartitionName, m.ReferencedAssemblies
                }),
                findings = findings.Select(f => new
                {
                    severity = f.Severity.ToString(), f.Subject, f.Message
                })
            }, new JsonSerializerOptions { WriteIndented = true }));
            return findings.Any(f => f.Severity == Severity.Error) ? 1 : 0;
        }

        Console.WriteLine();
        Console.WriteLine(new string('=', 78));
        Console.WriteLine($" 插件契约校验：共检查 {manifests.Count} 个程序集");
        Console.WriteLine(new string('=', 78));

        foreach (var m in manifests)
        {
            var bad = findings.Any(f => f.Severity == Severity.Error && f.Subject == m.Path);
            Console.WriteLine($"  [{(bad ? "FAIL" : "OK  ")}] {Path.GetFileName(m.Path)}");
            if (m.EntryPointType is not null)
                Console.WriteLine($"           入口: {m.EntryPointType}");
            if (m.PartitionName is not null)
                Console.WriteLine($"           数据分区: {m.PartitionName}");
            Console.WriteLine($"           Agent {m.Agents.Count} 个: " +
                (m.Agents.Count == 0 ? "（无）" : string.Join(", ", m.Agents)));
        }

        Console.WriteLine();
        if (findings.Count == 0)
        {
            Console.WriteLine("  未发现问题。");
        }
        else
        {
            foreach (var f in findings.OrderByDescending(f => f.Severity))
            {
                Console.WriteLine($"  [{(f.Severity == Severity.Error ? "ERROR" : "WARN ")}] {f.Subject}");
                Console.WriteLine($"          {f.Message}");
            }
        }

        var errors = findings.Count(f => f.Severity == Severity.Error);
        var warns = findings.Count(f => f.Severity == Severity.Warning);
        Console.WriteLine();
        Console.WriteLine($"  ERROR {errors} 项，WARN {warns} 项");
        Console.WriteLine(errors == 0 ? "  *** 校验通过 ***" : "  *** 校验未通过 ***");

        return errors == 0 ? 0 : 1;
    }

    private static ManifestInfo Inspect(string asmPath, List<Finding> findings)
    {
        var info = new ManifestInfo { Path = asmPath };

        MetadataLoadContext mlc;
        Assembly asm;
        try
        {
            (mlc, asm) = LoadForInspection(asmPath);
        }
        catch (Exception ex)
        {
            findings.Add(Finding.Error(asmPath, $"无法加载程序集（仅反射）: {ex.Message}"));
            return info;
        }

        Type[] types;
        try
        {
            types = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            findings.Add(Finding.Error(asmPath,
                "部分类型无法加载：" + string.Join("; ",
                    ex.LoaderExceptions.Select(e => e?.Message)
                        .Where(m => m is not null).Take(3))));
            types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
        }

        var fileName = Path.GetFileNameWithoutExtension(asmPath);
        var referenced = asm.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();
        info.ReferencedAssemblies = referenced;

        // ===== 规则 1：必须引用 SDK =====
        if (!referenced.Contains(SdkAssemblyName, StringComparer.OrdinalIgnoreCase))
        {
            findings.Add(Finding.Error(asmPath, $"未引用 {SdkAssemblyName}，无法实现契约。"));
            return info;
        }

        // ===== 规则 2：文件名必须符合加载约定 =====
        if (!fileName.StartsWith(PluginFilePrefix, StringComparison.Ordinal))
        {
            findings.Add(Finding.Error(asmPath,
                $"文件名必须以 \"{PluginFilePrefix}\" 开头，否则 PluginRegistry 扫描不到。"));
        }

        // ===== 规则 3：禁止引用宿主 =====
        if (referenced.Contains(HostAssemblyName, StringComparer.OrdinalIgnoreCase))
        {
            findings.Add(Finding.Error(asmPath,
                $"引用了宿主程序集 {HostAssemblyName}，会造成循环依赖并破坏热插拔。"));
        }

        // ===== 规则 4：禁止直接引用其他插件 =====
        foreach (var other in referenced
                     .Where(r => r.StartsWith(PluginFilePrefix, StringComparison.OrdinalIgnoreCase))
                     .Where(r => !r.Equals(SdkAssemblyName, StringComparison.OrdinalIgnoreCase))
                     .Where(r => !r.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
        {
            findings.Add(Finding.Error(asmPath,
                $"直接引用了其他插件程序集 {other}。插件间只能通过事件通信，" +
                "否则会形成加载顺序耦合，且依赖插件缺失时无法降级。"));
        }

        // ===== 规则 5：入口必须恰好一个 =====
        // 注意：契约接口直接从同一个 MetadataLoadContext 解析，
        // 否则类型标识不同，IsAssignableFrom 恒为 false。
        var entryPointContract = ResolveContract(mlc, SdkAssemblyName, "BankingAgent.PluginSdk.IPluginEntryPoint");
        if (entryPointContract is null)
        {
            findings.Add(Finding.Warning(asmPath,
                "无法解析 IPluginEntryPoint 契约（SDK 程序集不在扫描路径内），跳过接口检查。"));
        }
        else
        {
            var entryPoints = types
                .Where(t => IsConcreteImplementer(t, entryPointContract))
                .ToList();

            if (entryPoints.Count == 0)
            {
                findings.Add(Finding.Error(asmPath, "未实现 IPluginEntryPoint，宿主不会加载它。"));
            }
            else if (entryPoints.Count > 1)
            {
                findings.Add(Finding.Error(asmPath,
                    $"实现了 {entryPoints.Count} 个 IPluginEntryPoint，契约要求每个程序集恰好一个：" +
                    string.Join(", ", entryPoints.Select(t => t.FullName))));
            }
            else
            {
                info.EntryPointType = entryPoints[0].FullName;

                var getManifest = entryPoints[0].GetMethod("GetManifest");
                if (getManifest is null || !getManifest.IsPublic)
                {
                    findings.Add(Finding.Error(asmPath, "IPluginEntryPoint.GetManifest 缺失或非 public。"));
                }
                else if (getManifest.ReturnType.Name != "PluginManifest")
                {
                    findings.Add(Finding.Error(asmPath,
                        $"GetManifest 返回 {getManifest.ReturnType.Name}，契约要求 PluginManifest。"));
                }

                var configure = entryPoints[0].GetMethod("ConfigureServices");
                if (configure is null || !configure.IsPublic)
                {
                    findings.Add(Finding.Error(asmPath, "IPluginEntryPoint.ConfigureServices 缺失或非 public。"));
                }
            }
        }

        // ===== 规则 6：至少有一个 Agent（否则不响应对话）=====
        var agentContract = ResolveContract(mlc, SdkAssemblyName, "BankingAgent.PluginSdk.IBankingAgent");
        if (agentContract is not null)
        {
            info.Agents = types
                .Where(t => IsConcreteImplementer(t, agentContract))
                .Select(t => t.Name)
                .ToList();

            if (info.Agents.Count == 0)
            {
                findings.Add(Finding.Warning(asmPath,
                    "未实现 IBankingAgent。插件加载后不会响应任何对话请求" +
                    "（若仅作后台任务或纯事件订阅插件，可忽略）。"));
            }
        }

        // ===== 规则 7：数据分区贡献器必须暴露 string PartitionName =====
        var contributorContract = ResolveContract(mlc, BaseAssemblyName,
            "BankingAgent.Base.Data.IEntitySetContributor");
        if (contributorContract is not null)
        {
            foreach (var c in types.Where(t => IsConcreteImplementer(t, contributorContract)))
            {
                // 注意：仅反射模式下不能用 PropertyInfo.PropertyType
                // （MetadataLoadContext 不支持它，会抛异常或被当作不匹配）。
                // 改用 getter 方法的返回类型判断，这样 `=> "x"` 与 `{ get; }` 都能识别。
                var getter = c.GetProperty("PartitionName")?.GetMethod;
                if (getter is null || getter.ReturnType.FullName != "System.String")
                {
                    findings.Add(Finding.Error(asmPath,
                        $"{c.Name} 未正确实现 PartitionName（必须是 public string 属性/getter）。"));
                    continue;
                }

                info.PartitionName ??= "(运行期求值)";
            }
        }

        return info;
    }

    /// <summary>从指定程序集解析契约类型（必须用同一个 MetadataLoadContext）。</summary>
    private static Type? ResolveContract(MetadataLoadContext mlc, string assemblyName, string typeName)
    {
        try
        {
            return mlc.LoadFromAssemblyName(new AssemblyName(assemblyName)).GetType(typeName);
        }
        catch
        {
            // SDK / Base 不在解析路径内时无法解析，交由调用方降级为警告
            return null;
        }
    }

    /// <summary>判断是否是可实例化的契约实现（排除接口、抽象类、泛型参数）。</summary>
    private static bool IsConcreteImplementer(Type t, Type contract)
    {
        try
        {
            return !t.IsAbstract && !t.IsInterface && !t.IsGenericTypeDefinition
                   && contract.IsAssignableFrom(t);
        }
        catch
        {
            // 某些类型在仅反射模式下解析会抛异常，按“不是实现”处理
            return false;
        }
    }

    private static (MetadataLoadContext Context, Assembly Assembly) LoadForInspection(string asmPath)
    {
        var coreDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var fullPath = Path.GetFullPath(asmPath);
        var dir = Path.GetDirectoryName(fullPath)!;

        var paths = new List<string>(Directory.EnumerateFiles(coreDir, "*.dll"));
        paths.AddRange(Directory.EnumerateFiles(dir, "*.dll"));

        // 关键：SDK / Base 通常**不在** plugins/ 子目录里。
        // 宿主的输出布局是 bin/<cfg>/<tfm>/ 放 SDK 与 Base，
        // bin/<cfg>/<tfm>/plugins/ 只放插件本身。
        // 因此必须把父目录（以及再上一层）也加入解析路径，
        // 否则契约接口解析不到，接口检查会被整体跳过 ——
        // 一个"永远通过"的校验器比没有校验器更危险。
        for (var up = Directory.GetParent(dir); up is not null && paths.Count < 4000; up = up.Parent)
        {
            if (!File.Exists(Path.Combine(up.FullName, SdkAssemblyName + ".dll")) &&
                !File.Exists(Path.Combine(up.FullName, BaseAssemblyName + ".dll")))
            {
                continue;
            }

            paths.AddRange(Directory.EnumerateFiles(up.FullName, "*.dll"));
            break;
        }

        // 校验器自身目录（例如通过项目引用拿到 SDK 时）
        var selfDir = Path.GetDirectoryName(typeof(Program).Assembly.Location);
        if (selfDir is not null)
        {
            paths.AddRange(Directory.EnumerateFiles(selfDir, "*.dll"));
        }

        var resolver = new PathAssemblyResolver(paths.Distinct(StringComparer.OrdinalIgnoreCase));
        var mlc = new MetadataLoadContext(resolver);
        return (mlc, mlc.LoadFromAssemblyPath(fullPath));
    }

    private static void CheckCrossPlugin(List<ManifestInfo> manifests, List<Finding> findings)
    {
        foreach (var g in manifests
                     .GroupBy(m => Path.GetFileName(m.Path), StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            findings.Add(Finding.Warning(g.First().Path,
                $"同名程序集出现 {g.Count()} 次：{string.Join(", ", g.Select(x => x.Path))}"));
        }
    }

    // ============================================================
    // 辅助类型
    // ============================================================

    private enum Severity { Warning, Error }

    private sealed record Finding(Severity Severity, string Subject, string Message)
    {
        public static Finding Error(string subject, string message) => new(Severity.Error, subject, message);
        public static Finding Warning(string subject, string message) => new(Severity.Warning, subject, message);
    }

    private sealed class ManifestInfo
    {
        public required string Path { get; init; }
        public string? EntryPointType { get; set; }
        public string? PartitionName { get; set; }
        public List<string> Agents { get; set; } = [];
        public List<string> ReferencedAssemblies { get; set; } = [];
    }
}
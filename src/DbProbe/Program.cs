// 探测 Provider 连接串 Builder 的真实属性（直接实例化触发程序集加载）
using System.Reflection;

void Probe(Func<object> factory, string label)
{
    Console.WriteLine("=== " + label + " ===");
    try
    {
        var obj = factory();
        var t = obj.GetType();
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.CanWrite).OrderBy(p => p.Name))
        {
            var pt = p.PropertyType.Name;
            if (pt.Contains("Int") || pt.Contains("Time") || pt.Contains("Bool")
                || pt.Contains("String") || pt.Contains("Pwd") || pt.Contains("Secre"))
            {
                Console.WriteLine("  " + pt + " " + p.Name);
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("  ERR: " + ex.Message);
    }
    Console.WriteLine();
}

Probe(() => new Npgsql.NpgsqlConnectionStringBuilder("Host=x"), "NpgsqlConnectionStringBuilder");
Probe(() => new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder("Data Source=x"), "SqliteConnectionStringBuilder");
Probe(() => new Microsoft.Data.SqlClient.SqlConnectionStringBuilder("Server=x"), "SqlConnectionStringBuilder");

using DatesErp.Tests;
using Microsoft.Data.SqlClient;
using System.Text.Json;

var sql = args.Contains("--sql");
var actualDelivery = args.Contains("--actual-delivery");
var todayOrders = args.Contains("--orders");
string? connection = null;
string? database = null;
var results = new List<object>();
int count = 0;
SqlConnectionStringBuilder? master = null;
try
{
    if (sql)
    {
        string configured = Environment.GetEnvironmentVariable("DATEERP_TEST_SQL") ?? throw new Exception("DATEERP_TEST_SQL is required. No SQLite fallback is permitted.");
        master = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master" };
        database = "DateERP_PlanningAcceptance_" + Guid.NewGuid().ToString("N");
        var builder = new SqlConnectionStringBuilder(master.ConnectionString) { InitialCatalog = database };
        connection = builder.ConnectionString;
        using var c = new SqlConnection(master.ConnectionString); c.Open();
        using var cmd = new SqlCommand("SELECT @@VERSION", c);
        Console.WriteLine(cmd.ExecuteScalar());
        Console.WriteLine("Isolated database: " + database);
    }
    using var host = new TestHost(sqlConnection: connection); host.LoginAsAdmin();
    Action<IServiceProvider, Action<bool, string>, bool> scenario = actualDelivery ? ActualDeliveryScenarios.Run : todayOrders ? TodayOrdersScenarios.Run : PlanningScenarios.Run;
    scenario(host.Services, (ok, label) =>
    {
        results.Add(new { step = ++count, passed = ok, label });
        Console.WriteLine((ok ? "PASS: " : "FAIL: ") + label);
        if (!ok) throw new Exception(label);
        if (actualDelivery && label.StartsWith("تقرير الإنتاج اليومي يقرأ الفرق", StringComparison.Ordinal)
            && Environment.GetEnvironmentVariable("DATEERP_ACTUAL_EVIDENCE_JSON") is string snapshot)
            ActualAcceptanceSnapshot.Write(host.Services, snapshot);
    }, sql);
    Console.WriteLine($"PASS {count}/{count}; provider={(sql ? "SQL Server" : "SQLite")}; actual shared UI row setters, not Windows WPF execution.");
    var output = Environment.GetEnvironmentVariable("DATEERP_ACCEPTANCE_JSON");
    if (output != null) File.WriteAllText(output, JsonSerializer.Serialize(new { provider = sql ? "SQL Server" : "SQLite", database, windowsWpfExecuted = false, results }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
finally
{
    // Only the random database created by this runner can be dropped, never a configured user database.
    if (database?.StartsWith("DateERP_PlanningAcceptance_") == true && master != null)
    {
        SqlConnection.ClearAllPools();
        using var c = new SqlConnection(master.ConnectionString); c.Open();
        using var cmd = new SqlCommand($"IF DB_ID('{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END", c);
        cmd.ExecuteNonQuery(); Console.WriteLine("Removed own isolated test database.");
    }
}

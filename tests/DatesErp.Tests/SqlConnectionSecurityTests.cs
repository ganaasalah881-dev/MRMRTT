using System.Text.Json;
using DatesErp.Infrastructure.Connection;
using Microsoft.Data.SqlClient;

namespace DatesErp.Tests;

public class SqlConnectionSecurityTests
{
    [Theory]
    [InlineData("Normal123!")]
    [InlineData("Example;Pass123")]
    [InlineData("Example;Database=OtherDb;Encrypt=False")]
    [InlineData(" space;\"quoted\"='value' ")]
    [InlineData("كلمة;مرور=سرية")]
    public void Password_RoundTrips_Without_Changing_Connection_Options(string password)
    {
        var cfg = new AppConfig
        {
            Server = @"SERVER\INSTANCE", Database = "DateFactory", AuthMode = "Sql", SqlUid = "erp",
            EncryptedSqlPassword = Protect.ProtectText(password)
        };
        var parsed = new SqlConnectionStringBuilder(cfg.BuildSqlServerConnectionString());
        Assert.Equal(password, parsed.Password);
        Assert.Equal("DateFactory", parsed.InitialCatalog);
        Assert.Equal(@"SERVER\INSTANCE", parsed.DataSource);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, parsed.Encrypt);
        Assert.False(parsed.TrustServerCertificate);
        Assert.False(parsed.PersistSecurityInfo);
        Assert.True(parsed.MultipleActiveResultSets);
    }

    [Fact]
    public void Server_Database_And_UserId_Are_Values_Not_Extra_Options()
    {
        const string server = "server;Encrypt=False";
        const string database = "factory;TrustServerCertificate=True";
        const string uid = "erp;Integrated Security=True";
        var parsed = new SqlConnectionStringBuilder(SqlConnectionSettings.CreateBuilder(
            server, database, "Sql", uid, "pass").ConnectionString);
        Assert.Equal(server, parsed.DataSource);
        Assert.Equal(database, parsed.InitialCatalog);
        Assert.Equal(uid, parsed.UserID);
        Assert.False(parsed.IntegratedSecurity);
        Assert.False(parsed.TrustServerCertificate);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, parsed.Encrypt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Application_And_ConnectionTest_Use_The_Same_Explicit_TLS_Policy(bool trust)
    {
        const string password = "a;Database=DateFactory;z";
        var cfg = new AppConfig
        {
            Server = "server", Database = "DateFactory", AuthMode = "Sql", SqlUid = "erp",
            EncryptedSqlPassword = Protect.ProtectText(password), TrustServerCertificate = trust
        };
        var app = new SqlConnectionStringBuilder(cfg.BuildSqlServerConnectionString());
        var test = SqlConnectionSettings.CreateBuilder(cfg.Server, cfg.Database, cfg.AuthMode,
            cfg.SqlUid, password, trust, timeout: 6);
        Assert.Equal(app.Password, test.Password);
        Assert.Equal(app.Encrypt, test.Encrypt);
        Assert.Equal(app.TrustServerCertificate, test.TrustServerCertificate);
        Assert.Equal(trust, app.TrustServerCertificate);
        Assert.Equal(8, app.ConnectTimeout);
        Assert.Equal(6, test.ConnectTimeout);
        test.InitialCatalog = "master";
        var master = new SqlConnectionStringBuilder(test.ConnectionString);
        Assert.Equal("master", master.InitialCatalog);
        Assert.Equal(password, master.Password); // Never string.Replace on the whole connection string.
        Assert.Equal("DateFactory", app.InitialCatalog);
    }

    [Fact]
    public void Old_Config_Defaults_To_Certificate_Validation_And_Explicit_OptIn_Persists()
    {
        var cfg = JsonSerializer.Deserialize<AppConfig>("{\"Server\":\"server\",\"Database\":\"DateFactory\",\"AuthMode\":\"Windows\"}")!;
        Assert.False(cfg.TrustServerCertificate);
        Assert.False(new SqlConnectionStringBuilder(cfg.BuildSqlServerConnectionString()).TrustServerCertificate);
        cfg.TrustServerCertificate = true;
        var restored = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(cfg))!;
        Assert.True(restored.TrustServerCertificate);
    }

    [Theory]
    [InlineData("", "db", "Windows")]
    [InlineData("server", " ", "Sql")]
    [InlineData("server", "db", "Local")]
    public void Invalid_SQL_Settings_Are_Rejected(string server, string database, string mode)
    {
        Assert.Throws<ArgumentException>(() => SqlConnectionSettings.CreateBuilder(server, database, mode));
    }

    [Fact]
    public void Windows_Authentication_Does_Not_Include_Stale_SQL_Credentials()
    {
        var builder = SqlConnectionSettings.CreateBuilder("server", "db", "Windows", "old-user", "old-password");
        Assert.True(builder.IntegratedSecurity);
        Assert.Empty(builder.UserID);
        Assert.Empty(builder.Password);
        Assert.DoesNotContain("old-password", builder.ConnectionString);
    }
}

using System.Data;
using System.Data.Common;
using System.Reflection;
using System.Text.RegularExpressions;
using DatesErp.Core.Domain.Entities;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DatesErp.Tests;

/// <summary>No test connects to the customer's SQL Server. The repair ADO.NET double
/// records SQL order and simulates transaction rollback; EF SQL generation is offline.</summary>
public class FinishedGoodsReceiptSchemaRepairTests
{
    [Fact]
    public void SqlServer_Model_AndFreshTable_UseNullableIndexableNumber_AndUniqueFilteredIndex()
    {
        var options = new DbContextOptionsBuilder<DatesErpDbContext>()
            .UseSqlServer("Server=does-not-exist.invalid;Database=NeverConnect;TrustServerCertificate=True")
            .Options;
        using var db = new DatesErpDbContext(options); // NEVER opens the connection
        var entity = db.Model.FindEntityType(typeof(FinishedGoodsReceipt))!;
        var store = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        var number = entity.FindProperty(nameof(FinishedGoodsReceipt.ReceiptNumber))!;
        Assert.True(number.IsNullable); // drafts must remain NULL
        Assert.Equal(FinishedGoodsReceipt.ReceiptNumberMaxLength, number.GetMaxLength());
        Assert.Equal("nvarchar(100)", number.GetColumnType(store));

        var index = Assert.Single(entity.GetIndexes(), i => i.Properties.Count == 1 && i.Properties[0] == number);
        Assert.True(index.IsUnique);
        Assert.Equal(FinishedGoodsReceiptSchemaRepair.IndexName, index.GetDatabaseName());
        Assert.Equal("[ReceiptNumber] IS NOT NULL", index.GetFilter());

        // Generic SchemaMigrator creates a fresh table from this EF column type.
        var create = (string)typeof(SchemaMigrator)
            .GetMethod("BuildCreateTable", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { entity, entity.GetTableName()!, store, false })!;
        Assert.Contains("[ReceiptNumber] nvarchar(100)", create);
        Assert.DoesNotContain("[ReceiptNumber] nvarchar(max)", create, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"\[ReceiptNumber\]\s+nvarchar\(100\)\s+NOT NULL", create);

        var efScript = db.Database.GenerateCreateScript(); // provider-generated; no SQL connection
        Assert.Matches(new Regex(@"\[ReceiptNumber\]\s+nvarchar\(100\)\s+NULL", RegexOptions.IgnoreCase), efScript);
        Assert.Contains("CREATE UNIQUE INDEX [IX_FinishedGoodsReceipts_ReceiptNumber]", efScript);
        Assert.Contains("WHERE [ReceiptNumber] IS NOT NULL", efScript);
    }

    [Fact]
    public void LegacyMaxColumn_IsAlteredBeforeCreatingUniqueIndex_Atomically_AndRerunIsHarmless()
    {
        using var conn = new ReceiptSchemaConnection();
        conn.Open();
        FinishedGoodsReceiptSchemaRepair.Preflight(conn);
        var report = new List<string>();
        FinishedGoodsReceiptSchemaRepair.Apply(conn, report);

        Assert.Equal(200, conn.MaxBytes);
        Assert.True(conn.IndexExists);
        Assert.True(conn.Nullable);
        Assert.Equal(1, conn.Commits);
        Assert.Equal(0, conn.Rollbacks);
        int alter = conn.Sql.FindIndex(s => s.StartsWith("ALTER TABLE [dbo].[FinishedGoodsReceipts] ALTER COLUMN"));
        int index = conn.Sql.FindIndex(s => s.StartsWith("CREATE UNIQUE INDEX [IX_FinishedGoodsReceipts_ReceiptNumber]"));
        Assert.True(alter >= 0 && index > alter);
        Assert.Contains("COLLATE SQL_Latin1_General_CP1_CI_AS NULL", conn.Sql[alter]);
        Assert.Contains("WITH (TABLOCKX, HOLDLOCK)", string.Join("\n", conn.Sql));
        Assert.Contains(conn.Sql, s => s.Contains("DATALENGTH([ReceiptNumber]) > 200"));
        Assert.Contains(conn.Sql, s => s.Contains("GROUP BY CONVERT(nvarchar(100), [ReceiptNumber])"));
        Assert.DoesNotContain(conn.Sql, s => s.Contains("DROP ") || s.Contains("UPDATE ") || s.Contains("DELETE "));
        Assert.Equal(2, report.Count); // ALTER and index are reported only after commit

        int sqlCount = conn.Sql.Count;
        FinishedGoodsReceiptSchemaRepair.Preflight(conn);
        FinishedGoodsReceiptSchemaRepair.Apply(conn, report);
        Assert.Equal(1, conn.Commits);
        Assert.Equal(2, report.Count);
        Assert.DoesNotContain(conn.Sql.Skip(sqlCount), s => s.Contains("TABLOCKX") || s.StartsWith("ALTER ") || s.StartsWith("CREATE "));
    }

    [Fact]
    public void ExistingOversizedReceipt_StopsWholeMigration_BeforeAnyChange()
    {
        using var conn = new ReceiptSchemaConnection { OverlongRows = 2, OverlongMaxBytes = 204 };
        var options = new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlServer(conn).Options;
        using var db = new DatesErpDbContext(options);
        var report = SchemaMigrator.Migrate(db);
        Assert.Single(report);
        Assert.Contains("Id=17", report[0]);
        Assert.Contains("204", report[0]);
        Assert.StartsWith("خطأ", report[0]);
        Assert.DoesNotContain(conn.Sql, s => s.StartsWith("ALTER ") || s.StartsWith("CREATE ") || s.StartsWith("UPDATE "));
        Assert.Equal(-1, conn.MaxBytes);
        Assert.Equal(0, conn.Commits);
    }

    [Fact]
    public void DuplicateReceipt_StopsBeforeAlter_AndRepeatsChecksUnderLock()
    {
        using var conn = new ReceiptSchemaConnection { DuplicateRows = 2 };
        conn.Open();
        var error = Assert.Throws<InvalidOperationException>(() => FinishedGoodsReceiptSchemaRepair.Preflight(conn));
        Assert.Contains("مكرر", error.Message);
        Assert.Equal(-1, conn.MaxBytes);
        Assert.False(conn.IndexExists);

        // Even if a second client creates duplicates AFTER the first read-only preflight,
        // Apply checks again under an exclusive lock and rolls back without changing data.
        Assert.Throws<InvalidOperationException>(() => FinishedGoodsReceiptSchemaRepair.Apply(conn, new List<string>()));
        Assert.Equal(1, conn.Rollbacks);
        Assert.DoesNotContain(conn.Sql, s => s.StartsWith("ALTER ") || s.StartsWith("CREATE "));
    }

    [Fact]
    public void IndexCreationFailure_RollsBackColumnAlter_NoFalseSuccessReport()
    {
        using var conn = new ReceiptSchemaConnection { FailIndexCreation = true };
        conn.Open();
        var report = new List<string>();
        Assert.Throws<InvalidOperationException>(() => FinishedGoodsReceiptSchemaRepair.Apply(conn, report));
        Assert.Equal(-1, conn.MaxBytes);
        Assert.False(conn.IndexExists);
        Assert.Equal(0, conn.Commits);
        Assert.Equal(1, conn.Rollbacks);
        Assert.Empty(report);
    }

    [Fact]
    public void ExistingIndexWithWrongFilter_FailsClosed_InsteadOfSkippingByName()
    {
        using var conn = new ReceiptSchemaConnection { IndexExists = true, IndexFilter = "[ReceiptNumber] IS NULL" };
        conn.Open();
        var error = Assert.Throws<InvalidOperationException>(() => FinishedGoodsReceiptSchemaRepair.Preflight(conn));
        Assert.Contains("بتعريف مختلف", error.Message);
        Assert.DoesNotContain(conn.Sql, s => s.StartsWith("ALTER ") || s.StartsWith("CREATE "));
    }

    [Fact]
    public void UnexpectedExistingColumnType_FailsClosed()
    {
        using var conn = new ReceiptSchemaConnection { ColumnType = "varchar" };
        conn.Open();
        Assert.Throws<InvalidOperationException>(() => FinishedGoodsReceiptSchemaRepair.Preflight(conn));
        Assert.Equal(0, conn.TransactionStarts);
    }

    [Fact]
    public void ExistingCorrectColumnAndIndex_DoNotTakeExclusiveLock()
    {
        using var conn = new ReceiptSchemaConnection { MaxBytes = 200, IndexExists = true };
        conn.Open();
        FinishedGoodsReceiptSchemaRepair.Preflight(conn);
        FinishedGoodsReceiptSchemaRepair.Apply(conn, new List<string>());
        Assert.Equal(0, conn.TransactionStarts);
        Assert.DoesNotContain(conn.Sql, s => s.Contains("TABLOCKX"));
    }

    /// <summary>Minimal SQL Server-shaped ADO.NET connection for the repair's SQL only;
    /// no database, process, socket, or genuine SQL Server service is involved.</summary>
    private sealed class ReceiptSchemaConnection : DbConnection
    {
        private ConnectionState _state;
        public List<string> Sql { get; } = new();
        public short MaxBytes { get; set; } = -1;
        public bool Nullable { get; set; } = true;
        public string ColumnType { get; set; } = "nvarchar";
        public bool IndexExists { get; set; }
        public string IndexFilter { get; set; } = "([ReceiptNumber] IS NOT NULL)";
        public int OverlongRows { get; set; }
        public int OverlongMaxBytes { get; set; } = 204;
        public int DuplicateRows { get; set; }
        public bool FailIndexCreation { get; set; }
        public int TransactionStarts { get; private set; }
        public int Commits { get; private set; }
        public int Rollbacks { get; private set; }
        private bool _locked;

        public override string ConnectionString { get; set; } = "Server=not-a-real-server;Database=TestOnly";
        public override string Database => "TestOnly";
        public override string DataSource => "NotARealSqlServer";
        public override string ServerVersion => "16.0";
        public override ConnectionState State => _state;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() => _state = ConnectionState.Closed;
        public override void Open() => _state = ConnectionState.Open;
        protected override DbCommand CreateDbCommand() => new Command(this);
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            Assert.Equal(IsolationLevel.Serializable, isolationLevel);
            TransactionStarts++;
            return new Transaction(this, MaxBytes, Nullable, IndexExists);
        }

        private static DbDataReader Rows((string, Type)[] columns, params object?[][] rows)
        {
            var table = new DataTable();
            foreach (var (name, type) in columns) table.Columns.Add(name, type);
            foreach (var cells in rows) table.Rows.Add(cells.Select(x => x ?? DBNull.Value).ToArray());
            return table.CreateDataReader();
        }

        private DbDataReader Read(string sql)
        {
            Sql.Add(sql);
            if (sql.Contains("FROM sys.columns AS c"))
                return Rows(new[] { ("max_length", typeof(short)), ("is_nullable", typeof(bool)),
                    ("type", typeof(string)), ("collation", typeof(string)) },
                    new object?[] { MaxBytes, Nullable, ColumnType, "SQL_Latin1_General_CP1_CI_AS" });
            if (sql.Contains("FROM sys.indexes AS i"))
            {
                var columns = new[] { ("unique", typeof(bool)), ("disabled", typeof(bool)),
                    ("filter", typeof(string)), ("keys", typeof(int)), ("first", typeof(int)) };
                return IndexExists
                    ? Rows(columns, new object?[] { true, false, IndexFilter, 1, 1 })
                    : Rows(columns);
            }
            if (sql.Contains("MAX(DATALENGTH([ReceiptNumber]))"))
                return Rows(new[] { ("count", typeof(long)), ("id", typeof(int)), ("bytes", typeof(long)) },
                    new object?[] { (long)OverlongRows, OverlongRows > 0 ? 17 : null, OverlongRows > 0 ? (long)OverlongMaxBytes : null });
            if (sql.Contains("GROUP BY CONVERT(nvarchar(100)"))
            {
                var columns = new[] { ("id", typeof(int)), ("count", typeof(long)) };
                return DuplicateRows > 1 ? Rows(columns, new object?[] { 21, (long)DuplicateRows }) : Rows(columns);
            }
            if (sql.Contains("TABLOCKX"))
            {
                _locked = true;
                return Rows(new[] { ("Id", typeof(int)) }, new object?[] { 1 });
            }
            throw new InvalidOperationException("Unexpected SQL: " + sql);
        }

        private int Write(string sql)
        {
            Sql.Add(sql);
            if (!_locked) throw new InvalidOperationException("DDL without exclusive lock");
            if (sql.StartsWith("ALTER TABLE [dbo].[FinishedGoodsReceipts] ALTER COLUMN"))
            {
                MaxBytes = 200;
                Nullable = true;
                return 0;
            }
            if (sql.StartsWith("CREATE UNIQUE INDEX [IX_FinishedGoodsReceipts_ReceiptNumber]"))
            {
                if (FailIndexCreation) throw new InvalidOperationException("Simulated SQL index failure");
                if (MaxBytes != 200) throw new InvalidOperationException("Unindexable column");
                IndexExists = true;
                return 0;
            }
            throw new InvalidOperationException("Unexpected SQL: " + sql);
        }

        private sealed class Transaction : DbTransaction
        {
            private readonly ReceiptSchemaConnection _connection;
            private readonly short _maxBytes;
            private readonly bool _nullable, _indexExists;
            public Transaction(ReceiptSchemaConnection connection, short maxBytes, bool nullable, bool indexExists)
                => (_connection, _maxBytes, _nullable, _indexExists) = (connection, maxBytes, nullable, indexExists);
            public override IsolationLevel IsolationLevel => IsolationLevel.Serializable;
            protected override DbConnection DbConnection => _connection;
            public override void Commit() { _connection.Commits++; _connection._locked = false; }
            public override void Rollback()
            {
                _connection.Rollbacks++;
                _connection.MaxBytes = _maxBytes;
                _connection.Nullable = _nullable;
                _connection.IndexExists = _indexExists;
                _connection._locked = false;
            }
        }

        private sealed class Command : DbCommand
        {
            private readonly ReceiptSchemaConnection _connection;
            public Command(ReceiptSchemaConnection connection) => _connection = connection;
            public override string CommandText { get; set; } = "";
            public override int CommandTimeout { get; set; } = 30;
            public override CommandType CommandType { get; set; } = CommandType.Text;
            public override UpdateRowSource UpdatedRowSource { get; set; }
            public override bool DesignTimeVisible { get; set; }
            protected override DbConnection DbConnection { get => _connection; set => throw new NotSupportedException(); }
            protected override DbTransaction? DbTransaction { get; set; }
            protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
            public override void Cancel() { }
            public override void Prepare() { }
            protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
            public override int ExecuteNonQuery() => _connection.Write(CommandText);
            public override object? ExecuteScalar()
            {
                using var reader = ExecuteDbDataReader(CommandBehavior.Default);
                return reader.Read() ? reader.GetValue(0) : null;
            }
            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => _connection.Read(CommandText);
        }
    }
}

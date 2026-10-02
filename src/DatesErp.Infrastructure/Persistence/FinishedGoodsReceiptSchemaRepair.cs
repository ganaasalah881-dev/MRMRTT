using System.Data;
using DatesErp.Core.Domain.Entities;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DatesErp.Tests")]

namespace DatesErp.Infrastructure.Persistence;

/// <summary>
/// SQL Server upgrade for a partially migrated installation whose existing RCV column is
/// nvarchar(max). The generic migrator adds missing columns but cannot change an existing
/// column's type. Never truncate, renumber, deduplicate, or silently replace an index.
/// The verified pre-migration backup is enforced by Bootstrapper, before Migrate is called.
/// </summary>
internal static class FinishedGoodsReceiptSchemaRepair
{
    internal const string TableName = "FinishedGoodsReceipts";
    internal const string IndexName = "IX_FinishedGoodsReceipts_ReceiptNumber";
    private const string SqlTable = "[dbo].[FinishedGoodsReceipts]";
    private const int TargetBytes = FinishedGoodsReceipt.ReceiptNumberMaxLength * 2; // UTF-16, not LEN() characters

    private readonly record struct ColumnInfo(short MaxBytes, bool Nullable, string TypeName, string Collation);

    /// <summary>
    /// Read-only fail-fast check, before the generic migrator touches ANY tables. A second
    /// check inside a locked transaction in Apply closes the race with other clients.
    /// </summary>
    internal static void Preflight(IDbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        var column = ReadColumn(cmd);
        if (column is null) return; // No table/column yet: generic migration will create it.
        ValidateColumn(column.Value);
        bool indexed = HasValidIndex(cmd);
        if (column.Value.MaxBytes == TargetBytes && column.Value.Nullable && indexed) return;
        ValidateData(cmd);
    }

    /// <summary>For existing SQL Server tables only; called before EnsureIndexes.</summary>
    internal static void Apply(IDbConnection conn, List<string> report)
    {
        using (var check = conn.CreateCommand())
        {
            var current = ReadColumn(check)
                ?? throw new InvalidOperationException($"تعذر العثور على العمود dbo.{TableName}.ReceiptNumber بعد إضافة الأعمدة المفقودة.");
            ValidateColumn(current);
            if (current.MaxBytes == TargetBytes && current.Nullable && HasValidIndex(check)) return;
        }

        // Exclusive table lock held from BOTH safety checks through ALTER and CREATE INDEX.
        // SQL Server DDL and the new index either commit together or roll back together.
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            // COUNT_BIG takes the table X lock even if the table is currently empty.
            cmd.CommandText = $"SELECT COUNT_BIG(*) FROM {SqlTable} WITH (TABLOCKX, HOLDLOCK)";
            cmd.ExecuteScalar();

            var column = ReadColumn(cmd)
                ?? throw new InvalidOperationException($"تعذر العثور على العمود dbo.{TableName}.ReceiptNumber أثناء الترحيل.");
            ValidateColumn(column);
            bool indexed = HasValidIndex(cmd);
            ValidateData(cmd); // DATALENGTH includes trailing spaces and UTF-16 surrogate pairs.

            bool altered = column.MaxBytes != TargetBytes || !column.Nullable;
            if (altered)
            {
                if (indexed)
                    throw new InvalidOperationException($"لا يمكن تغيير {TableName}.ReceiptNumber مع وجود فهرس معتمد عليه؛ يلزم فحص تعريفه أولاً.");
                cmd.CommandText = $"ALTER TABLE {SqlTable} ALTER COLUMN [ReceiptNumber] nvarchar({FinishedGoodsReceipt.ReceiptNumberMaxLength}) COLLATE {column.Collation} NULL";
                cmd.ExecuteNonQuery();
            }

            if (!indexed)
            {
                cmd.CommandText = $"CREATE UNIQUE INDEX [{IndexName}] ON {SqlTable} ([ReceiptNumber]) WHERE [ReceiptNumber] IS NOT NULL";
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            if (altered) report.Add($"تم تصحيح نوع العمود: {TableName}.ReceiptNumber إلى nvarchar({FinishedGoodsReceipt.ReceiptNumberMaxLength}) NULL دون قص البيانات.");
            if (!indexed) report.Add($"تم إنشاء الفهرس المفقود: {TableName}.{IndexName}");
        }
        catch
        {
            try { tx.Rollback(); } catch { /* Preserve the original SQL error. */ }
            throw;
        }
    }

    private static ColumnInfo? ReadColumn(IDbCommand cmd)
    {
        cmd.CommandText = $"""
            SELECT c.max_length, c.is_nullable, t.name, c.collation_name
            FROM sys.columns AS c
            JOIN sys.types AS t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID(N'dbo.{TableName}', N'U') AND c.name = N'ReceiptNumber'
            """;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new ColumnInfo(reader.GetInt16(0), reader.GetBoolean(1), reader.GetString(2),
            reader.IsDBNull(3) ? "" : reader.GetString(3));
    }

    private static void ValidateColumn(ColumnInfo column)
    {
        if (!string.Equals(column.TypeName, "nvarchar", StringComparison.OrdinalIgnoreCase)
            || (column.MaxBytes != -1 && (column.MaxBytes <= 0 || column.MaxBytes % 2 != 0))
            || string.IsNullOrEmpty(column.Collation)
            || column.Collation.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
        {
            throw new InvalidOperationException($"نوع/ترتيب العمود {TableName}.ReceiptNumber غير متوقع؛ لا يُغيَّر تلقائياً. راجع مخطط SQL Server قبل إعادة المحاولة.");
        }
    }

    private static bool HasValidIndex(IDbCommand cmd)
    {
        cmd.CommandText = $"""
            SELECT i.is_unique, i.is_disabled, i.filter_definition,
                (SELECT COUNT(*) FROM sys.index_columns AS ic
                 WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0),
                (SELECT COUNT(*) FROM sys.index_columns AS ic
                 JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                 WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                   AND ic.key_ordinal = 1 AND c.name = N'ReceiptNumber')
            FROM sys.indexes AS i
            WHERE i.object_id = OBJECT_ID(N'dbo.{TableName}', N'U') AND i.name = N'{IndexName}'
            """;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return false;
        var filter = reader.IsDBNull(2) ? "" : reader.GetString(2);
        // SQL Server sometimes wraps filter_definition in parentheses or brackets.
        var normalized = new string(filter.Where(c => !char.IsWhiteSpace(c) && c != '[' && c != ']'
                                              && c != '(' && c != ')').ToArray());
        bool valid = reader.GetBoolean(0) && !reader.GetBoolean(1)
            && normalized.Equals("ReceiptNumberISNOTNULL", StringComparison.OrdinalIgnoreCase)
            && reader.GetInt32(3) == 1 && reader.GetInt32(4) == 1;
        if (!valid)
            throw new InvalidOperationException($"الفهرس {IndexName} موجود بتعريف مختلف؛ لن يُستبدل أو يُتجاوز تلقائياً. راجع فريد/مصفّى/أعمدته.");
        return true;
    }

    private static void ValidateData(IDbCommand cmd)
    {
        // COUNT_BIG reports EVERY offending row; DATALENGTH, unlike LEN, counts trailing
        // spaces. The first Id and maximum size help locate data without logging its content.
        cmd.CommandText = $"""
            SELECT COUNT_BIG(*), MIN([Id]), MAX(DATALENGTH([ReceiptNumber]))
            FROM {SqlTable}
            WHERE [ReceiptNumber] IS NOT NULL AND DATALENGTH([ReceiptNumber]) > {TargetBytes}
            """;
        using (var reader = cmd.ExecuteReader())
        {
            if (reader.Read() && reader.GetInt64(0) > 0)
                throw new InvalidOperationException($"تعذر تضييق {TableName}.ReceiptNumber: عدد القيم الأطول من {FinishedGoodsReceipt.ReceiptNumberMaxLength} وحدة UTF-16: {reader.GetInt64(0)}؛ مثال Id={reader.GetInt32(1)}، أكبر طول بالبايت={Convert.ToInt64(reader.GetValue(2))}. لم تُقص أي قيمة؛ يلزم تسوية البيانات بموافقة المسؤول قبل إعادة الترحيل.");
        }

        // CONVERT is safe ONLY after the DATALENGTH check. It preserves the column's
        // collation; SQL Server GROUP BY therefore finds the duplicates that would make
        // the filtered UNIQUE index fail (including case-insensitive matches).
        cmd.CommandText = $"""
            SELECT TOP (1) MIN([Id]), COUNT_BIG(*)
            FROM {SqlTable}
            WHERE [ReceiptNumber] IS NOT NULL
            GROUP BY CONVERT(nvarchar({FinishedGoodsReceipt.ReceiptNumberMaxLength}), [ReceiptNumber])
            HAVING COUNT_BIG(*) > 1
            ORDER BY COUNT_BIG(*) DESC, MIN([Id])
            """;
        using var duplicate = cmd.ExecuteReader();
        if (duplicate.Read())
            throw new InvalidOperationException($"تعذر إنشاء الفهرس الفريد {IndexName}: رقم سند مكرر حسب ترتيب SQL Server (مثال Id={duplicate.GetInt32(0)}، عدد النسخ={duplicate.GetInt64(1)}). لم تتغير البيانات؛ يلزم تسوية التكرار بموافقة المسؤول.");
    }
}

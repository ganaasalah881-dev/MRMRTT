using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Connection;
using DatesErp.Infrastructure.Persistence;
using DatesErp.Infrastructure.Session;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Infrastructure;

/// <summary>تركيب طبقة البنية التحتية في حاوية الخدمات.</summary>
public static class DependencyInjection
{
    /// <summary>تسجيل سياق قاعدة البيانات حسب الإعداد: SQL Server مركزي أو SQLite (اختبارات).</summary>
    public static IServiceCollection AddDatesErpInfrastructure(this IServiceCollection services, Action<DbContextOptionsBuilder> configure = null)
    {
        services.AddSingleton<SessionContext>();
        services.AddSingleton<ICurrentSession>(sp => sp.GetRequiredService<SessionContext>());
        services.AddSingleton<AuditSaveChangesInterceptor>();
        services.AddSingleton<ConnectionTester>();
        // §مهم: ترقيم المستندات يجب أن يكون Scoped حتى تُحفظ زيادة التسلسل مع معاملة المستند نفسها
        // (كان Singleton من قبل فلا تُحفظ الزيادة أبداً ← أرقام مكررة ← UNIQUE constraint)
        services.AddScoped<INumberingService, NumberingService>();

        services.AddDbContext<DatesErpDbContext>((sp, options) =>
        {
            options.AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>());
            if (configure != null) configure(options);
            else
            {
                var cfg = AppConfig.Load();
                if (cfg != null) options.UseSqlServer(cfg.BuildSqlServerConnectionString());
                else options.UseSqlite("Data Source=dateerp_dev.db");
            }
        });

        return services;
    }
}

/// <summary>
/// ترقيم المستندات المركزي — تسلسل آمن داخل معاملة.
/// §الحماية من التكرار (طبقتان):
///  • يُصلح المخططات المفقودة تلقائياً (ينشئ المخطط إن لم يوجد).
///  • حلقة ضمان: يتقدّم حتى رقم غير مستخدم — فيُصلح تلقائياً أي قاعدة تسلسلها غير
///    متزامن (كان الرقم يُكرر قديماً لأن الخدمة كانت Singleton فلا تُحفظ الزيادة أبداً).
/// §B84/C1 (صدق توثيقي): الحلقة تفحص المحفوظ فقط، فجهازان متزامنان قد يولّدان نفس الرقم.
///    يُعالَج بإعادة المحاولة التلقائية في ServiceBase.RunInTransaction عند تعارض القيد الفريد.
/// §B110: على SQL Server أصبح صف المخطط يُقفل بقفل تحديث داخل معاملة المستدعي،
///    فيتسلسل الطلبان المتزامنان ولا يتصادمان أصلاً (الإعادة تبقى شبكة أمان لبقية المزودين).
/// </summary>
public class NumberingService : INumberingService
{
    private readonly DatesErpDbContext _db;

    public NumberingService(DatesErpDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// §ترقيم نقي 1.50.53 — كل نوع مستند له تسلسل نقي 1،2،3… بلا بادئة ولا سنة.
    /// المستندات القديمة (REC-2026-0001…) تبقى كما هي — لا تُمس.
    /// التسلسل الجديد يبدأ من 1 عند أول استخدام نقي، ويستمر مستقلاً عن العداد القديم.
    /// </summary>
    public string Next(string schemeCode)
    {
        var scheme = GetOrCreateScheme(schemeCode);
        int maxPure = GetMaxPureNumber(schemeCode);
        // Migrate the legacy prefixed counter ONCE. Prefix is no longer used
        // when rendering numbers, so changing it to the scheme code records
        // that the pure sequence has begun, even when no document was saved
        // after a reservation. Resetting LastSequence on every call when the
        // document table is empty repeated "1" forever (and on TRT it broke
        // every second treatment with a UNIQUE constraint violation).
        if (maxPure == 0 && !string.Equals(scheme.Prefix, schemeCode, StringComparison.OrdinalIgnoreCase))
        {
            scheme.LastSequence = 0;
            scheme.Prefix = schemeCode;
        }
        else if (maxPure > scheme.LastSequence)
            scheme.LastSequence = maxPure;

        string number;
        do
        {
            scheme.LastSequence += 1;
            number = scheme.LastSequence.ToString();
        } while (IsUsed(schemeCode, number));

        return number;
    }

    public string Peek(string schemeCode)
    {
        var scheme = GetOrCreateScheme(schemeCode);
        int maxPure = GetMaxPureNumber(schemeCode);
        bool unconvertedLegacy = maxPure == 0
            && !string.Equals(scheme.Prefix, schemeCode, StringComparison.OrdinalIgnoreCase);
        int next = (unconvertedLegacy ? 0 : Math.Max(maxPure, scheme.LastSequence)) + 1;
        // تخطي المحجوز إن وجد
        while (IsUsed(schemeCode, next.ToString())) next++;
        return next.ToString();
    }

    public string Reserve(string schemeCode)
    {
        var num = Next(schemeCode);
        _db.SaveChanges();
        return num;
    }

    private NumberingScheme GetOrCreateScheme(string schemeCode)
    {
        NumberingScheme? scheme = _db.Database.IsSqlServer()
            ? _db.NumberingSchemes
                .FromSqlRaw("SELECT * FROM [NumberingSchemes] WITH (UPDLOCK, HOLDLOCK) WHERE [SchemeCode] = {0}", schemeCode)
                .FirstOrDefault()
            : _db.NumberingSchemes.FirstOrDefault(s => s.SchemeCode == schemeCode);
        if (scheme == null)
        {
            scheme = new NumberingScheme
            {
                SchemeCode = schemeCode,
                SchemeName = schemeCode,
                Prefix = schemeCode,
                LastSequence = 0
            };
            _db.NumberingSchemes.Add(scheme);
        }
        return scheme;
    }

    /// <summary>أكبر رقم نقي موجود في جدول المخطط (يُقرأ كـ int).</summary>
    private int GetMaxPureNumber(string schemeCode)
    {
        try
        {
            var list = schemeCode switch
            {
                "SHIP" => _db.Shipments.AsNoTracking().Select(x => x.DocumentNumber).ToList(),
                "PLAN" or "PLN" => _db.ProductionPlans.AsNoTracking().Select(x => x.DocumentNumber).ToList(),
                "ORD" => _db.ProductionOrders.AsNoTracking().Select(x => x.DocumentNumber).ToList(),
                "EXE" => _db.ProductionExecutions.AsNoTracking().Select(x => x.DocumentNumber).ToList(),
                "QC" => _db.QualityChecks.AsNoTracking().Select(x => x.DocumentNumber).ToList(),
                "TRT" => _db.RawTreatments.AsNoTracking().Select(x => x.TreatmentNo).ToList(),
                "PDL" => _db.ProductionDeliveries.AsNoTracking().Select(x => x.DocumentNumber).ToList(),
                "FGR" => _db.FinishedGoodsReceipts.AsNoTracking().Select(x => x.DocumentNumber).ToList(),
                "RCV" => _db.FinishedGoodsReceipts.AsNoTracking().Select(x => x.ReceiptNumber).ToList(),
                "CD" => _db.CustomerDeliveries.AsNoTracking().Select(x => x.DocumentNumber).ToList(),
                "TXN" => _db.InventoryTransactions.AsNoTracking().Select(x => x.TxnNumber).ToList(),
                "CTX" => _db.InventoryTransactions.AsNoTracking().Select(x => x.TxnNumber).ToList(),
                "CCD" => _db.CartonCountDocs.AsNoTracking().Select(x => x.DocumentNumber).ToList(),
                "CSD" => _db.CartonSaleDocs.AsNoTracking().Select(x => x.DocumentNumber).ToList(),
                "PCL" => _db.PlanClosings.AsNoTracking().Select(x => x.DocumentNumber).ToList(),
                "LOT" => _db.Lots.AsNoTracking().Select(x => x.LotCode).ToList(),
                "TASK" => _db.WorkflowTasks.AsNoTracking().Select(x => x.TaskNumber).ToList(),
                _ => new List<string>()
            };
            int max = 0;
            foreach (var s in list)
            {
                if (int.TryParse(s, out var n) && n > max) max = n;
            }
            return max;
        }
        catch { return 0; }
    }

    private bool HasAnyPure(string schemeCode) => GetMaxPureNumber(schemeCode) > 0;

    /// <summary>هل هذا الرقم مستخدم فعلاً في جدول المخطط؟</summary>
    private bool IsUsed(string schemeCode, string number) => schemeCode switch
    {
        "SHIP" => _db.Shipments.Any(x => x.DocumentNumber == number),
        "PLAN" or "PLN" => _db.ProductionPlans.Any(x => x.DocumentNumber == number),
        "ORD" => _db.ProductionOrders.Any(x => x.DocumentNumber == number),
        "EXE" => _db.ProductionExecutions.Any(x => x.DocumentNumber == number),
        "QC" => _db.QualityChecks.Any(x => x.DocumentNumber == number),
        "TRT" => _db.RawTreatments.Any(x => x.TreatmentNo == number),
        "PDL" => _db.ProductionDeliveries.Any(x => x.DocumentNumber == number),
        "FGR" => _db.FinishedGoodsReceipts.Any(x => x.DocumentNumber == number),
        "RCV" => _db.FinishedGoodsReceipts.Any(x => x.ReceiptNumber == number),
        "CD" => _db.CustomerDeliveries.Any(x => x.DocumentNumber == number),
        "TXN" => _db.InventoryTransactions.Any(x => x.TxnNumber == number),
        "CTX" => _db.InventoryTransactions.Any(x => x.TxnNumber == number),
        "CCD" => _db.CartonCountDocs.Any(x => x.DocumentNumber == number),
        "CSD" => _db.CartonSaleDocs.Any(x => x.DocumentNumber == number),
        "PCL" => _db.PlanClosings.Any(x => x.DocumentNumber == number),
        "LOT" => _db.Lots.Any(x => x.LotCode == number),
        "TASK" => _db.WorkflowTasks.Any(x => x.TaskNumber == number),
        _ => false
    };
}

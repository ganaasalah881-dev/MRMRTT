using DatesErp.Core.Common;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>§25 — محرك التقارير المركزي: بيانات حية من القاعدة المركزية (مستلم/مصروف/متبقي).</summary>
public partial class ReportService : ServiceBase, IReportService
{
    public ReportService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    public List<ReportDefinition> GetReports()
    {
        var customerOpts = Db.Customers.AsNoTracking().OrderBy(c => c.CustomerName)
            .Select(c => new { c.Id, c.CustomerName }).ToList().Select(x => (x.Id.ToString(), x.CustomerName)).ToList();
        var productOpts = Db.Products.AsNoTracking().OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.ProductNameAr }).ToList().Select(x => (x.Id.ToString(), x.ProductNameAr)).ToList();
        var list = new List<ReportDefinition>
    {
        new ReportDefinition { Code = "receiving_line_treatment", TitleAr = "معالجة بنود الاستلام — نعم/لا وحتى تاريخ", Category = "الاستلام", Parameters = DateRange() },
        new ReportDefinition { Code = "receiving", TitleAr = "تقارير الاستلام", Category = "الاستلام", Parameters = DateRange() },
        new ReportDefinition { Code = "inventory", TitleAr = "تقارير المخزون (الأرصدة)", Category = "المخزون" },
        new ReportDefinition { Code = "customers", TitleAr = "تقارير العملاء وأرصدتهم", Category = "العملاء" },
        new ReportDefinition { Code = "lots", TitleAr = "تقارير الدفعات Lots", Category = "الدفعات" },
        new ReportDefinition { Code = "plans", TitleAr = "خطط الإنتاج", Category = "الإنتاج", Parameters = DateRange() },
        new ReportDefinition { Code = "orders", TitleAr = "أوامر الإنتاج", Category = "الإنتاج", Parameters = DateRange() },
        new ReportDefinition { Code = "material_consumption", TitleAr = "تقارير استهلاك المواد", Category = "المواد" },
        new ReportDefinition { Code = "aux_consumption_by_order", TitleAr = "استهلاك الأصناف المساعدة حسب أمر الإنتاج", Category = "المواد", Parameters = new List<ReportParameter>{ new ReportParameter{ Key="order", LabelAr="أمر الإنتاج", Kind="text" } } },
        new ReportDefinition { Code = "aux_period_consumption", TitleAr = "استهلاك صنف مساعد خلال فترة", Category = "المواد", Parameters = DateRange() },
        new ReportDefinition { Code = "aux_setup", TitleAr = "تهيئة الأصناف المساعدة", Category = "المواد" },
        new ReportDefinition { Code = "aux_bom", TitleAr = "مكونات الإنتاج (BOM) — احتياجات الصنف التام", Category = "المواد" },
        new ReportDefinition { Code = "production", TitleAr = "الإنتاج المنفذ (جلسات التشغيل)", Category = "الإنتاج", Parameters = DateRange() },
        new ReportDefinition { Code = "quality", TitleAr = "فحوصات الجودة", Category = "الجودة", Parameters = DateRange() },
        new ReportDefinition { Code = "wastage", TitleAr = "الهالك والأصناف الثانوية", Category = "الجودة" },
        new ReportDefinition { Code = "finished_goods", TitleAr = "تقارير الإنتاج التام", Category = "المخزون" },
        new ReportDefinition { Code = "delivery", TitleAr = "تسليم العملاء", Category = "التسليم", Parameters = DateRange() },
        new ReportDefinition { Code = "delivery_analysis", TitleAr = "تسليم العملاء — تحليلي (صنف/دفعة/خطة/أمر/صفة + مجاميع)", Category = "التسليم", Parameters = DateRange() },
        new ReportDefinition { Code = "movements", TitleAr = "تقارير حركة المخزون", Category = "المخزون", Parameters = DateRange() },
        new ReportDefinition { Code = "audit", TitleAr = "تقارير التدقيق", Category = "الإدارة", Parameters = DateRange() },
        new ReportDefinition { Code = "management", TitleAr = "تقارير الإدارة (مؤشرات)", Category = "الإدارة" },
        new ReportDefinition
        {
            Code = "item_journey",
            TitleAr = "تتبع الصنف — الرحلة الكاملة (استلام ← إنتاج ← فحص ← تسليم)",
            Category = "التتبع",
            Parameters = new List<ReportParameter>
            {
                new ReportParameter { Key = "customer", LabelAr = "العميل", Kind = "list", Options = customerOpts },
                new ReportParameter { Key = "product", LabelAr = "الصنف", Kind = "list", Options = productOpts }
            }
        }
    };
        // §مرحلة التقارير: تقارير العمليات والتقارير الشاملة (المحرك الجديد)
        list.AddRange(GetNewReportDefinitions());
        // §خيارات الشحنات الفعلية لفلتر تتبع الشحنة (أحدث 100)
        foreach (var d in list.Where(x => x.Code == "shipment_tracking"))
        {
            var prm = d.Parameters.First(x => x.Key == "shipment");
            prm.Options = Db.Shipments.AsNoTracking().OrderByDescending(x => x.Id).Take(100).ToList()
                .Select(x => ((string)x.Id.ToString(), $"{x.DocumentNumber} ({x.TotalWeightKg:N0} كجم)")).ToList();
        }
        // §العملية الشاملة لتطوير التقارير: الحزمة الاحترافية (7 تقارير)
        list.AddRange(GetProfessionalDefinitions());
        // §المعالجة والتعقيم: حزمة تقارير الدورة (سجل + متأخرات + أداء المدد).
        list.AddRange(GetTreatmentDefinitions());
        return list;
    }

    /// <summary>§نص الفترة للترويسة — يظهر دائماً حتى بلا فلتر.</summary>
    private static string PeriodText(DateTime? from, DateTime? to)
    {
        if (from == null && to == null) return $"من البداية حتى {DateTime.Today:dd/MM/yyyy} (بلا تحديد فترة)";
        return $"{(from != null ? from.Value.ToString("dd/MM/yyyy") : "البداية")} ← {(to != null ? to.Value.ToString("dd/MM/yyyy") : "اليوم")}";
    }

    private static List<ReportParameter> DateRange() => new()
    {
        new ReportParameter { Key = "from", LabelAr = "من تاريخ", Kind = "date" },
        new ReportParameter { Key = "to", LabelAr = "إلى تاريخ", Kind = "date" }
    };

    public ReportResult Run(string reportCode, Dictionary<string, string> parameters)
    {
        Require("reports", "View");
        parameters ??= new Dictionary<string, string>();
        DateTime? from = parameters.TryGetValue("from", out var f) && UiFormat.TryParseDate(f, out var fd) ? fd : null;
        DateTime? to = parameters.TryGetValue("to", out var t) && UiFormat.TryParseDate(t, out var td) ? td : null;
        // §تتبع الصنف: تصفية التقارير بالعميل و/أو الصنف
        int? custId = parameters.TryGetValue("customer", out var cp) && int.TryParse(cp, out var cVal) ? cVal : null;
        int? prodId = parameters.TryGetValue("product", out var pp) && int.TryParse(pp, out var pVal) ? pVal : null;

        var r = new ReportResult();
            r.RowLinks = new List<DocLinkDto>();   // §زر «+» للتنقل إلى المستند المصدر
        switch (reportCode)
        {
            case "receiving_line_treatment":
            {
                r.TitleAr = "معالجة بنود الاستلام — القرار والتاريخ والحالة لكل بند";
                r.Columns.AddRange(new[] { "رقم الاستلام", "تاريخ الاستلام", "العميل", "معرف البند", "الصنف", "الوحدة", "الكمية (كجم)", "العبوة", "المعالجة", "حتى تاريخ", "الحالة", "اكتملت فعلياً" });
                var query = Db.Shipments.AsNoTracking().Include(s => s.Items).AsQueryable();
                if (from != null) query = query.Where(s => s.ReceivedDate >= from);
                if (to != null) query = query.Where(s => s.ReceivedDate < to.Value.Date.AddDays(1));
                if (custId != null) query = query.Where(s => s.CustomerId == custId);
                var receiving = new ReceivingService(Db, Session, Numbering);
                foreach (var ship in query.OrderByDescending(s => s.Id).ToList())
                {
                    var states = receiving.ReadTreatmentStates(ship.Id).ToDictionary(x => x.ShipmentItemId);
                    foreach (var item in ship.Items.Where(i => prodId == null || i.ProductId == prodId).OrderBy(i => i.Id))
                    {
                        var state = states[item.Id];
                        r.Rows.Add(new object[] { ship.DocumentNumber, ship.ReceivedDate?.ToString("dd/MM/yyyy"),
                            Db.Customers.Where(c => c.Id == ship.CustomerId).Select(c => c.CustomerName).FirstOrDefault(), item.Id,
                            Db.Products.Where(p => p.Id == item.ProductId).Select(p => p.ProductNameAr).FirstOrDefault(),
                            item.ReceiptUnit, item.TotalWeightKg,
                            Db.PackagingTypes.Where(p => p.Id == item.PackagingTypeId).Select(p => p.PackageNameAr).FirstOrDefault(),
                            item.TreatmentRequired == null ? "سجل سابق/يلزم تحديد القرار" : item.TreatmentRequired.Value ? "نعم" : "لا",
                            state.UntilDate?.ToString("dd/MM/yyyy"), state.StateAr, state.CompletedAt?.ToString("dd/MM/yyyy HH:mm") });
                        r.RowLinks.Add(new DocLinkDto { DocType = "receiving", Id = ship.Id });
                    }
                }
                r.Summary["إجمالي الكمية (كجم)"] = r.Rows.Sum(x => Convert.ToDouble(x[6])).ToString("N1");
                break;
            }
            case "receiving":
            {
                r.TitleAr = "تقرير الاستلام";
                r.Columns.AddRange(new[] { "رقم الاستلام", "التاريخ", "العميل", "عدد البنود", "إجمالي الوزن (كجم)", "الحالة" });
                var q = Db.Shipments.Include(s => s.Items).AsQueryable();
                if (from != null) q = q.Where(s => s.ReceivedDate >= from);
                if (to != null) q = q.Where(s => s.ReceivedDate <= to.Value.AddDays(1));
                if (custId != null) q = q.Where(s => s.CustomerId == custId);
                if (prodId != null) q = q.Where(s => s.Items.Any(i => i.ProductId == prodId));
                foreach (var s in q.OrderByDescending(s => s.Id))
                {
                    var cust = Db.Customers.Where(c => c.Id == s.CustomerId).Select(c => c.CustomerName).FirstOrDefault();
                    r.Rows.Add(new object[] { s.DocumentNumber, s.ReceivedDate?.ToString("dd/MM/yyyy"), cust, s.Items.Count, s.TotalWeightKg, Core.Common.DocStatuses.ToArabic(s.Status) });
                    r.RowLinks.Add(new DocLinkDto { DocType = "receiving", Id = s.Id });
                }
                r.Summary["إجمالي الاستلام (كجم)"] = r.Rows.Sum(x => Convert.ToDouble(x[4])).ToString("N1");
                break;
            }
            case "inventory":
            {
                r.TitleAr = "تقرير أرصدة المخزون — كل عبوة منفصلة (4كجم/8كجم)";
                r.Columns.AddRange(new[] { "المخزن", "الصنف/المادة", "الدفعة", "العميل", "العبوة", "الرصيد (كجم)", "عدد العبوات" });
                foreach (var b in Db.StockBalances.Where(b => b.QtyKg != 0 || b.PackageCount != 0))
                {
                    r.Rows.Add(new object[]
                    {
                        Db.Warehouses.Where(w => w.Id == b.WarehouseId).Select(w => w.WarehouseNameAr).FirstOrDefault(),
                        b.ProductId != null ? Db.Products.Where(p => p.Id == b.ProductId).Select(p => p.ProductNameAr).FirstOrDefault()
                                            : Db.AuxiliaryMaterials.Where(m => m.Id == b.MaterialId).Select(m => m.MaterialNameAr).FirstOrDefault(),
                        Db.Lots.Where(l => l.Id == b.LotId).Select(l => l.LotCode).FirstOrDefault(),
                        Db.Customers.Where(c => c.Id == b.CustomerId).Select(c => c.CustomerName).FirstOrDefault(),
                        b.PackagingTypeId != null ? Db.PackagingTypes.Where(p => p.Id == b.PackagingTypeId).Select(p => p.PackageNameAr).FirstOrDefault() : "بدون عبوة",
                        b.QtyKg, b.PackageCount
                    });
                }
                break;
            }
            case "customers":
            {
                r.TitleAr = "تقرير العملاء وأرصدة الإنتاج التام";
                // §القاعدة الذهبية: المنتج التام رسمي بالكرتون والكجم بيان إضافي
                r.Columns.AddRange(new[] { "العميل", "الهاتف", UnitsPolicy.BalanceCtnHeader, UnitsPolicy.BalanceKgHeader, "المسلَّم (كرتون)", "المسلَّم (كجم)" });
                foreach (var c in Db.Customers.Where(c => c.IsActive))
                {
                    double fgCtn = Db.StockBalances.Where(b => b.CustomerId == c.Id && b.QtyKg > 0).Sum(b => (double)b.PackageCount);
                    double fg = Db.StockBalances.Where(b => b.CustomerId == c.Id && b.QtyKg > 0).Sum(b => b.QtyKg);
                    double deliveredCtn = Db.CustomerDeliveries.Where(d => d.CustomerId == c.Id && d.IsApproved).Sum(d => (double)d.TotalCartons);
                    double delivered = Db.CustomerDeliveries.Where(d => d.CustomerId == c.Id && d.IsApproved).Sum(d => d.TotalQtyKg);
                    r.Rows.Add(new object[] { c.CustomerName, c.Phone, fgCtn, fg, deliveredCtn, delivered });
                }
                break;
            }
            case "lots":
            {
                r.TitleAr = "تقرير الدفعات (المستلم / المصروف للإنتاج / المتبقي)";
                r.Columns.AddRange(new[] { "الدفعة", "الصنف", "العميل", "المستلم (كجم)", "المصروف للإنتاج (كجم)", "المتبقي (كجم)", "المسلَّم (كجم)" });
                var lotsQ = Db.Lots.AsQueryable();
                if (custId != null) lotsQ = lotsQ.Where(l => l.CustomerId == custId);
                if (prodId != null) lotsQ = lotsQ.Where(l => l.ProductId == prodId);
                foreach (var l in lotsQ)
                {
                    r.Rows.Add(new object[]
                    {
                        l.LotCode,
                        Db.Products.Where(p => p.Id == l.ProductId).Select(p => p.ProductNameAr).FirstOrDefault(),
                        Db.Customers.Where(c => c.Id == l.CustomerId).Select(c => c.CustomerName).FirstOrDefault(),
                        l.InitialQtyKg, l.ProducedQtyKg, l.InStockQtyKg, l.DeliveredQtyKg
                    });
                    r.RowLinks.Add(new DocLinkDto { DocType = "receiving", Id = l.ShipmentId ?? 0 });
                }
                // §فحص التقارير الشامل: إجمالي المتبقي يُحسب من الصفوف المعروضة (بعد الفلاتر) لا من كل الدفعات
                r.Summary["إجمالي المتبقي (كجم)"] = r.Rows.Sum(x => Convert.ToDouble(x[5])).ToString("N1");
                break;
            }
            case "plans":
            {
                r.TitleAr = "تقرير خطط الإنتاج";
                r.Columns.AddRange(new[] { "الخطة", "العنوان", "من", "إلى", "عدد البنود", "إجمالي الكمية (كجم)", "الحالة" });
                var plansQ = Db.ProductionPlans.Include(p => p.Items).AsQueryable();
                if (from != null) plansQ = plansQ.Where(p => p.StartDate >= from);
                if (to != null) plansQ = plansQ.Where(p => p.StartDate < to.Value.AddDays(1));
                if (custId != null) plansQ = plansQ.Where(p => p.Items.Any(i => i.CustomerId == custId));
                if (prodId != null) plansQ = plansQ.Where(p => p.Items.Any(i => i.ProductId == prodId));
                foreach (var p in plansQ)
                {
                    r.Rows.Add(new object[] { p.DocumentNumber, p.PlanTitle, p.StartDate?.ToString("dd/MM/yyyy"), p.EndDate?.ToString("dd/MM/yyyy"),
                        p.Items.Count, p.Items.Sum(i => i.PlannedQtyKg), Core.Common.DocStatuses.ToArabic(p.Status) });
                    r.RowLinks.Add(new DocLinkDto { DocType = "planning", Id = p.Id });
                }
                break;
            }
            case "orders":
            {
                r.TitleAr = "تقرير أوامر الإنتاج";
                r.Columns.AddRange(new[] { "الأمر", "التاريخ", "عدد البنود", "المخطط (كجم)", "المنتَج (كجم)", "الحالة" });
                var ordersQ = Db.ProductionOrders.Include(o => o.Items).AsQueryable();
                if (from != null) ordersQ = ordersQ.Where(o => o.ProductionDate >= from);
                if (to != null) ordersQ = ordersQ.Where(o => o.ProductionDate < to.Value.AddDays(1));
                if (custId != null) ordersQ = ordersQ.Where(o => o.CustomerId == custId || o.Items.Any(i => i.CustomerId == custId));
                if (prodId != null) ordersQ = ordersQ.Where(o => o.Items.Any(i => i.ProductId == prodId));
                foreach (var o in ordersQ)
                {
                    r.Rows.Add(new object[] { o.DocumentNumber, o.ProductionDate?.ToString("dd/MM/yyyy"), o.Items.Count,
                        o.Items.Sum(i => i.PlannedQtyKg), o.Items.Sum(i => i.ProducedQtyKg), Core.Common.DocStatuses.ToArabic(o.Status) });
                    r.RowLinks.Add(new DocLinkDto { DocType = "orders", Id = o.Id });
                }
                break;
            }
            case "material_consumption":
            {
                r.TitleAr = "تقرير استهلاك المواد المساعدة";
                r.Columns.AddRange(new[] { "الأمر", "المادة", "المحتسبة", "المصروفة", "المستهلكة", "الهالك", "المتبقي غير المستخدم" });
                foreach (var m in Db.ProductionOrderMaterials)
                {
                    double unused = m.ActualIssuedQty - m.ConsumedQty - m.WastedQty - m.ReturnedQty;
                    r.Rows.Add(new object[]
                    {
                        Db.ProductionOrders.Where(o => o.Id == m.OrderId).Select(o => o.DocumentNumber).FirstOrDefault(),
                        Db.AuxiliaryMaterials.Where(x => x.Id == m.MaterialId).Select(x => x.MaterialNameAr).FirstOrDefault(),
                        m.CalculatedQty, m.ActualIssuedQty, m.ConsumedQty, m.WastedQty, Math.Round(unused, 2)
                    });
                }
                break;
            }
            case "production":
            {
                r.TitleAr = "تقرير الإنتاج المنفذ";
                r.Columns.AddRange(new[] { "الجلسة", "الأمر", "البداية", "النهاية", UnitsPolicy.FinishedQtyHeader, UnitsPolicy.FinishedWeightHeader, "الحالة" });
                var exeQ = Db.ProductionExecutions.AsQueryable();
                if (from != null) exeQ = exeQ.Where(e => e.StartDateTime >= from);
                if (to != null) exeQ = exeQ.Where(e => e.StartDateTime < to.Value.AddDays(1));
                if (custId != null) exeQ = exeQ.Where(e => Db.ProductionOrders.Any(o => o.Id == e.OrderId && o.CustomerId == custId));
                if (prodId != null) exeQ = exeQ.Where(e => Db.ProductionOrderItems.Any(i => i.OrderId == e.OrderId && i.ProductId == prodId));
                foreach (var e in exeQ)
                {
                    r.Rows.Add(new object[] { e.DocumentNumber,
                        Db.ProductionOrders.Where(o => o.Id == e.OrderId).Select(o => o.DocumentNumber).FirstOrDefault(),
                        e.StartDateTime?.ToString("dd/MM/yyyy HH:mm"), e.EndDateTime?.ToString("dd/MM/yyyy HH:mm"),
                        e.ActualCartons, e.ActualQtyKg, Core.Common.DocStatuses.ToArabic(e.Status) });
                    r.RowLinks.Add(new DocLinkDto { DocType = "orders", Id = e.OrderId });
                }
                break;
            }
            case "quality":
            {
                r.TitleAr = "تقرير فحوصات الجودة";
                r.Columns.AddRange(new[] { "الفحص", "الأمر", "التاريخ", "المفحوص (كرتون)", "المقبول (كرتون)", "المرفوض (كرتون)", "المفحوص (كجم)", "المقبول (كجم)", "المرفوض (كجم)", "الحالة" });
                var qcQ = Db.QualityChecks.AsQueryable();
                if (from != null) qcQ = qcQ.Where(c => c.CheckDate >= from);
                if (to != null) qcQ = qcQ.Where(c => c.CheckDate < to.Value.AddDays(1));
                if (custId != null) qcQ = qcQ.Where(c => Db.ProductionOrders.Any(o => o.Id == c.OrderId && o.CustomerId == custId));
                if (prodId != null) qcQ = qcQ.Where(c => c.Items.Any(i => i.ProductId == prodId));
                foreach (var c in qcQ)
                {
                    r.Rows.Add(new object[] { c.DocumentNumber,
                        Db.ProductionOrders.Where(o => o.Id == c.OrderId).Select(o => o.DocumentNumber).FirstOrDefault(),
                        c.CheckDate?.ToString("dd/MM/yyyy"), c.TotalCheckedCartons, c.AcceptedCartons, c.TotalCheckedCartons - c.AcceptedCartons,
                        c.TotalCheckedKg, c.AcceptedKg, c.RejectedKg, Core.Common.DocStatuses.ToArabic(c.Status) });
                    r.RowLinks.Add(new DocLinkDto { DocType = "quality", Id = c.Id });
                }
                break;
            }
            case "wastage":
            {
                r.TitleAr = "تقرير الهالك والأصناف الثانوية (بالكيلو)";
                r.Columns.AddRange(new[] { "الفحص", "الصنف الثانوي", "الكمية (كجم)" });
                foreach (var b in Db.QualityByProductRecords)
                {
                    r.Rows.Add(new object[]
                    {
                        Db.QualityChecks.Where(c => c.Id == b.CheckId).Select(c => c.DocumentNumber).FirstOrDefault(),
                        Db.ByProducts.Where(x => x.Id == b.ByProductId).Select(x => x.ByProductNameAr).FirstOrDefault(),
                        b.QtyKg
                    });
                }
                break;
            }
            case "finished_goods":
            {
                r.TitleAr = "تقرير أرصدة الإنتاج التام حسب العميل";
                r.Columns.AddRange(new[] { "العميل", "الصنف", "الدفعة", UnitsPolicy.BalanceCtnHeader, UnitsPolicy.BalanceKgHeader });
                var whFg = Db.Warehouses.FirstOrDefault(w => w.WarehouseCode == "WFG")?.Id ?? 0;
                var fgQ = Db.StockBalances.Where(b => b.WarehouseId == whFg && (b.QtyKg != 0 || b.PackageCount != 0));
                if (custId != null) fgQ = fgQ.Where(b => b.CustomerId == custId);
                if (prodId != null) fgQ = fgQ.Where(b => b.ProductId == prodId);
                foreach (var b in fgQ)
                {
                    r.Rows.Add(new object[]
                    {
                        Db.Customers.Where(c => c.Id == b.CustomerId).Select(c => c.CustomerName).FirstOrDefault(),
                        Db.Products.Where(p => p.Id == b.ProductId).Select(p => p.ProductNameAr).FirstOrDefault(),
                        Db.Lots.Where(l => l.Id == b.LotId).Select(l => l.LotCode).FirstOrDefault(),
                        (double)b.PackageCount, b.QtyKg
                    });
                }
                break;
            }
            case "delivery":
            {
                r.TitleAr = "تقرير تسليم العملاء";
                r.Columns.AddRange(new[] { "السند", "العميل", "التاريخ", UnitsPolicy.FinishedQtyHeader, UnitsPolicy.FinishedWeightHeader, "الحالة" });
                var dlvQ = Db.CustomerDeliveries.AsQueryable();
                if (from != null) dlvQ = dlvQ.Where(d => d.DeliveryDate >= from);
                if (to != null) dlvQ = dlvQ.Where(d => d.DeliveryDate < to.Value.AddDays(1));
                if (custId != null) dlvQ = dlvQ.Where(d => d.CustomerId == custId);
                if (prodId != null) dlvQ = dlvQ.Where(d => d.Items.Any(i => i.ProductId == prodId));
                foreach (var d in dlvQ)
                {
                    r.Rows.Add(new object[] { d.DocumentNumber,
                        Db.Customers.Where(c => c.Id == d.CustomerId).Select(c => c.CustomerName).FirstOrDefault(),
                        d.DeliveryDate?.ToString("dd/MM/yyyy"), (double)d.TotalCartons, d.TotalQtyKg, Core.Common.DocStatuses.ToArabic(d.Status) });
                    r.RowLinks.Add(new DocLinkDto { DocType = "delivery", Id = d.Id });
                }
                break;
            }
            case "delivery_analysis":
            {
                // §أمر شاشة التسليم — تقرير تحليلي بكل الكميات المسلمة: صف لكل بند (صنف×دفعة)
                // بسلسلة تتبعه الكاملة (خطة/أمر/صفة/دفعة/عبوة) ومجاميع لكل عميل وإجمالي عام.
                r.TitleAr = "تقرير تسليم العملاء التحليلي — بند/دفعة/خطة/أمر مع المجاميع";
                r.Columns.AddRange(new[] { "العميل", "السند", "التاريخ", "الصنف", "الكود", "الصفة",
                    "الدفعة", "العبوة", "رقم الخطة", "رقم الأمر",
                    UnitsPolicy.FinishedQtyHeader, UnitsPolicy.FinishedWeightHeader, "الحالة" });
                var dq = Db.CustomerDeliveries.AsQueryable();
                if (from != null) dq = dq.Where(d => d.DeliveryDate >= from);
                if (to != null) dq = dq.Where(d => d.DeliveryDate < to.Value.AddDays(1));
                if (custId != null) dq = dq.Where(d => d.CustomerId == custId);
                if (prodId != null) dq = dq.Where(d => d.Items.Any(i => i.ProductId == prodId));
                var docs = dq.OrderBy(d => d.CustomerId).ThenBy(d => d.DeliveryDate).ThenBy(d => d.Id)
                    .Select(d => new { d.Id, d.DocumentNumber, d.CustomerId, d.DeliveryDate, d.Status, d.IsApproved,
                        Items = d.Items.Select(i => new { i.ProductId, i.LotId, i.PackagingTypeId, i.PackageCount, i.QtyKg }).ToList() })
                    .ToList();

                var lotIds = docs.SelectMany(d => d.Items).Where(i => i.LotId != null).Select(i => i.LotId!.Value).Distinct().ToList();
                var orderRefs = (from oi in Db.ProductionOrderItems.AsNoTracking()
                                 join o in Db.ProductionOrders.AsNoTracking() on oi.OrderId equals o.Id
                                 where oi.LotId != null && lotIds.Contains(oi.LotId.Value)
                                 select new { oi.LotId, o.Id, o.DocumentNumber, o.SourcePlanId }).ToList();
                var orderIds = orderRefs.Select(x => x.Id).Distinct().ToList();
                var planIds = orderRefs.Where(x => x.SourcePlanId != null).Select(x => x.SourcePlanId!.Value).Distinct().ToList();
                var plans = Db.ProductionPlans.AsNoTracking().Where(x => planIds.Contains(x.Id)).ToDictionary(x => x.Id, x => x.DocumentNumber);
                // §1.50.72 P2-2: نفس إصلاح DeliveryPickService — التطابق الصارم على "نهائي"
                // لم ينطبق على القيم الفعلية ("نهائي — بعد التبريد …") فعمود الصفة كان فارغاً دائماً.
                var decisions = Db.QualityChecks.AsNoTracking()
                    .Where(c => c.OrderId != null && orderIds.Contains(c.OrderId.Value)
                                && c.IsApproved
                                && (c.CheckType == null || c.CheckType.Contains("نهائي")))
                    .GroupBy(c => c.OrderId).Select(g => new { g.Key, Dec = g.Max(c => c.Decision) })
                    .ToList().ToDictionary(x => x.Key!.Value, x => x.Dec);

                int? curCust = null; double custCtn = 0, custKg = 0; int custDocs = 0;
                double totCtn = 0, totKg = 0; int approvedDocs = 0, draftDocs = 0;
                var custNames = new Dictionary<int, string>();
                void EmitCustTotal()
                {
                    if (curCust == null) return;
                    r.Rows.Add(new object[] { $"إجمالي تسليمات العميل «{custNames[curCust.Value]}»", $"{custDocs} سند", "", "", "", "", "", "", "", "", custCtn, custKg, "" });
                }
                foreach (var d in docs)
                {
                    if (d.CustomerId != curCust) { EmitCustTotal(); curCust = d.CustomerId; custCtn = 0; custKg = 0; custDocs = 0; }
                    string custName = Db.Customers.Where(c => c.Id == d.CustomerId).Select(c => c.CustomerName).FirstOrDefault() ?? "-";
                    custNames[d.CustomerId] = custName;
                    custDocs++;
                    if (d.IsApproved) approvedDocs++;
                    else if (d.Status == Core.Common.DocStatuses.Draft) draftDocs++;
                    // Completed/posted customer deliveries remain approved documents.
                    foreach (var i in d.Items)
                    {
                        var prod = Db.Products.AsNoTracking().FirstOrDefault(x => x.Id == i.ProductId);
                        var refLot = orderRefs.FirstOrDefault(x => x.LotId == i.LotId);
                        string grade = refLot != null && decisions.TryGetValue(refLot.Id, out var dec)
                            ? (dec == "Passed" ? "سليم (مطابق)" : dec == "Quarantine" ? "محجوز" : "مرفوض") : "—";
                        r.Rows.Add(new object[] { custName, d.DocumentNumber, d.DeliveryDate?.ToString("dd/MM/yyyy"),
                            prod?.ProductNameAr ?? "-", prod?.ProductCode ?? "-", grade,
                            i.LotId != null ? Db.Lots.Where(l => l.Id == i.LotId).Select(l => l.LotCode).FirstOrDefault() ?? "—" : "—",
                            i.PackagingTypeId != null ? Db.PackagingTypes.Where(k => k.Id == i.PackagingTypeId).Select(k => k.PackageNameAr).FirstOrDefault() ?? "—" : "كرتون الصنف",
                            refLot?.SourcePlanId != null && plans.TryGetValue(refLot.SourcePlanId.Value, out var planNo) ? planNo : "—",
                            refLot?.DocumentNumber ?? "—",
                            (double)i.PackageCount, i.QtyKg, Core.Common.DocStatuses.ToArabic(d.Status) });
                        r.RowLinks.Add(new DocLinkDto { DocType = "delivery", Id = d.Id });
                        custCtn += i.PackageCount; custKg += i.QtyKg; totCtn += i.PackageCount; totKg += i.QtyKg;
                    }
                }
                EmitCustTotal();
                r.Rows.Add(new object[] { "الإجمالي العام (كل العملاء)", $"{docs.Count} سند", "", "", "", "", "", "", "", "", totCtn, totKg, "" });
                r.Summary["عدد السندات"] = docs.Count.ToString();
                r.Summary["معتمد / مسودة"] = $"{approvedDocs} / {draftDocs}";
                r.Summary["عدد العملاء"] = custNames.Count.ToString();
                r.Summary[UnitsPolicy.FinishedQtyHeader] = totCtn.ToString("N0");
                r.Summary[UnitsPolicy.FinishedWeightHeader] = totKg.ToString("N1");
                break;
            }
            case "movements":
            {
                r.TitleAr = "تقرير حركة المخزون (تتبع كامل)";
                r.Columns.AddRange(new[] { "الحركة", "التاريخ", "المخزن", "الصنف", "الدفعة", "النوع", UnitsPolicy.RawQtyHeader, UnitsPolicy.PackagesHeader, "المستند", "المستخدم", "الجهاز" });
                var q = Db.InventoryTransactions.AsQueryable();
                if (from != null) q = q.Where(x => x.TxnDate >= from);
                if (to != null) q = q.Where(x => x.TxnDate <= to.Value.AddDays(1));
                // §فحص التقارير الشامل: فلترة بالعميل/الصنف كبقية التقارير (كانت الفترة وحدها)
                if (custId != null) q = q.Where(x => x.CustomerId == custId);
                if (prodId != null) q = q.Where(x => x.ProductId == prodId);
                // §فحص التقارير الشامل: الحركات تولد معتمدة فقط (لا حركة بدون مستند معتمد §9) —
                // يُبقى الشرط صريحاً حمايةً من أي مسار مستقبلي يكتب حركة غير معتمدة.
                q = q.Where(x => x.IsApproved);
                int total = q.Count();
                foreach (var x in q.OrderByDescending(x => x.TxnDate).Take(3000))
                {
                    r.Rows.Add(new object[] { x.TxnNumber, x.TxnDate.ToString("dd/MM/yyyy HH:mm"),
                        Db.Warehouses.Where(w => w.Id == x.WarehouseId).Select(w => w.WarehouseNameAr).FirstOrDefault(),
                        x.ProductId != null ? Db.Products.Where(p => p.Id == x.ProductId).Select(p => p.ProductNameAr).FirstOrDefault()
                                            : Db.AuxiliaryMaterials.Where(m => m.Id == x.MaterialId).Select(m => m.MaterialNameAr).FirstOrDefault(),
                        Db.Lots.Where(l => l.Id == x.LotId).Select(l => l.LotCode).FirstOrDefault(),
                        // §فحص التقارير الشامل: كان التحويل والتسوية يظهران «صادر» — تسمية الأنواع الأربعة صحيحة
                        x.MovementType == Core.Domain.Enums.MovementType.Inbound ? "وارد"
                        : x.MovementType == Core.Domain.Enums.MovementType.Outbound ? "صادر"
                        : x.MovementType == Core.Domain.Enums.MovementType.Transfer ? "تحويل بين مخازن" : "تسوية جرد",
                        x.QtyKg, (double)x.PackageCount, $"{x.ReferenceDocType}: {x.ReferenceDocNumber}",
                        Db.Users.Where(u => u.Id == x.CreatedBy).Select(u => u.FullName).FirstOrDefault(), x.MachineName });
                }
                // §فحص التقارير الشامل: قصّ أحدث 3000 كان صامتاً — تنبيه صريح حتى لا تُقرأ مجاميع ناقصة كنهائية
                if (total > 3000) r.Summary["تنبيه"] = $"معروض أحدث 3000 حركة من أصل {total} — حدّد فترة أو عميلاً أضيق للمطابقة الكاملة";
                r.Summary["إجمالي الكمية المعروضة (كجم)"] = r.Rows.Sum(x => Convert.ToDouble(x[6])).ToString("N1");
                r.Summary["إجمالي العبوات المعروضة"] = r.Rows.Sum(x => Convert.ToDouble(x[7])).ToString("N0");
                break;
            }
            case "audit":
            {
                r.TitleAr = "تقرير سجل التدقيق";
                r.Columns.AddRange(new[] { "التاريخ", "المستخدم", "الجهاز", "الإجراء", "الشاشة", "المستند", "السجل" });
                var q = Db.AuditLogs.AsQueryable();
                if (from != null) q = q.Where(a => a.ActionDate >= from);
                if (to != null) q = q.Where(a => a.ActionDate <= to.Value.AddDays(1));
                int auditTotal = q.Count();
                foreach (var a in q.OrderByDescending(a => a.ActionDate).Take(3000))
                    r.Rows.Add(new object[] { a.ActionDate.ToString("dd/MM/yyyy HH:mm:ss"), a.UserName, a.MachineName, a.ActionType, a.ScreenName, a.DocumentNumber, a.RecordId });
                if (auditTotal > 3000) r.Summary["تنبيه"] = $"معروض أحدث 3000 سجل من أصل {auditTotal} — حدّد فترة أضيق";
                break;
            }
            case "management":
            {
                r.TitleAr = "مؤشرات الإدارة العامة";
                r.Columns.AddRange(new[] { "المؤشر", "القيمة" });
                r.Rows.Add(new object[] { "إجمالي المستلم من التمور (كجم)", Db.Shipments.Where(s => s.IsApproved).Sum(s => s.TotalWeightKg) });
                r.Rows.Add(new object[] { "رصيد الخام المتبقي (كجم)", Db.Lots.Sum(l => l.InStockQtyKg) });
                r.Rows.Add(new object[] { "إجمالي الإنتاج المنفذ (كجم)", Db.ProductionExecutions.Where(e => e.Status == "Completed").Sum(e => e.ActualQtyKg) });
                r.Rows.Add(new object[] { "إجمالي المسلَّم للعملاء (كجم)", Db.CustomerDeliveries.Where(d => d.IsApproved).Sum(d => d.TotalQtyKg) });
                r.Rows.Add(new object[] { "أوامر إنتاج مفتوحة", Db.ProductionOrders.Count(o => !o.IsClosed && o.IsApproved) });
                r.Rows.Add(new object[] { "خطط نشطة", Db.ProductionPlans.Count(p => p.IsApproved && !p.IsClosed) });
                r.Rows.Add(new object[] { "أجهزة متصلة", Db.ClientMachines.Count(m => m.IsActive) });
                break;
            }
            case "item_journey":
            {
                // §تتبع الصنف: الرحلة الكاملة لكل صنف — استلام ← خطة ← أمر ← إنتاج ← فحص ← مخزون ← تسليم ← فاتورة
                r.TitleAr = "تقرير تتبع الصنف — الرحلة الكاملة من الاستلام حتى الفاتورة";
                var svc = new TraceabilityService(Db, Session, Numbering);
                var journeys = svc.GetJourneys(custId, prodId);
                r.Columns.AddRange(new[] { "الصنف", "النوع", "العميل", "المرحلة", "المستند", "التاريخ", "الدفعة", "الكمية (كجم)", "الكراتين", "الحالة", "التفاصيل" });
                foreach (var j in journeys)
                {
                    r.Rows.Add(new object[]
                    {
                        j.ProductName, j.ItemTypeAr, j.CustomerName, "═══ ملخص الرحلة ═══", "-", "-", "-",
                        j.ReceivedKg, 0,
                        $"استُلم {j.ReceivedKg:N1} | خُطط {j.PlannedKg:N1} | أُنتج {j.ProducedKg:N1} | قُبل {j.AcceptedKg:N1}",
                        $"مخزون {j.InStockKg:N1} | سُلّم {j.DeliveredKg:N1} | فُوتر {j.InvoicedKg:N1} | متبقي {j.RemainingKg:N1}"
                    });
                    foreach (var s in j.Stages)
                        r.Rows.Add(new object[] { j.ProductName, j.ItemTypeAr, s.CustomerName, s.StageAr, s.DocNumber, s.Date ?? "-", s.LotCode ?? "-", s.QtyKg, s.Cartons, s.StatusAr ?? "-", s.Detail ?? "-" });
                }
                break;
            }
            case "aux_consumption_by_order":
            {
                r.TitleAr = "استهلاك الأصناف المساعدة حسب أمر الإنتاج";
                r.Columns.AddRange(new[] { "أمر الإنتاج", "الصنف التام", "إنتاج (كرتون)", "الصنف المساعد", "الوحدة", "المطلوب", "المصروف", "المتبقي", "التفاصيل" });
                int? orderId = null;
                if (parameters.TryGetValue("order", out var orderStr) && int.TryParse(orderStr, out var oid)) orderId = oid;
                else if (parameters.TryGetValue("order", out var orderNum))
                {
                    orderId = Db.ProductionOrders.AsNoTracking().Where(o => o.DocumentNumber == orderNum).Select(o => o.Id).FirstOrDefault();
                    if (orderId == 0) orderId = null;
                }
                var auxSvc = new AuxiliaryManagementService(Db, Session, Numbering);
                var ordersQ = Db.ProductionOrders.AsQueryable();
                if (orderId != null) ordersQ = ordersQ.Where(o => o.Id == orderId);
                foreach (var o in ordersQ.OrderByDescending(o => o.Id).Take(100))
                {
                    var needs = auxSvc.CalculateNeedsForOrder(o.Id);
                    var finishedName = Db.ProductionOrderItems.Where(i => i.OrderId == o.Id).Select(i => Db.Products.Where(p => p.Id == i.ProductId).Select(p => p.ProductNameAr).FirstOrDefault()).FirstOrDefault() ?? "-";
                    int totalCartons = Db.ProductionOrderItems.Where(i => i.OrderId == o.Id).Sum(i => i.PlannedCartons);
                    foreach (var n in needs)
                    {
                        r.Rows.Add(new object[] { o.DocumentNumber, finishedName, totalCartons, n.AuxiliaryProductName, n.Unit, n.RequiredQty, n.IssuedQty, n.RemainingQty, n.CalculationDetails });
                        r.RowLinks.Add(new DocLinkDto { DocType = "orders", Id = o.Id });
                    }
                }
                break;
            }
            case "aux_period_consumption":
            {
                r.TitleAr = "استهلاك صنف مساعد خلال فترة";
                r.Columns.AddRange(new[] { "الصنف المساعد", "الوحدة", "إجمالي المصروف", "عدد الحركات", "الفترة" });
                var auxSvc2 = new AuxiliaryManagementService(Db, Session, Numbering);
                var fromD = from ?? DateTime.Today.AddMonths(-1);
                var toD = to ?? DateTime.Today;
                var periodRows = auxSvc2.GetPeriodConsumption(fromD, toD);
                foreach (var pr in periodRows)
                {
                    r.Rows.Add(new object[] { pr.AuxiliaryProductName, pr.Unit, pr.TotalIssued, pr.TransactionsCount, $"{fromD:dd/MM/yyyy} - {toD:dd/MM/yyyy}" });
                }
                r.Summary["إجمالي الأصناف المستهلكة"] = periodRows.Count.ToString();
                r.Summary["إجمالي الكمية المصروفة"] = periodRows.Sum(x => x.TotalIssued).ToString("N3");
                break;
            }
            case "aux_setup":
            {
                r.TitleAr = "تهيئة الأصناف المساعدة";
                r.Columns.AddRange(new[] { "الكود", "الصنف المساعد", "المجموعة", "الوحدة", "وزن الوحدة", "طريقة الصرف", "يحتاج صرف؟", "الحالة", "مُهيأ؟" });
                var auxSvc3 = new AuxiliaryManagementService(Db, Session, Numbering);
                var setups = auxSvc3.GetAuxiliarySetups();
                foreach (var s2 in setups)
                {
                    r.Rows.Add(new object[] { s2.ProductCode, s2.ProductName, s2.GroupCode, s2.BaseUnit, s2.UnitWeightKg, s2.DispensingMethodAr, s2.NeedsIssueOnOrder ? "نعم" : "لا", s2.IsActive ? "فعال" : "موقوف", s2.IsConfigured ? "نعم" : "لا" });
                }
                break;
            }
            case "aux_bom":
            {
                r.TitleAr = "مكونات الإنتاج (BOM) — احتياجات الصنف التام";
                r.Columns.AddRange(new[] { "الصنف التام", "الصنف المساعد", "الوحدة", "الكمية لكل كرتون", "طريقة الحساب", "الحالة" });
                var auxSvc4 = new AuxiliaryManagementService(Db, Session, Numbering);
                var finished = auxSvc4.GetFinishedProducts();
                foreach (var fp in finished)
                {
                    var reqs = auxSvc4.GetRequirementsForFinished(fp.Id);
                    foreach (var rq in reqs)
                    {
                        r.Rows.Add(new object[] { fp.ProductNameAr, rq.AuxiliaryProductName, rq.Unit, rq.QtyPerCarton, rq.CalculationMethodAr, rq.IsActive ? "فعال" : "موقوف" });
                    }
                }
                break;
            }
            default:
            {
                // §مرحلة التقارير: التقارير الجديدة (العمليات + الشاملة + الاحترافية)
                var nr = RunNewReports(reportCode, parameters, from, to, custId, prodId)
                    ?? RunProfessional(reportCode, parameters, from, to, custId, prodId)
                    ?? RunTreatmentReports(reportCode, parameters, from, to, custId, prodId);
                if (nr != null)
                {
                    if (string.IsNullOrWhiteSpace(nr.PeriodLabel)) nr.PeriodLabel = PeriodText(from, to);
                    nr.RowLinks ??= new List<DocLinkDto>();
                }
                return nr;
            }
        }
        // §إصلاح: الفترة تُعرض دائماً في الترويسة — حتى بلا فلتر، ليعرف القارئ مدى تغطية التقرير
        if (string.IsNullOrWhiteSpace(r.PeriodLabel)) r.PeriodLabel = PeriodText(from, to);
        // §زر «+»: القائمة لا تكون null أبداً حتى لا ينكسر العرض في تقرير بلا مستند مصدر
        r.RowLinks ??= new List<DocLinkDto>();
        // §إصلاح: كل تقرير يحمل إجمالي عدد الصفوف على الأقل
        if (r.Summary.Count == 0) r.Summary["عدد الصفوف"] = r.Rows.Count.ToString("N0");
        return r;
    }
}

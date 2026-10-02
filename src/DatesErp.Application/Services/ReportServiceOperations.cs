using DatesErp.Core.Common;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Interfaces.Services;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §مرحلة التقارير — محرك التقارير الجديد (مبني من الصفر على نموذج النظام السليم،
/// لا على تقارير النظام المرجعي): كل سطر يحتفظ بمرجعه الكامل (عميل/شحنة/دفعة/صنف/عبوة)،
/// فلاتر بالعميل والصنف والفترة والحالة، وإجماليات وبطاقات مؤشرات.
/// تقارير العمليات: حركات التوريد، حركات الخطط، سجل العمليات الموحد.
/// التقارير الشاملة: كشف الصنف، كشف العميل، الإنتاج اليومي، الفحص التفصيلي.
/// </summary>
public partial class ReportService
{
    // ═══════════════════════════ تعريفات التقارير الجديدة ═══════════════════════════

    private List<ReportDefinition> GetNewReportDefinitions()
    {
        var customers = Db.Customers.AsNoTracking().OrderBy(c => c.CustomerName)
            .Select(c => new { c.Id, c.CustomerName }).ToList().Select(x => (x.Id.ToString(), x.CustomerName)).ToList();
        var products = Db.Products.AsNoTracking().OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.ProductNameAr }).ToList().Select(x => (x.Id.ToString(), x.ProductNameAr)).ToList();
        var warehouses = Db.Warehouses.AsNoTracking().OrderBy(w => w.Id)
            .Select(w => new { w.Id, w.WarehouseNameAr }).ToList().Select(x => (x.Id.ToString(), x.WarehouseNameAr)).ToList();
        var shipments = Db.Shipments.AsNoTracking().OrderByDescending(s => s.Id).Take(100)
            .Select(s => new { s.Id, s.DocumentNumber, s.CustomerId, s.TotalWeightKg }).ToList()
            .Select(x => (x.Id.ToString(),
                $"{x.DocumentNumber} — {Db.Customers.AsNoTracking().Where(c => c.Id == x.CustomerId).Select(c => c.CustomerName).FirstOrDefault() ?? "-"} ({x.TotalWeightKg:N0} كجم)")).ToList();

        ReportParameter PDate(string key, string label) => new() { Key = key, LabelAr = label, Kind = "date" };
        ReportParameter PCust() => new() { Key = "customer", LabelAr = "العميل", Kind = "list", Options = customers };
        ReportParameter PProd() => new() { Key = "product", LabelAr = "الصنف", Kind = "list", Options = products };

        return new List<ReportDefinition>
        {
            new()
            {
                Code = "receiving_detail",
                TitleAr = "حركات التوريد التفصيلية (شحنة × صنف × دفعة)",
                Category = "العمليات",
                Parameters = new() { PDate("from", "من تاريخ"), PDate("to", "إلى تاريخ"), PCust(), PProd() }
            },
            new()
            {
                Code = "plans_activity",
                TitleAr = "حركات الخطط (المخطط/المنتج/المقبول/المسلَّم لكل بند)",
                Category = "العمليات",
                Parameters = new() { PDate("from", "من تاريخ"), PDate("to", "إلى تاريخ"), PCust(), PProd() }
            },
            new()
            {
                Code = "operations",
                TitleAr = "سجل العمليات الموحد (كل المستندات في دفتر واحد)",
                Category = "العمليات",
                Parameters = new()
                {
                    PDate("from", "من تاريخ"), PDate("to", "إلى تاريخ"), PCust(),
                    new() { Key = "optype", LabelAr = "نوع العملية", Kind = "list", Options = new()
                    {
                        ("receiving", "استلام"), ("plan", "خطة إنتاج"), ("order", "أمر إنتاج"),
                        ("production", "إنتاج فعلي"), ("quality", "فحص جودة"),
                        ("finished", "استلام تام"), ("delivery", "تسليم عميل")
                    } }
                }
            },
            new()
            {
                Code = "item_statement",
                TitleAr = "كشف الصنف الشامل (الرحلة الكاملة من الاستلام حتى المتبقي)",
                Category = "الشاملة",
                Parameters = new() { PCust(), PProd() }
            },
            new()
            {
                Code = "customer_statement",
                TitleAr = "كشف العميل الشامل (استلام/تخطيط/إنتاج/مخزون/تسليم/فوترة)",
                Category = "الشاملة",
                Parameters = new() { PCust() }
            },
            new()
            {
                Code = "daily_production",
                TitleAr = "تقرير الإنتاج اليومي (المنتَج والمخرجات الثانوية والتوقفات)",
                Category = "الشاملة",
                Parameters = new() { PDate("from", "من تاريخ"), PDate("to", "إلى تاريخ"), PCust() }
            },
            new()
            {
                Code = "production_balance",
                TitleAr = "تقرير توازن الإنتاج (المدخلات مقابل المخرجات وفرق الوزن)",
                Category = "الشاملة",
                Parameters = new() { PDate("from", "من تاريخ"), PDate("to", "إلى تاريخ"), PCust() }
            },
            new()
            {
                Code = "quality_detail",
                TitleAr = "تقرير الفحص التفصيلي (القرار والمعايير المخبرية)",
                Category = "الشاملة",
                Parameters = new()
                {
                    PDate("from", "من تاريخ"), PDate("to", "إلى تاريخ"), PCust(),
                    new() { Key = "decision", LabelAr = "قرار الجودة", Kind = "list", Options = new()
                    {
                        ("Passed", "مطابق ومقبول"), ("Quarantine", "حجز وتحريز"), ("Rejected", "مرفوض/عوادم")
                    } }
                }
            },
            // ═══ §تقارير المخزون: حركة المخازن، حركة الصنف برصيد جارٍ، المخزن الشامل ═══
            new()
            {
                Code = "carton_statement",
                TitleAr = "كشف الكرتون الفارغ (متولّد/مبيع/فروقات عدّ/رصيد)",
                Category = "المخزون",
                Parameters = new() { PDate("from", "من تاريخ"), PDate("to", "إلى تاريخ") }
            },
            new()
            {
                Code = "warehouse_movements",
                TitleAr = "حركة المخازن (وارد/منصرف بكل مستند — تنقل + لكل حركة)",
                Category = "المخزون",
                Parameters = new()
                {
                    PDate("from", "من تاريخ"), PDate("to", "إلى تاريخ"),
                    new() { Key = "warehouse", LabelAr = "المخزن", Kind = "list", Options = warehouses },
                    new() { Key = "mtype", LabelAr = "نوع الحركة", Kind = "list", Options = new() { ("in", "وارد"), ("out", "منصرف") } },
                    PCust(), PProd()
                }
            },
            new()
            {
                Code = "item_movements",
                TitleAr = "حركة الصنف (وارد/منصرف/رصيد جارٍ على مستوى المخزن)",
                Category = "المخزون",
                Parameters = new()
                {
                    PProd(),
                    new() { Key = "warehouse", LabelAr = "المخزن (اختياري)", Kind = "list", Options = warehouses },
                    PDate("from", "من تاريخ"), PDate("to", "إلى تاريخ"), PCust()
                }
            },
            // §إصلاح: كان التقريران منفَّذين بالكامل (shipment_full: 204 سطر + 187 سطر اختبارات،
            // و shipment_tracking) لكن بلا ReportDefinition — فلا يظهران في القائمة ولا يمكن فتحهما
            // إطلاقاً. الاختبارات مرّت لأنها تستدعي Run() مباشرةً متجاوزةً القائمة.
            new()
            {
                Code = "shipment_full",
                TitleAr = "تقرير الشحنة الشامل — كشف حساب تمور العميل (الدخول، الأوامر، المخرجات، المتبقي)",
                Category = "التتبع",
                Parameters = new()
                {
                    new() { Key = "shipment", LabelAr = "الشحنة (اختياري)", Kind = "list", Options = shipments },
                    new() { Key = "lot", LabelAr = "رقم الدفعة", Kind = "text" },
                    new() { Key = "order", LabelAr = "رقم أمر الإنتاج", Kind = "text" },
                    PDate("from", "من تاريخ"), PDate("to", "إلى تاريخ"), PCust(), PProd()
                }
            },
            new()
            {
                Code = "shipment_tracking",
                TitleAr = "تتبع الشحنة — دفتر الحركة والرصيد الجاري",
                Category = "التتبع",
                Parameters = new()
                {
                    // §مطلوب: الدالة تُرجع تنبيهاً إن لم تُختر شحنة.
                    new() { Key = "shipment", LabelAr = "الشحنة", Kind = "list", Options = shipments },
                    PDate("from", "من تاريخ"), PDate("to", "إلى تاريخ")
                }
            },
            new()
            {
                Code = "warehouse_full",
                TitleAr = "تقرير المخزن الشامل (الأرصدة الحالية لكل صنف/دفعة/عميل)",
                Category = "المخزون",
                Parameters = new()
                {
                    new() { Key = "warehouse", LabelAr = "المخزن (اختياري)", Kind = "list", Options = warehouses },
                    PCust(), PProd()
                }
            }
        };
    }

    // ═══════════════════════════ تشغيل التقارير الجديدة ═══════════════════════════

    private ReportResult RunNewReports(string code, Dictionary<string, string> p,
        DateTime? from, DateTime? to, int? custId, int? prodId)
    {
        string CustName(int? id) => id == null ? "—" :
            Db.Customers.AsNoTracking().Where(c => c.Id == id).Select(c => c.CustomerName).FirstOrDefault() ?? "—";
        string ProdName(int id) =>
            Db.Products.AsNoTracking().Where(x => x.Id == id).Select(x => x.ProductNameAr).FirstOrDefault() ?? "—";

        // §توافق مع البيانات السابقة للديناميكية: الإقفالات القديمة خزّنت «حشف» و«نوى» في عمودين
        // ثابتين. توزَّع على المخرجات المعرَّفة مطابقةً بالاسم — وما لا يطابق يُترك صفراً بدل اختراع اسم.
        Dictionary<int, double> ByProductLegacySplit(double hashfKg, double nawaKg)
        {
            var map = new Dictionary<int, double>();
            foreach (var b in Db.ByProducts.AsNoTracking().Where(x => x.IsActive).ToList())
            {
                map[b.Id] = 0;
                string n = b.ByProductNameAr ?? "";
                if (n.Contains("حشف")) map[b.Id] += hashfKg;
                else if (n.Contains("نوى")) map[b.Id] += nawaKg;
            }
            return map;
        }
        string ExecStatusAr(string s) => s switch
        {
            "Completed" => "مكتمل ✅",
            "Partial" => "جزئي 🟠",
            "InProgress" => "قيد التنفيذ 🏭",
            _ => "لم يبدأ ⏳"
        };
        string DecisionAr(string d) => d switch
        {
            "Quarantine" => "🟡 حجز وتحريز",
            "Rejected" => "🔴 مرفوض/عوادم",
            _ => "🟢 مطابق ومقبول"
        };

        switch (code)
        {
            // ═══ 1) حركات التوريد التفصيلية ═══
            case "receiving_detail":
            {
                var r = new ReportResult
                {
                    TitleAr = "حركات التوريد التفصيلية — كل بند استلام بوحدته الأصلية وكميته القياسية (كجم)",
                    Columns = new List<string> { "سند الاستلام", "التاريخ", "العميل", "الحاوية", "الصنف", "وحدة الاستلام", "العدد", "وزن الوحدة (كجم)", "الكمية (كجم)", "الدفعة", "الحالة" },
                    RowLinks = new List<DocLinkDto>()
                };
                var q = Db.ShipmentItems.AsNoTracking()
                    .Join(Db.Shipments.AsNoTracking(), i => i.ShipmentId, s => s.Id, (i, s) => new { i, s });
                if (from != null) q = q.Where(x => x.s.ReceivedDate >= from);
                if (to != null) q = q.Where(x => x.s.ReceivedDate <= to.Value.AddDays(1));
                if (custId != null) q = q.Where(x => x.s.CustomerId == custId);
                if (prodId != null) q = q.Where(x => x.i.ProductId == prodId);

                double totKg = 0; var shipIds = new HashSet<int>();
                foreach (var x in q.OrderByDescending(z => z.s.Id))
                {
                    totKg += x.i.TotalWeightKg;
                    shipIds.Add(x.s.Id);
                    r.Rows.Add(new object[]
                    {
                        x.s.DocumentNumber, UiFormat.D(x.s.ReceivedDate), CustName(x.s.CustomerId),
                        x.s.ContainerNumber ?? "—", ProdName(x.i.ProductId),
                        x.i.ReceiptUnit ?? "كرتون", UiFormat.N0(x.i.PackageCount), UiFormat.N(x.i.UnitWeightKg),
                        UiFormat.N(x.i.TotalWeightKg),
                        Db.Lots.AsNoTracking().Where(l => l.ShipmentItemId == x.i.Id).Select(l => l.LotCode).FirstOrDefault() ?? "—",
                        x.s.IsApproved ? "معتمد" : "مسودة"
                    });
                    r.RowLinks.Add(new DocLinkDto { DocType = "receiving", Id = x.s.Id });
                }
                r.Summary["عدد سندات الاستلام"] = UiFormat.N0(shipIds.Count);
                r.Summary["عدد البنود"] = UiFormat.N0(r.Rows.Count);
                r.Summary["إجمالي الوزن المستلم (كجم)"] = UiFormat.N(totKg);
                return r;
            }

            // ═══ 2) حركات الخطط ═══
            case "plans_activity":
            {
                var r = new ReportResult
                {
                    TitleAr = "حركات الخطط — كل بند بمرجعه الكامل مع المخطط والمنتج والمقبول والمسلَّم",
                    Columns = new List<string> { "الخطة", "اليوم المجدول", "العميل", "الدفعة", "الشحنة", "الصنف", "العبوة",
                        "المخطط (كجم)", "المخطط (كرتون)", "المنتَج (كرتون · كجم)", "المقبول (كجم)", "المسلَّم (كجم)", "المتبقي (كجم)", "حالة التنفيذ" },
                    RowLinks = new List<DocLinkDto>()
                };
                var q = Db.ProductionPlanItems.AsNoTracking()
                    .Join(Db.ProductionPlans.AsNoTracking(), i => i.PlanId, pl => pl.Id, (i, pl) => new { i, pl });
                if (from != null) q = q.Where(x => x.i.ScheduledDate >= from);
                if (to != null) q = q.Where(x => x.i.ScheduledDate <= to.Value.AddDays(1));
                if (custId != null) q = q.Where(x => x.i.CustomerId == custId);
                if (prodId != null) q = q.Where(x => x.i.ProductId == prodId);

                double tPlanned = 0, tProduced = 0, tAccepted = 0, tDelivered = 0;
                foreach (var x in q.OrderBy(z => z.i.ScheduledDate).ThenBy(z => z.i.Id))
                {
                    double remaining = Math.Max(0, x.i.PlannedQtyKg - x.i.DeliveredQtyKg);
                    tPlanned += x.i.PlannedQtyKg; tProduced += x.i.ProducedQtyKg;
                    tAccepted += x.i.AcceptedQtyKg; tDelivered += x.i.DeliveredQtyKg;
                    // §قاعدة الوحدات: الكرتون وحدة التداول بعد الخطط — عدد كراتين المنتَج
                    // من أوامر الإنتاج الفعلية المرتبطة بهذا البند (لا رقم مفترض).
                    int producedCartons = Db.ProductionOrderItems.AsNoTracking()
                        .Where(oi => oi.PlanItemId == x.i.Id).Sum(oi => oi.ProducedCartons);
                    r.Rows.Add(new object[]
                    {
                        x.pl.DocumentNumber, UiFormat.D(x.i.ScheduledDate), CustName(x.i.CustomerId),
                        Db.Lots.AsNoTracking().Where(l => l.Id == x.i.LotId).Select(l => l.LotCode).FirstOrDefault() ?? "—",
                        Db.Shipments.AsNoTracking().Where(s => s.Id == x.i.ShipmentId).Select(s => s.DocumentNumber).FirstOrDefault() ?? "—",
                        ProdName(x.i.ProductId),
                        x.i.PackagingTypeId != null ? Db.PackagingTypes.AsNoTracking().Where(pk => pk.Id == x.i.PackagingTypeId).Select(pk => pk.PackageNameAr).FirstOrDefault() ?? "-" : "-",
                        UiFormat.N(x.i.PlannedQtyKg), UiFormat.N0(x.i.PlannedCartons),
                        producedCartons > 0 ? ReportUnits.CartonThenWeight(producedCartons, x.i.ProducedQtyKg) : UiFormat.N(x.i.ProducedQtyKg),
                        UiFormat.N(x.i.AcceptedQtyKg), UiFormat.N(x.i.DeliveredQtyKg),
                        UiFormat.N(remaining), ExecStatusAr(x.i.ExecutionStatus)
                    });
                    r.RowLinks.Add(new DocLinkDto { DocType = "planning", Id = x.pl.Id });
                }
                r.Summary["إجمالي المخطط (كجم)"] = UiFormat.N(tPlanned);
                r.Summary["إجمالي المنتَج (كجم)"] = UiFormat.N(tProduced);
                r.Summary["إجمالي المقبول (كجم)"] = UiFormat.N(tAccepted);
                r.Summary["إجمالي المسلَّم (كجم)"] = UiFormat.N(tDelivered);
                r.Summary["نسبة الإنجاز"] = tPlanned > 0 ? UiFormat.Pct(tDelivered / tPlanned * 100) : "—";
                // §الشكل الموحد: شريط معادلة الإقفال أسفل الجدول
                r.Equation = $"المخطط {UiFormat.N(tPlanned)} = المنتَج {UiFormat.N(tProduced)} + المتبقي {UiFormat.N(Math.Max(0, tPlanned - tDelivered))} كجم"
                    + $" · المسلَّم {UiFormat.N(tDelivered)} كجم" + (tPlanned > 0 ? $" · نسبة الإنجاز {UiFormat.Pct(tDelivered / tPlanned * 100)}" : "");
                return r;
            }

            // ═══ 3) سجل العمليات الموحد ═══
            case "operations":
            {
                string opFilter = p.GetValueOrDefault("optype") ?? "";
                var r = new ReportResult
                {
                    TitleAr = "سجل العمليات الموحد — دورة العمل الكاملة في دفتر واحد",
                    Columns = new List<string> { "العملية", "المستند", "التاريخ", "العميل", "الصنف/البيان", "الكمية (كجم)", "الكراتين", "الحالة" },
                    RowLinks = new List<DocLinkDto>()
                };
                var rows = new List<(DateTime dt, string op, string doc, string cust, string item, string qty, string cartons, string status, DocLinkDto link)>();

                if (opFilter is "" or "receiving")
                    foreach (var s in Db.Shipments.AsNoTracking().Where(s => from == null || s.ReceivedDate >= from)
                             .Where(s => to == null || s.ReceivedDate <= to.Value.AddDays(1))
                             .Where(s => custId == null || s.CustomerId == custId).OrderByDescending(x => x.Id))
                    {
                        var names = string.Join(" + ", Db.ShipmentItems.AsNoTracking()
                            .Where(i => i.ShipmentId == s.Id).Select(i => i.ProductId).Distinct().ToList()
                            .Select(pid => ProdName(pid)));
                        if (prodId != null && !Db.ShipmentItems.AsNoTracking().Any(i => i.ShipmentId == s.Id && i.ProductId == prodId)) continue;
                        rows.Add((s.ReceivedDate ?? s.CreatedDate, "📥 استلام", s.DocumentNumber, CustName(s.CustomerId), names, UiFormat.N(s.TotalWeightKg), UiFormat.N0(s.TotalCartons), s.IsApproved ? "معتمد" : "مسودة", new DocLinkDto { DocType = "receiving", Id = s.Id }));
                    }

                if (opFilter is "" or "plan")
                    foreach (var pl in Db.ProductionPlans.AsNoTracking()
                             .Where(pl => from == null || pl.StartDate >= from).Where(pl => to == null || pl.StartDate <= to.Value.AddDays(1))
                             .OrderByDescending(x => x.Id))
                    {
                        var items = Db.ProductionPlanItems.AsNoTracking().Where(i => i.PlanId == pl.Id).ToList();
                        if (custId != null && !items.Any(i => i.CustomerId == custId)) continue;
                        if (prodId != null && !items.Any(i => i.ProductId == prodId)) continue;
                        var names = string.Join(" + ", items.Select(i => i.ProductId).Distinct().ToList().Select(pid => ProdName(pid)));
                        rows.Add((pl.StartDate ?? pl.CreatedDate, "📋 خطة إنتاج", pl.DocumentNumber, pl.PlanTitle ?? "—", names, UiFormat.N(items.Sum(i => i.PlannedQtyKg)), UiFormat.N0(items.Sum(i => i.PlannedCartons)), DocStatuses.ToArabic(pl.Status), new DocLinkDto { DocType = "planning", Id = pl.Id }));
                    }

                if (opFilter is "" or "order")
                    foreach (var o in Db.ProductionOrders.AsNoTracking()
                             .Where(o => from == null || o.ProductionDate >= from).Where(o => to == null || o.ProductionDate <= to.Value.AddDays(1))
                             .Where(o => custId == null || o.CustomerId == custId).OrderByDescending(x => x.Id))
                    {
                        var items = Db.ProductionOrderItems.AsNoTracking().Where(i => i.OrderId == o.Id).ToList();
                        if (prodId != null && !items.Any(i => i.ProductId == prodId)) continue;
                        var names = string.Join(" + ", items.Select(i => i.ProductId).Distinct().ToList().Select(pid => ProdName(pid)));
                        rows.Add((o.ProductionDate ?? o.CreatedDate, "📝 أمر إنتاج", o.DocumentNumber, CustName(o.CustomerId), names, UiFormat.N(items.Sum(i => i.PlannedQtyKg)), UiFormat.N0(items.Sum(i => i.PlannedCartons)), o.IsClosed ? "مغلق" : DocStatuses.ToArabic(o.Status), new DocLinkDto { DocType = "orders", Id = o.Id }));
                    }

                if (opFilter is "" or "production")
                    foreach (var e in Db.ProductionExecutions.AsNoTracking()
                             .Where(e => from == null || e.StartDateTime >= from).Where(e => to == null || e.StartDateTime <= to.Value.AddDays(1))
                             .OrderByDescending(x => x.Id))
                    {
                        var order = Db.ProductionOrders.AsNoTracking().FirstOrDefault(o => o.Id == e.OrderId);
                        if (custId != null && order?.CustomerId != custId) continue;
                        if (prodId != null && !Db.ProductionOrderItems.AsNoTracking().Any(i => i.OrderId == e.OrderId && i.ProductId == prodId)) continue;
                        var names = string.Join(" + ", Db.ProductionOrderItems.AsNoTracking().Where(i => i.OrderId == e.OrderId).Select(i => i.ProductId).Distinct().ToList().Select(pid => ProdName(pid)));
                        string status = e.IsDayClosed ? "إقفال يوم" : DocStatuses.ToArabic(e.Status);
                        string detail = e.IsDayClosed ? $"{names} | مخرجات ثانوية {UiFormat.N(e.HashfKg + e.NawaKg)} · فاقد {UiFormat.N(e.WastageQtyKg)}" : names;
                        rows.Add((e.StartDateTime ?? e.CreatedDate, "🏭 إنتاج فعلي", e.DocumentNumber, CustName(order?.CustomerId), detail, UiFormat.N(e.ActualQtyKg), UiFormat.N0(e.ActualCartons), status, order != null ? new DocLinkDto { DocType = "orders", Id = order.Id } : null));
                    }

                if (opFilter is "" or "quality")
                    foreach (var c in Db.QualityChecks.AsNoTracking()
                             .Where(c => from == null || c.CheckDate >= from).Where(c => to == null || c.CheckDate <= to.Value.AddDays(1))
                             .OrderByDescending(x => x.Id))
                    {
                        var order = c.OrderId != null ? Db.ProductionOrders.AsNoTracking().FirstOrDefault(o => o.Id == c.OrderId) : null;
                        if (custId != null && order?.CustomerId != custId) continue;
                        if (prodId != null && !c.Items.Any(i => i.ProductId == prodId)) continue;
                        var names = string.Join(" + ", Db.QualityCheckItems.AsNoTracking().Where(i => i.CheckId == c.Id).Select(i => i.ProductId).Distinct().ToList().Select(pid => ProdName(pid)));
                        rows.Add((c.CheckDate ?? c.CreatedDate, "🔬 فحص جودة", c.DocumentNumber, CustName(order?.CustomerId), names, $"مقبول {UiFormat.N(c.AcceptedKg)} / مرفوض {UiFormat.N(c.RejectedKg)}", "—", (c.IsApproved ? "معتمد — " : "مسودة — ") + DecisionAr(c.Decision), new DocLinkDto { DocType = "quality", Id = c.Id }));
                    }

                if (opFilter is "" or "finished")
                    foreach (var f in Db.FinishedGoodsReceipts.AsNoTracking()
                             .Where(f => from == null || f.DeliveryDate >= from).Where(f => to == null || f.DeliveryDate <= to.Value.AddDays(1))
                             .OrderByDescending(x => x.Id))
                    {
                        var order = Db.ProductionOrders.AsNoTracking().FirstOrDefault(o => o.Id == f.OrderId);
                        if (custId != null && order?.CustomerId != custId) continue;
                        if (prodId != null && !Db.FinishedGoodsReceiptItems.AsNoTracking().Any(i => i.ReceiptId == f.Id && i.ProductId == prodId)) continue;
                        var names = string.Join(" + ", Db.FinishedGoodsReceiptItems.AsNoTracking().Where(i => i.ReceiptId == f.Id).Select(i => i.ProductId).Distinct().ToList().Select(pid => ProdName(pid)));
                        double tot = Db.FinishedGoodsReceiptItems.AsNoTracking().Where(i => i.ReceiptId == f.Id).Sum(i => i.ReceivedQtyKg);
                        string st = f.ReceiptStatus == "Full" ? "مستلم بالكامل" : f.ReceiptStatus == "Partial" ? "استلام جزئي" : "بانتظار الاستلام";
                        rows.Add((f.DeliveryDate ?? f.CreatedDate, "📦 استلام تام", f.DocumentNumber, CustName(order?.CustomerId), names, UiFormat.N(tot), UiFormat.N0(Db.FinishedGoodsReceiptItems.AsNoTracking().Where(i => i.ReceiptId == f.Id).Sum(i => i.PackageCount)), st, new DocLinkDto { DocType = "finishedgoods", Id = f.Id }));
                    }

                if (opFilter is "" or "delivery")
                    foreach (var d in Db.CustomerDeliveries.AsNoTracking()
                             .Where(d => from == null || d.DeliveryDate >= from).Where(d => to == null || d.DeliveryDate <= to.Value.AddDays(1))
                             .Where(d => custId == null || d.CustomerId == custId).OrderByDescending(x => x.Id))
                    {
                        if (prodId != null && !Db.CustomerDeliveryItems.AsNoTracking().Any(i => i.DeliveryId == d.Id && i.ProductId == prodId)) continue;
                        var names = string.Join(" + ", Db.CustomerDeliveryItems.AsNoTracking().Where(i => i.DeliveryId == d.Id).Select(i => i.ProductId).Distinct().ToList().Select(pid => ProdName(pid)));
                        string st = d.IsApproved ? $"معتمد — مفوتر {UiFormat.N(d.InvoicedQtyKg)}" : "مسودة";
                        rows.Add((d.DeliveryDate ?? d.CreatedDate, "🚛 تسليم عميل", d.DocumentNumber, CustName(d.CustomerId), names, UiFormat.N(d.TotalQtyKg), UiFormat.N0(d.TotalCartons), st, new DocLinkDto { DocType = "delivery", Id = d.Id }));
                    }

                foreach (var row in rows.OrderByDescending(x => x.dt).Take(2000))
                {
                    r.Rows.Add(new object[] { row.op, row.doc, UiFormat.DT(row.dt), row.cust, row.item, row.qty, row.cartons, row.status });
                    r.RowLinks.Add(row.link);
                }

                r.Summary["عدد العمليات"] = UiFormat.N0(r.Rows.Count);
                r.Summary["استلام"] = UiFormat.N0(rows.Count(x => x.op.Contains("استلام"))) + " | خطة " + UiFormat.N0(rows.Count(x => x.op.Contains("خطة")))
                    + " | أمر " + UiFormat.N0(rows.Count(x => x.op.Contains("أمر"))) + " | إنتاج " + UiFormat.N0(rows.Count(x => x.op.Contains("إنتاج")))
                    + " | فحص " + UiFormat.N0(rows.Count(x => x.op.Contains("فحص"))) + " | تام " + UiFormat.N0(rows.Count(x => x.op.Contains("استلام تام")))
                    + " | تسليم " + UiFormat.N0(rows.Count(x => x.op.Contains("تسليم")));
                return r;
            }

            // ═══ 4) كشف الصنف الشامل ═══
            case "item_statement":
            {
                var r = new ReportResult
                {
                    TitleAr = "كشف الصنف الشامل — الرحلة الكاملة من الاستلام حتى المتبقي",
                    Columns = new List<string> { "الصنف", "النوع", "العميل", "المستلم (كجم)", "المخطط (كجم)", "المنتَج (كجم)",
                        "المقبول (كجم)", "مخزون التام (كجم)", "المسلَّم (كجم)", "المفوتر (كجم)", "المتبقي (كجم)" }
                };
                var trace = new TraceabilityService(Db, Session, Numbering);
                var journeys = trace.GetJourneys(custId, prodId);
                double tRec = 0, tPl = 0, tPr = 0, tAc = 0, tSt = 0, tDe = 0, tIn = 0;
                foreach (var j in journeys)
                {
                    tRec += j.ReceivedKg; tPl += j.PlannedKg; tPr += j.ProducedKg; tAc += j.AcceptedKg;
                    tSt += j.InStockKg; tDe += j.DeliveredKg; tIn += j.InvoicedKg;
                    r.Rows.Add(new object[]
                    {
                        j.ProductName, j.ItemTypeAr, j.CustomerName,
                        UiFormat.N(j.ReceivedKg), UiFormat.N(j.PlannedKg), UiFormat.N(j.ProducedKg),
                        UiFormat.N(j.AcceptedKg), UiFormat.N(j.InStockKg), UiFormat.N(j.DeliveredKg),
                        UiFormat.N(j.InvoicedKg), UiFormat.N(j.RemainingKg)
                    });
                }
                r.Summary["عدد الأصناف"] = UiFormat.N0(r.Rows.Count);
                r.Summary["الإجمالي — مستلم"] = UiFormat.N(tRec);
                r.Summary["الإجمالي — مخطط"] = UiFormat.N(tPl);
                r.Summary["الإجمالي — منتَج"] = UiFormat.N(tPr);
                r.Summary["الإجمالي — مسلَّم"] = UiFormat.N(tDe);
                r.Summary["الإجمالي — متبقي"] = UiFormat.N(tSt);
                return r;
            }

            // ═══ 5) كشف العميل الشامل ═══
            case "customer_statement":
            {
                var r = new ReportResult
                {
                    TitleAr = "كشف العميل الشامل — الاستلام والتخطيط والإنتاج والمخزون والتسليم والفوترة",
                    Columns = new List<string> { "العميل", "المستلم (كجم)", "المخطط (كجم)", "المنتَج (كجم)", "المقبول (كجم)",
                        "مخزون التام (كجم)", "المسلَّم (كجم)", "المفوتر (كجم)", "غير المفوتر (كجم)" }
                };
                var whFg = Db.Warehouses.AsNoTracking().FirstOrDefault(w => w.WarehouseCode == "WFG")?.Id ?? 0;
                var custQ = Db.Customers.AsNoTracking().OrderBy(c => c.CustomerName).AsQueryable();
                if (custId != null) custQ = custQ.Where(c => c.Id == custId);
                foreach (var c in custQ.ToList())
                {
                    double received = Db.Lots.AsNoTracking().Where(l => l.CustomerId == c.Id).Sum(l => l.InitialQtyKg);
                    double planned = Db.ProductionPlanItems.AsNoTracking().Where(i => i.CustomerId == c.Id).Sum(i => i.PlannedQtyKg);
                    double produced = Db.ProductionOrderItems.AsNoTracking().Where(i => i.CustomerId == c.Id).Sum(i => i.ProducedQtyKg);
                    double accepted = Db.QualityCheckItems.AsNoTracking()
                        .Join(Db.QualityChecks.AsNoTracking(), q => q.CheckId, ch => ch.Id, (q, ch) => new { q, ch })
                        .Where(x => x.ch.IsApproved && x.ch.OrderId != null
                            && Db.ProductionOrders.Any(o => o.Id == x.ch.OrderId && o.CustomerId == c.Id))
                        .Sum(x => x.q.AcceptedQtyKg);
                    double stock = Db.StockBalances.AsNoTracking().Where(b => b.WarehouseId == whFg && b.CustomerId == c.Id).Sum(b => b.QtyKg); // §1.50.66 — مجموع كل العبوات للعميل (4كجم+8كجم) — للعرض الإجمالي، التفصيل في تقرير آخر
                    double delivered = Db.CustomerDeliveries.AsNoTracking().Where(d => d.CustomerId == c.Id && d.IsApproved).Sum(d => d.TotalQtyKg);
                    double invoiced = Db.CustomerDeliveries.AsNoTracking().Where(d => d.CustomerId == c.Id && d.IsApproved).Sum(d => d.InvoicedQtyKg);
                    if (received + planned + produced + stock + delivered <= 0 && custId == null) continue; // عميل بلا نشاط
                    r.Rows.Add(new object[]
                    {
                        c.CustomerName, UiFormat.N(received), UiFormat.N(planned), UiFormat.N(produced), UiFormat.N(accepted),
                        UiFormat.N(stock), UiFormat.N(delivered), UiFormat.N(invoiced), UiFormat.N(Math.Max(0, delivered - invoiced))
                    });
                }
                r.Summary["عدد العملاء"] = UiFormat.N0(r.Rows.Count);
                return r;
            }

            // ═══ 6) الإنتاج اليومي ═══
            case "daily_production":
            {
                var r = new ReportResult
                {
                    TitleAr = "تقرير الإنتاج اليومي — المنتَج والمخرجات الثانوية والتوقفات لكل أمر",
                    RowLinks = new List<DocLinkDto>()
                };
                // §لا أسماء مخرجات مثبّتة: عمود لكل مخرج ثانوي معرَّف في إعدادات الأصناف
                var bpDefs = Db.ByProducts.AsNoTracking().Where(b => b.IsActive || Db.ExecutionByProducts.Any(x => x.ByProductId == b.Id)).OrderBy(b => b.Id).ToList();
                r.Columns = new List<string> { "اليوم", "الجلسة", "الأمر", "العميل", "الأصناف", "المنتَج (كجم)", "الكراتين" };
                foreach (var b in bpDefs) r.Columns.Add($"{b.ByProductNameAr} ({b.UnitOfMeasure})");
                if (bpDefs.Count == 0) r.Columns.Add("المخرجات الثانوية (كجم)");
                r.Columns.AddRange(new[] { "الفاقد (كجم)", "توقفات (ساعة)", "الحالة", "المخطط (كرتون)", "الفرق (كرتون)", "سبب التوقف", "الخام المستهلك فعليًا (كجم)", "ملاحظات" });
                var q = Db.ProductionExecutions.AsNoTracking()
                    .Where(e => e.Status == "Completed" || e.IsDayClosed);
                if (from != null) q = q.Where(e => e.StartDateTime >= from);
                if (to != null) q = q.Where(e => e.StartDateTime <= to.Value.AddDays(1));
                if (custId != null) q = q.Where(e => Db.ProductionOrders.Any(o => o.Id == e.OrderId && o.CustomerId == custId));

                double tProd = 0, tWaste = 0, tDown = 0; int tCartons = 0;
                var tBy = bpDefs.ToDictionary(b => b.Id, _ => 0.0);
                foreach (var e in q.OrderByDescending(x => x.StartDateTime).ToList())
                {
                    var order = Db.ProductionOrders.AsNoTracking().FirstOrDefault(o => o.Id == e.OrderId);
                    var names = string.Join(" + ", Db.ProductionOrderItems.AsNoTracking().Where(i => i.OrderId == e.OrderId).Select(i => i.ProductId).Distinct().ToList().Select(pid => ProdName(pid)));
                    double down = Db.ExecutionDowntimes.AsNoTracking().Where(d => d.ExecutionId == e.Id).Sum(d => d.Hours);
                    // §المخرجات من سجلات الجلسة (ExecutionByProducts) — ومع رجوع للأعمدة
                    // القديمة (HashfKg/NawaKg) للبيانات المسجّلة قبل الديناميكية
                    var byQty = bpDefs.ToDictionary(b => b.Id, _ => 0.0);
                    foreach (var rec in Db.ExecutionByProducts.AsNoTracking().Where(x => x.ExecutionId == e.Id).ToList())
                        if (byQty.ContainsKey(rec.ByProductId)) byQty[rec.ByProductId] += (double)rec.Qty;
                    if (byQty.Values.All(v => v == 0))
                    {
                        var legacy = ByProductLegacySplit(e.HashfKg, e.NawaKg);
                        foreach (var kv in legacy) if (byQty.ContainsKey(kv.Key)) byQty[kv.Key] = kv.Value;
                    }

                    tProd += e.ActualQtyKg; tCartons += e.ActualCartons; tWaste += e.WastageQtyKg; tDown += down;
                    foreach (var kv in byQty) tBy[kv.Key] += kv.Value;

                    var cells = new List<object>
                    {
                        UiFormat.D(e.StartDateTime), e.DocumentNumber, order?.DocumentNumber ?? "—", CustName(order?.CustomerId), names,
                        UiFormat.N(e.ActualQtyKg), UiFormat.N0(e.ActualCartons)
                    };
                    if (bpDefs.Count > 0) foreach (var b in bpDefs) cells.Add(UiFormat.N(byQty[b.Id]));
                    else cells.Add(UiFormat.N(e.HashfKg + e.NawaKg));
                    cells.Add(UiFormat.N(e.WastageQtyKg));
                    cells.Add(UiFormat.N(down, 1));
                    cells.Add(e.IsDayClosed ? "إقفال يوم 🔒" : DocStatuses.ToArabic(e.Status));
                    int planned = Db.ProductionOrderItems.AsNoTracking().Where(i => i.OrderId == e.OrderId).Sum(i => i.PlannedCartons);
                    cells.Add(UiFormat.N0(planned));
                    cells.Add(UiFormat.N0(planned - e.ActualCartons));
                    cells.Add(string.Join("؛ ", Db.ExecutionDowntimes.AsNoTracking().Where(d => d.ExecutionId == e.Id).Select(d => d.ReasonAr).ToList()));
                    cells.Add(UiFormat.N(e.ConsumedRawKg)); cells.Add(e.ClosingNotes ?? "");
                    r.Rows.Add(cells.ToArray());
                    r.RowLinks.Add(order != null ? new DocLinkDto { DocType = "orders", Id = order.Id } : null);
                }
                r.Summary["عدد جلسات الإنتاج"] = UiFormat.N0(r.Rows.Count);
                r.Summary["إجمالي المنتَج (كجم)"] = UiFormat.N(tProd);
                r.Summary["إجمالي الكراتين"] = UiFormat.N0(tCartons);
                if (bpDefs.Count > 0)
                    foreach (var b in bpDefs) r.Summary[$"إجمالي {b.ByProductNameAr} ({b.UnitOfMeasure})"] = UiFormat.N(tBy[b.Id]);
                else
                    r.Summary["إجمالي المخرجات الثانوية (كجم)"] = UiFormat.N(tBy.Values.Sum());
                r.Summary["إجمالي الفاقد (كجم)"] = UiFormat.N(tWaste);
                r.Summary["إجمالي التوقفات (ساعة)"] = UiFormat.N(tDown, 1);
                return r;
            }

            // ═══ 7) الفحص التفصيلي ═══
            // ═══ §توازن الإنتاج: إجراء رقابي لا قيد مانع ═══
            // في تصنيع التمور يزيد وزن الخارج عن الداخل لإضافة الماء أثناء التشغيل،
            // والماء لا يُسجَّل صنفاً ولا مدخلاً. فالنظام لا يرفض العملية ولا يفترض معادلة،
            // بل يحسب الفرق ونسبة الانحراف ويعرضهما للمراجعة الرقابية.
            case "production_balance":
            {
                var r = new ReportResult
                {
                    TitleAr = "تقرير توازن الإنتاج — وزن المدخلات مقابل وزن المخرجات وفرق الانحراف",
                    Columns = new List<string>
                    {
                        "الجلسة", "الأمر", "العميل", "وزن الخام المسجل (كجم)", "المنتج التام (كجم)",
                        "المخرجات الثانوية (كجم)", "الفاقد (كجم)", "إجمالي المخرجات (كجم)",
                        "فرق الوزن (كجم)", "نسبة الانحراف ٪", "الملاحظة الرقابية"
                    },
                    RowLinks = new List<DocLinkDto>()
                };
                var q = Db.ProductionExecutions.AsNoTracking().Where(e => e.IsDayClosed);
                if (from != null) q = q.Where(e => e.StartDateTime >= from);
                if (to != null) q = q.Where(e => e.StartDateTime <= to.Value.AddDays(1));
                if (custId != null) q = q.Where(e => Db.ProductionOrders.Any(o => o.Id == e.OrderId && o.CustomerId == custId));

                double tIn = 0, tOut = 0; int flagged = 0;
                foreach (var e in q.OrderByDescending(x => x.StartDateTime))
                {
                    var order = Db.ProductionOrders.AsNoTracking().FirstOrDefault(o => o.Id == e.OrderId);
                    double rawIn = e.ConsumedRawKg;
                    double byTotal = Db.ExecutionByProducts.AsNoTracking()
                        .Where(b => b.ExecutionId == e.Id).Sum(b => (double)b.Qty);
                    if (byTotal == 0) byTotal = e.HashfKg + e.NawaKg;   // §البيانات السابقة للديناميكية
                    double outs = e.ActualQtyKg + byTotal + e.WastageQtyKg;
                    double diff = Math.Round(outs - rawIn, 1);
                    double pct = rawIn > 0 ? Math.Round(diff / rawIn * 100, 2) : 0;

                    // §إجراء رقابي: زيادة الوزن تحتاج مراجعة (ماء التشغيل غالباً) — لا رفض
                    string note = diff > 0.001
                        ? "زيادة في الوزن تحتاج إلى مراجعة رقابية (ماء التشغيل لا يُسجَّل في النظام)"
                        : diff < -0.001 ? "نقص في الوزن — راجع الفاقد والتوالف" : "متوازن";
                    if (diff > 0.001) flagged++;

                    tIn += rawIn; tOut += outs;
                    r.Rows.Add(new object[]
                    {
                        e.DocumentNumber, order?.DocumentNumber ?? "—", CustName(order?.CustomerId),
                        UiFormat.N(rawIn), UiFormat.N(e.ActualQtyKg), UiFormat.N(byTotal), UiFormat.N(e.WastageQtyKg),
                        UiFormat.N(outs), (diff > 0 ? "+" : "") + UiFormat.N(diff), pct, note
                    });
                    r.RowLinks.Add(order != null ? new DocLinkDto { DocType = "orders", Id = order.Id } : null);
                }

                double tDiff = Math.Round(tOut - tIn, 1);
                r.Summary["عدد الجلسات"] = UiFormat.N0(r.Rows.Count);
                r.Summary["إجمالي وزن الخام المسجل (كجم)"] = UiFormat.N(tIn);
                r.Summary["إجمالي المخرجات (كجم)"] = UiFormat.N(tOut);
                r.Summary["فرق الوزن الإجمالي (كجم)"] = (tDiff > 0 ? "+" : "") + UiFormat.N(tDiff);
                r.Summary["نسبة الانحراف الإجمالية ٪"] = tIn > 0 ? Math.Round(tDiff / tIn * 100, 2).ToString("N2") : "0";
                r.Summary["جلسات تحتاج مراجعة رقابية"] = UiFormat.N0(flagged);
                r.Summary["ملاحظة"] = "الماء المستخدم أثناء التشغيل لا يُسجَّل صنفاً ولا مدخلاً — والفرق إجراء رقابي لا يمنع الاعتماد";
                return r;
            }

            case "quality_detail":
            {
                string dec = p.GetValueOrDefault("decision") ?? "";
                var r = new ReportResult
                {
                    TitleAr = "تقرير الفحص التفصيلي — القرار والمعايير المخبرية لكل فحص",
                    Columns = new List<string> { "الفحص", "التاريخ", "الأمر", "العميل", "الأصناف", "مقبول (كجم)", "مرفوض (كجم)",
                        "رطوبة %", "سكريات Brix°", "قشرة %", "شوائب %", "القرار", "الحالة" },
                    RowLinks = new List<DocLinkDto>()
                };
                var q = Db.QualityChecks.AsNoTracking().AsQueryable();
                if (from != null) q = q.Where(c => c.CheckDate >= from);
                if (to != null) q = q.Where(c => c.CheckDate <= to.Value.AddDays(1));
                if (!string.IsNullOrEmpty(dec)) q = q.Where(c => c.Decision == dec);
                if (custId != null) q = q.Where(c => c.OrderId != null && Db.ProductionOrders.Any(o => o.Id == c.OrderId && o.CustomerId == custId));

                double tAcc = 0, tRej = 0;
                foreach (var c in q.OrderByDescending(x => x.Id))
                {
                    var order = c.OrderId != null ? Db.ProductionOrders.AsNoTracking().FirstOrDefault(o => o.Id == c.OrderId) : null;
                    if (prodId != null && !Db.QualityCheckItems.AsNoTracking().Any(i => i.CheckId == c.Id && i.ProductId == prodId)) continue;
                    var names = string.Join(" + ", Db.QualityCheckItems.AsNoTracking().Where(i => i.CheckId == c.Id).Select(i => i.ProductId).Distinct().ToList().Select(pid => ProdName(pid)));
                    tAcc += c.AcceptedKg; tRej += c.RejectedKg;
                    r.Rows.Add(new object[]
                    {
                        c.DocumentNumber, UiFormat.D(c.CheckDate), order?.DocumentNumber ?? "يدوي", CustName(order?.CustomerId), names,
                        UiFormat.N(c.AcceptedKg), UiFormat.N(c.RejectedKg),
                        UiFormat.N(c.MoisturePct, 1), UiFormat.N(c.BrixDeg, 1), UiFormat.N(c.SkinSeparationPct, 1), UiFormat.N(c.ImpuritiesPct, 1),
                        DecisionAr(c.Decision), c.IsApproved ? "معتمد" : "مسودة"
                    });
                    r.RowLinks.Add(new DocLinkDto { DocType = "quality", Id = c.Id });
                }
                r.Summary["عدد الفحوصات"] = UiFormat.N0(r.Rows.Count);
                r.Summary["إجمالي المقبول (كجم)"] = UiFormat.N(tAcc);
                r.Summary["إجمالي المرفوض (كجم)"] = UiFormat.N(tRej);
                r.Summary["نسبة القبول"] = (tAcc + tRej) > 0 ? UiFormat.Pct(tAcc / (tAcc + tRej) * 100) : "—";
                return r;
            }

            // ═══ 8) حركة المخازن — كل حركة وارد/منصرف بمستندها + زر تنقل ═══
            case "warehouse_movements":
            {
                int? whId = int.TryParse(p.GetValueOrDefault("warehouse"), out var whv) ? whv : null;
                string mtype = p.GetValueOrDefault("mtype") ?? "";
                var q = Db.InventoryTransactions.AsNoTracking().AsQueryable();
                if (from != null) q = q.Where(t => t.TxnDate >= from);
                if (to != null) q = q.Where(t => t.TxnDate <= to.Value.AddDays(1));
                if (whId != null) q = q.Where(t => t.WarehouseId == whId);
                if (custId != null) q = q.Where(t => t.CustomerId == custId);
                if (prodId != null) q = q.Where(t => t.ProductId == prodId);
                if (mtype == "in") q = q.Where(t => t.MovementType == Core.Domain.Enums.MovementType.Inbound);
                else if (mtype == "out") q = q.Where(t => t.MovementType == Core.Domain.Enums.MovementType.Outbound);

                // §1.50.67 FIX: لا حد 3000 يخفي حركات — الآن 10000 مع تحذير، والتصدير الكامل بدون حد
                int totalCount = q.Count();
                bool truncated = totalCount > 10000;
                var r = new ReportResult
                {
                    TitleAr = "حركة المخازن — كل حركة وارد ومنصرف بمستندها الكامل (اضغط + لفتح المستند)" + (truncated ? $" — يعرض أحدث 10000 من {totalCount} حركة" : ""),
                    Columns = new List<string> { "التاريخ", "المخزن", "النوع", "الصنف/المادة", "الدفعة", "العبوة", "العميل", "الوارد (كجم)", "المنصرف (كجم)", "الكمية الموقعة (كجم)", "العبوات", "المستند المرجعي", "البيان" },
                    RowLinks = new List<DocLinkDto>()
                };
                double tIn = 0, tOut = 0;
                foreach (var t in q.OrderByDescending(x => x.TxnDate).ThenByDescending(x => x.Id).Take(10000))
                {
                    string itemName = t.ProductId != null ? ProdName(t.ProductId.Value)
                        : t.MaterialId != null ? Db.AuxiliaryMaterials.AsNoTracking().Where(m => m.Id == t.MaterialId).Select(m => m.MaterialNameAr).FirstOrDefault() ?? "-" : "-";
                    bool inbound = t.MovementType == Core.Domain.Enums.MovementType.Inbound;
                    if (inbound) tIn += Math.Abs(t.QtyKg); else tOut += Math.Abs(t.QtyKg);
                    string packNameMov = t.PackagingTypeId != null ? Db.PackagingTypes.AsNoTracking().Where(p => p.Id == t.PackagingTypeId).Select(p => p.PackageNameAr).FirstOrDefault() ?? "—" : "—";
                    double absKg = Math.Abs(t.QtyKg);
                    string signedKg = (inbound ? "+" : "−") + UiFormat.N(absKg);
                    r.Rows.Add(new object[]
                    {
                        UiFormat.DT(t.TxnDate),
                        Db.Warehouses.AsNoTracking().Where(w => w.Id == t.WarehouseId).Select(w => w.WarehouseNameAr).FirstOrDefault() ?? "-",
                        inbound ? "⬆ وارد" : "⬇ منصرف",
                        itemName,
                        Db.Lots.AsNoTracking().Where(l => l.Id == t.LotId).Select(l => l.LotCode).FirstOrDefault() ?? "—",
                        packNameMov,
                        CustName(t.CustomerId),
                        inbound ? UiFormat.N(absKg) : "—",
                        inbound ? "—" : UiFormat.N(absKg),
                        signedKg,
                        UiFormat.N0(Math.Abs(t.PackageCount)),
                        $"{t.ReferenceDocType}: {t.ReferenceDocNumber}",
                        t.Notes ?? "—"
                    });
                    r.RowLinks.Add(LinkForTxn(t));
                }
                r.Summary["عدد الحركات"] = UiFormat.N0(r.Rows.Count) + (truncated ? $" (من {totalCount} — فلتر بالتاريخ لتقليل العدد)" : "");
                r.Summary["إجمالي الوارد (كجم)"] = UiFormat.N(tIn) + (truncated ? " (على المعروض فقط)" : "");
                r.Summary["إجمالي المنصرف (كجم)"] = UiFormat.N(tOut) + (truncated ? " (على المعروض فقط)" : "");
                r.Summary["الصافي"] = UiFormat.N(tIn - tOut) + (truncated ? " (على المعروض فقط)" : "");
                if (truncated) r.Summary["⚠️ تنبيه"] = $"يوجد {totalCount} حركة تطابق الفلتر — يعرض أحدث 10000 فقط. استخدم فلتر التاريخ أو تصدير Excel الكامل.";
                return r;
            }

            // ═══ 9) حركة الصنف — دفتر أستاذ برصيد جارٍ على مستوى المخزن ═══
            case "item_movements":
            {
                int? whId = int.TryParse(p.GetValueOrDefault("warehouse"), out var whv2) ? whv2 : null;
                var r = new ReportResult
                {
                    TitleAr = prodId != null
                        ? $"حركة الصنف «{ProdName(prodId.Value)}» — وارد/منصرف/رصيد جارٍ {(whId != null ? "في المخزن المحدد" : "في كل المخازن")}"
                        : "حركة الصنف — اختر صنفاً من الفلاتر لعرض حركته",
                    Columns = new List<string> { "التاريخ", "المخزن", "المستند", "النوع", "الكمية (كجم)", "العبوات", "الرصيد بعد الحركة (كجم)", "البيان" },
                    RowLinks = new List<DocLinkDto>()
                };
                if (prodId == null)
                {
                    r.Summary["تنبيه"] = "اختر صنفاً من فلتر «الصنف» ثم اضغط تشغيل.";
                    return r;
                }
                var q = Db.InventoryTransactions.AsNoTracking()
                    .Where(t => t.ProductId == prodId)
                    .OrderBy(t => t.TxnDate).ThenBy(t => t.Id).AsQueryable();
                if (whId != null) q = q.Where(t => t.WarehouseId == whId);
                if (from != null) q = q.Where(t => t.TxnDate >= from);
                if (to != null) q = q.Where(t => t.TxnDate <= to.Value.AddDays(1));
                if (custId != null) q = q.Where(t => t.CustomerId == custId);

                double running = 0, tIn = 0, tOut = 0;
                foreach (var t in q.ToList())
                {
                    running += t.QtyKg; // الكمية موقعة: + وارد / − منصرف
                    bool inbound = t.MovementType == Core.Domain.Enums.MovementType.Inbound;
                    if (inbound) tIn += Math.Abs(t.QtyKg); else tOut += Math.Abs(t.QtyKg);
                    r.Rows.Add(new object[]
                    {
                        UiFormat.DT(t.TxnDate),
                        Db.Warehouses.AsNoTracking().Where(w => w.Id == t.WarehouseId).Select(w => w.WarehouseNameAr).FirstOrDefault() ?? "-",
                        $"{t.ReferenceDocType}: {t.ReferenceDocNumber}",
                        inbound ? "⬆ وارد" : "⬇ منصرف",
                        (t.QtyKg >= 0 ? "+" : "−") + UiFormat.N(Math.Abs(t.QtyKg)),
                        UiFormat.N0(Math.Abs(t.PackageCount)),
                        UiFormat.N(running),
                        t.Notes ?? "—"
                    });
                    r.RowLinks.Add(LinkForTxn(t));
                }
                r.Summary["عدد الحركات"] = UiFormat.N0(r.Rows.Count);
                r.Summary["إجمالي الوارد (كجم)"] = UiFormat.N(tIn);
                r.Summary["إجمالي المنصرف (كجم)"] = UiFormat.N(tOut);
                r.Summary["الرصيد النهائي (كجم)"] = UiFormat.N(running);
                return r;
            }

            // ═══ 10) المخزن الشامل — الأرصدة الحالية لكل صنف/دفعة/عميل ═══
            case "warehouse_full":
            {
                int? whId = int.TryParse(p.GetValueOrDefault("warehouse"), out var whv3) ? whv3 : null;
                var r = new ReportResult
                {
                    TitleAr = whId != null
                        ? $"تقرير المخزن الشامل — {Db.Warehouses.AsNoTracking().Where(w => w.Id == whId).Select(w => w.WarehouseNameAr).FirstOrDefault()}"
                        : "تقرير المخزن الشامل — أرصدة كل المخازن (كل عبوة منفصلة + المتاح)",
                    Columns = new List<string> { "المخزن", "الصنف/المادة", "الدفعة", "العبوة", "العميل", "الرصيد (كجم)", "العبوات", "محجوز (كجم)", "تحت المعالجة (كجم)", "المتاح (كجم)", "آخر حركة", "تنبيه" }
                };
                var q = Db.StockBalances.AsNoTracking().Where(b => b.QtyKg != 0 || b.PackageCount != 0); // §1.50.66 — يعرض كل عبوة منفصلة (4كجم/8كجم) — لا يخلط
                if (whId != null) q = q.Where(b => b.WarehouseId == whId);
                if (custId != null) q = q.Where(b => b.CustomerId == custId);
                if (prodId != null) q = q.Where(b => b.ProductId == prodId);

                var perWh = new Dictionary<int, (double kg, int pkg)>();
                var warehouseCodes = Db.Warehouses.AsNoTracking().ToDictionary(w => w.Id, w => w.WarehouseCode);
                int rawWarehouseId = warehouseCodes.FirstOrDefault(w => w.Value == "WRM").Key;
                var rawByLot = Db.StockBalances.AsNoTracking()
                    .Where(b => b.WarehouseId == rawWarehouseId && b.LotId != null).ToList()
                    .GroupBy(b => b.LotId!.Value).ToDictionary(g => g.Key, g => g.Sum(b => Math.Max(0, b.QtyKg)));
                int negCount = 0;
                double totalAvailable = 0;
                foreach (var b in q.OrderBy(x => x.WarehouseId).ToList())
                {
                    string itemName = b.ProductId != null ? ProdName(b.ProductId.Value)
                        : b.MaterialId != null ? Db.AuxiliaryMaterials.AsNoTracking().Where(m => m.Id == b.MaterialId).Select(m => m.MaterialNameAr).FirstOrDefault() ?? "-" : "-";
                    // §1.50.67 FIX: آخر حركة يجب أن تشمل PackagingTypeId — كان يأخذ حركة عبوة أخرى
                    var lastTxn = Db.InventoryTransactions.AsNoTracking()
                        .Where(t => t.WarehouseId == b.WarehouseId && t.ProductId == b.ProductId && t.MaterialId == b.MaterialId
                                    && t.LotId == b.LotId && t.CustomerId == b.CustomerId && t.PackagingTypeId == b.PackagingTypeId)
                        .OrderByDescending(t => t.TxnDate).ThenByDescending(t => t.Id).FirstOrDefault();
                    string packNameFull = b.PackagingTypeId != null ? Db.PackagingTypes.AsNoTracking().Where(p => p.Id == b.PackagingTypeId).Select(p => p.PackageNameAr).FirstOrDefault() ?? "—" : "—";
                    string warn = "";
                    if (b.QtyKg < -0.001 || b.PackageCount < 0)
                    {
                        warn = "⛔ رصيد سالب";
                        negCount++;
                    }
                    else if (b.ProductId != null && b.PackageCount > 0)
                    {
                        // The selected package overrides the product default, just
                        // as at issuance and receipt (two sizes can share a product).
                        double w = UnitsPolicy.CartonWeight(Db, b.ProductId.Value, b.PackagingTypeId);
                        if (w > 0)
                        {
                            double computed = Math.Round(b.PackageCount * w, 1);
                            double tol = Math.Max(1.0, Math.Abs(b.QtyKg) * 0.02);
                            if (Math.Abs(b.QtyKg - computed) > tol) warn = $"⚠️ كجم {b.QtyKg:N1} ≠ {b.PackageCount}×{w:N1}={computed:N1}";
                        }
                    }
                    // A lot is a RAW input identity. Its reservation/processing
                    // cannot be reused as the available FINISHED GOODS balance.
                    // Split raw reservations across WRM package rows exactly once.
                    double reservedKg = 0, underTreatKg = 0, availableKg = b.QtyKg;
                    var whCode = warehouseCodes.GetValueOrDefault(b.WarehouseId);
                    if (whCode == "WRM" && b.LotId is int rawLotId)
                    {
                        var lotInfo = Db.Lots.AsNoTracking().Where(l => l.Id == rawLotId)
                            .Select(l => new { l.ReservedQtyKg, l.UnderTreatmentQtyKg }).FirstOrDefault();
                        if (lotInfo != null)
                        {
                            double rawStock = rawByLot.GetValueOrDefault(rawLotId);
                            double share = rawStock > 0 ? Math.Max(0, b.QtyKg) / rawStock : 0;
                            reservedKg = Math.Min(Math.Max(0, b.QtyKg), lotInfo.ReservedQtyKg * share);
                            // Already physically transferred out of WRM to WTRT;
                            // show processing as information, not a second deduction.
                            underTreatKg = lotInfo.UnderTreatmentQtyKg * share;
                            availableKg = Math.Max(0, b.QtyKg - reservedKg);
                        }
                    }
                    else if (whCode == "WTRT")
                        availableKg = 0; // work in progress cannot be dispensed
                    totalAvailable += availableKg;
                    r.Rows.Add(new object[]
                    {
                        Db.Warehouses.AsNoTracking().Where(w => w.Id == b.WarehouseId).Select(w => w.WarehouseNameAr).FirstOrDefault() ?? "-",
                        itemName,
                        Db.Lots.AsNoTracking().Where(l => l.Id == b.LotId).Select(l => l.LotCode).FirstOrDefault() ?? "—",
                        packNameFull,
                        CustName(b.CustomerId),
                        UiFormat.N(b.QtyKg), UiFormat.N0(b.PackageCount),
                        reservedKg > 0 ? UiFormat.N(reservedKg) : "—",
                        underTreatKg > 0 ? UiFormat.N(underTreatKg) : "—",
                        UiFormat.N(availableKg),
                        lastTxn != null ? $"{UiFormat.D(lastTxn.TxnDate)} — {(lastTxn.MovementType == Core.Domain.Enums.MovementType.Inbound ? "وارد" : "منصرف")} {UiFormat.N(Math.Abs(lastTxn.QtyKg))}" : "—",
                        warn
                    });
                    if (!perWh.ContainsKey(b.WarehouseId)) perWh[b.WarehouseId] = (0, 0);
                    var cur = perWh[b.WarehouseId];
                    perWh[b.WarehouseId] = (cur.kg + b.QtyKg, cur.pkg + b.PackageCount);
                }
                foreach (var wh in perWh)
                {
                    string name = Db.Warehouses.AsNoTracking().Where(w => w.Id == wh.Key).Select(w => w.WarehouseNameAr).FirstOrDefault() ?? $"مخزن {wh.Key}";
                    r.Summary[$"رصيد {name}"] = $"{UiFormat.N(wh.Value.kg)} كجم | {UiFormat.N0(wh.Value.pkg)} عبوة";
                }
                r.Summary["عدد الأرصدة"] = UiFormat.N0(r.Rows.Count);
                r.Summary["الإجمالي العام (كجم)"] = UiFormat.N(perWh.Sum(x => x.Value.kg));
                r.Summary["المتاح العام (كجم)"] = UiFormat.N(totalAvailable);
                if (negCount > 0) r.Summary["⛔ أرصدة سالبة"] = $"{negCount} رصيد سالب — راجع سلامة المخزون";
                return r;
            }

            // ═══ 11) تقرير الشحنة الشامل — كشف حساب تمور العميل من الدخول حتى المتبقي ═══
            case "shipment_full":
            {
                int? shipId = int.TryParse(p.GetValueOrDefault("shipment"), out var shv) ? shv : null;
                string lotCode = p.GetValueOrDefault("lot")?.Trim();
                string orderNo = p.GetValueOrDefault("order")?.Trim();
                var r = new ReportResult
                {
                    TitleAr = "تقرير الشحنة الشامل — متى دخلت، كم دخلت، أوامرها ومخرجاتها، وماذا تبقى (كشف حساب تمور العميل)",
                    Columns = new List<string> { "المرحلة", "المرجع", "التاريخ", "العميل", "الصنف", "العبوة", "العدد", "الكمية (كجم)", "الكراتين", "التفاصيل" },
                    RowLinks = new List<DocLinkDto>(),
                    // §الشكل الموحد: عمود المرحلة يلوّن الصفوف، والمعادلة تُعرض في الشريط السفلي
                    StageColumn = "المرحلة"
                };

                var sq = Db.Shipments.AsNoTracking().AsQueryable();
                if (shipId != null) sq = sq.Where(s => s.Id == shipId);
                if (custId != null) sq = sq.Where(s => s.CustomerId == custId);
                if (from != null) sq = sq.Where(s => s.ReceivedDate >= from);
                if (to != null) sq = sq.Where(s => s.ReceivedDate <= to.Value.AddDays(1));

                double gIn = 0, gConsumed = 0, gFinished = 0, gBy = 0, gDelivered = 0, gRawRemain = 0, gFinRemain = 0;
                int whFg = Db.Warehouses.AsNoTracking().FirstOrDefault(w => w.WarehouseCode == "WFG")?.Id ?? 0;

                foreach (var ship in sq.OrderByDescending(s => s.Id).ToList())
                {
                    string cust = CustName(ship.CustomerId);
                    // ترويسة الشحنة
                    r.Rows.Add(new object[]
                    {
                        "🚢 الشحنة", ship.DocumentNumber, UiFormat.D(ship.ReceivedDate), cust,
                        ship.ContainerNumber ?? "—", "—", "—",
                        UiFormat.N(ship.TotalWeightKg), "—",
                        $"وصلت {UiFormat.D(ship.ArrivalDate)} | {ship.ItemCount} أصناف | حالة السند: {(ship.IsApproved ? "معتمد" : "مسودة")}"
                    });
                    r.RowLinks.Add(new DocLinkDto { DocType = "receiving", Id = ship.Id });
                    gIn += ship.TotalWeightKg;

                    var items = Db.ShipmentItems.AsNoTracking().Where(i => i.ShipmentId == ship.Id).ToList();
                    foreach (var it in items)
                    {
                        if (prodId != null && it.ProductId != prodId) continue;
                        var lot = Db.Lots.AsNoTracking().FirstOrDefault(l => l.ShipmentItemId == it.Id);
                        if (lotCode != null && (lot == null || !string.Equals(lot.LotCode, lotCode, StringComparison.OrdinalIgnoreCase))) continue;
                        string rawName = ProdName(it.ProductId);
                        string packName = it.PackagingTypeId != null
                            ? Db.PackagingTypes.AsNoTracking().Where(pk => pk.Id == it.PackagingTypeId).Select(pk => pk.PackageNameAr).FirstOrDefault() ?? "عبوة"
                            : "—";

                        // ── 1) الدخول: كم دخل وعلى أي مستوى (صنف × عبوة) ──
                        var inProgress = Db.RawTreatments.AsNoTracking().Where(t => t.LotId == lot.Id && t.Status == DatesErp.Core.Domain.Entities.TreatmentStatuses.InProgress).ToList();
                        DateTime? readyForLot = inProgress.Count > 0 ? inProgress.Max(t => t.ExpectedReadyAt)
                            : (it.TreatmentRequired == true ? it.TreatmentUntilDate : (DateTime?)null);
                        string treatEntry = it.TreatmentRequired == true
                            ? $" | معالجة: نعم — حتى {UiFormat.D(it.TreatmentUntilDate)} | جاهزية الدفعة: {UiFormat.D(readyForLot ?? it.TreatmentUntilDate)}"
                            : it.TreatmentRequired == false ? " | معالجة: لا" : " | معالجة: سجل سابق";
                        r.Rows.Add(new object[]
                        {
                            "📥 1. الدخول", ship.DocumentNumber, UiFormat.D(ship.ReceivedDate), cust, rawName,
                            $"{packName} × {UiFormat.N(it.UnitWeightKg)} كجم",
                            UiFormat.N0(it.PackageCount), UiFormat.N(it.TotalWeightKg), "—",
                            ReportUnits.TidyLine(
                                ReportUnits.WarehouseEntry(it.TotalWeightKg, it.PackageCount, it.ReceiptUnit ?? ReportUnits.PrimaryTradeUnitAr),
                                $"وحدة الاستلام الأصلية: {it.ReceiptUnit ?? ReportUnits.PrimaryTradeUnitAr}",
                                $"الدفعة: {lot?.LotCode ?? "—"}{treatEntry}")
                        });
                        r.RowLinks.Add(new DocLinkDto { DocType = "receiving", Id = ship.Id });
                        if (lot == null) continue;

                        // ── 2) أوامر الإنتاج التي عُمِلت لهذه الدفعة + مخرجات كل أمر + المتبقي بعده ──
                        double cumulativeConsumed = 0, cumulativeReturned = 0;
                        var orderItems = Db.ProductionOrderItems.AsNoTracking()
                            .Where(oi => oi.LotId == lot.Id)
                            .Join(Db.ProductionOrders.AsNoTracking(), oi => oi.OrderId, o => o.Id, (oi, o) => new { oi, o })
                            .Where(x => orderNo == null || (x.o.DocumentNumber ?? "").Contains(orderNo))
                            .OrderBy(x => x.o.ProductionDate ?? x.o.CreatedDate).ThenBy(x => x.o.Id)
                            .ToList();
                        int ordNo = 0;
                        foreach (var x in orderItems)
                        {
                            ordNo++;
                            cumulativeConsumed += x.oi.PlannedQtyKg;
                            string finName = ProdName(x.oi.ProductId);

                            // مخرجات هذا الأمر: المنتَج + الثانوية (من الإقفالات، أو من جلسات التنفيذ)
                            // إن شمل الأمر دفعات متعددة تُوزَّع الثانوية بنسبة كل بند من مخطط الأمر
                            double orderPlannedTotal = Db.ProductionOrderItems.AsNoTracking()
                                .Where(oi => oi.OrderId == x.o.Id).Sum(oi => oi.PlannedQtyKg);
                            double share = orderPlannedTotal > 0 ? x.oi.PlannedQtyKg / orderPlannedTotal : 1;
                            var closings = Db.PlanClosingItems.AsNoTracking()
                                .Where(ci => ci.OrderId == x.o.Id && ci.LotId == lot.Id).ToList();
                            double hashf, nawa, wastage;
                            if (closings.Count > 0)
                            {
                                hashf = closings.Sum(ci => ci.HashfKg); nawa = closings.Sum(ci => ci.NawaKg); wastage = closings.Sum(ci => ci.WastageKg);
                                cumulativeReturned += closings.Sum(ci => ci.ReturnedToRawKg);
                            }
                            else
                            {
                                var exes = Db.ProductionExecutions.AsNoTracking().Where(e => e.OrderId == x.o.Id).ToList();
                                hashf = exes.Sum(e => e.HashfKg) * share;
                                nawa = exes.Sum(e => e.NawaKg) * share;
                                wastage = exes.Sum(e => e.WastageQtyKg) * share;
                            }
                            double byTotal = hashf + nawa + wastage;
                            double remainingAfter = lot.InitialQtyKg - cumulativeConsumed + cumulativeReturned;

                            // §الكمية الفعلية المسحوبة من الشحنة لهذا الأمر — من تتبع السحب على بند الخطة
                            // (وحدة سطر الاستلام + وزنها + الوزن المكافئ)، لا من مخطط الأمر فقط.
                            var srcPi = x.oi.PlanItemId != null
                                ? Db.ProductionPlanItems.AsNoTracking().FirstOrDefault(pi => pi.Id == x.oi.PlanItemId) : null;
                            string drawTxt = srcPi != null && srcPi.SourceQtyKg > 0
                                ? $"سحب {UiFormat.N(srcPi.SourceQtyInUnit)} {srcPi.SourceUnit ?? ""} (الوزن المكافئ {UiFormat.N(srcPi.SourceQtyKg)} كجم) — من الشحنة {srcPi.ShipmentId} / الدفعة {srcPi.LotId}"
                                : $"خُطط له من الدفعة {UiFormat.N(x.oi.PlannedQtyKg)} كجم ثم اعتُمد وصُرف";

                            r.Rows.Add(new object[]
                            {
                                $"📝 2.{ordNo} أمر إنتاج", x.o.DocumentNumber, UiFormat.D(x.o.ProductionDate ?? x.o.CreatedDate), CustName(x.oi.CustomerId),
                                $"{rawName} ← {finName}", "—", "—",
                                UiFormat.N(x.oi.PlannedQtyKg), UiFormat.N0(x.oi.PlannedCartons),
                                drawTxt
                            });
                            r.RowLinks.Add(new DocLinkDto { DocType = "orders", Id = x.o.Id });

                            // §مخرجات الجودة الفعلية لهذا الأمر (سليم/منسم/مخلفات...) من نتائج الفحص
                            // المعتمدة — أنواع مختلفة لكل أمر حسب ما سُجّل فعلاً، لا مخرَج واحد مفترض.
                            var qcIds = Db.QualityChecks.AsNoTracking().Where(qc => qc.OrderId == x.o.Id && qc.IsApproved)
                                .Select(qc => qc.Id).ToList();
                            var qcResults = Db.InspectionResults.AsNoTracking()
                                .Where(ir => qcIds.Contains(ir.CheckId)).ToList();
                            string qualTxt = "";
                            if (qcResults.Count > 0)
                            {
                                var parts = qcResults.GroupBy(ir => ir.ResultTypeId)
                                    .Select(g => new
                                    {
                                        Name = Db.InspectionResultTypes.AsNoTracking().Where(t => t.Id == g.Key).Select(t => t.NameAr).FirstOrDefault() ?? $"نوع {g.Key}",
                                        Qty = g.Sum(ir => (double)ir.Qty),
                                        Unit = g.First().UnitLabel ?? ""
                                    }).ToList();
                                qualTxt = " | مخرجات الجودة المعتمدة: " + string.Join(" · ", parts.Select(p => $"{p.Name} {UiFormat.N(p.Qty)} {p.Unit}"));
                            }
                            else qualTxt = qcIds.Count > 0 ? " | فحص قيد الإعداد (غير معتمد)" : "";

                            // §قاعدة الوحدات: بعد خطط الإنتاج وحدة التداول هي الكرتون التام —
                            // فيُذكر اسم الصنف وعدد الكراتين بوزنها (وزن الكرتون × العدد = الإجمالي).
                            double cw = x.oi.CartonWeightKg > 0 ? x.oi.CartonWeightKg
                                : (x.oi.ProducedCartons > 0 ? x.oi.ProducedQtyKg / x.oi.ProducedCartons : 0);
                            string doneTxt = ReportUnits.TidyLine(
                                x.oi.ProducedCartons > 0 ? $"{finName}: {ReportUnits.CartonWithWeight(x.oi.ProducedCartons, cw, x.oi.ProducedQtyKg)}" : null,
                                $"ثانوية: {UiFormat.N(byTotal)}",
                                $"فاقد: {UiFormat.N(wastage)}") + qualTxt;
                            r.Rows.Add(new object[]
                            {
                                $"🏭 مخرجات الأمر {ordNo}", x.o.DocumentNumber, UiFormat.D(x.o.ProductionDate ?? x.o.CreatedDate), CustName(x.oi.CustomerId),
                                finName, "—", "—",
                                UiFormat.N(x.oi.ProducedQtyKg), UiFormat.N0(x.oi.ProducedCartons),
                                doneTxt
                            });
                            r.RowLinks.Add(new DocLinkDto { DocType = "orders", Id = x.o.Id });

                            r.Rows.Add(new object[]
                            {
                                $"⚖ المتبقي بعد الأمر {ordNo}", lot.LotCode, "—", cust, rawName, "—", "—",
                                UiFormat.N(remainingAfter), "—",
                                $"الرصيد الخام بعد هذا الأمر (من أصل {UiFormat.N(lot.InitialQtyKg)})"
                            });
                            r.RowLinks.Add(null);

                            gConsumed += x.oi.PlannedQtyKg;
                            gFinished += x.oi.ProducedQtyKg;
                            gBy += byTotal;
                        }
                        if (orderItems.Count == 0)
                        {
                            r.Rows.Add(new object[] { "📝 2. أوامر الإنتاج", "—", "—", cust, rawName, "—", "—", "0", "—", "لم يُعمل لهذه الدفعة أي أمر إنتاج بعد" });
                            r.RowLinks.Add(null);
                        }

                        // ── 3) ماذا خرج تاماً وما هو ──
                        var fgRows = Db.FinishedGoodsReceiptItems.AsNoTracking()
                            .Where(fi => fi.LotId == lot.Id)
                            .Join(Db.FinishedGoodsReceipts.AsNoTracking(), fi => fi.ReceiptId, f => f.Id, (fi, f) => new { fi, f })
                            .OrderBy(x => x.f.DeliveryDate ?? x.f.CreatedDate).ToList();
                        foreach (var fx in fgRows)
                        {
                            r.Rows.Add(new object[]
                            {
                                "📦 3. خرج تام", $"{fx.f.DocumentNumber} (استلام {fx.f.ReceiptNumber ?? "-"})", UiFormat.D(fx.f.DeliveryDate ?? fx.f.CreatedDate), cust,
                                ProdName(fx.fi.ProductId),
                                fx.fi.PackagingTypeId != null ? Db.PackagingTypes.AsNoTracking().Where(pk => pk.Id == fx.fi.PackagingTypeId).Select(pk => pk.PackageNameAr).FirstOrDefault() ?? "-" : "-",
                                UiFormat.N0(fx.fi.PackageCount), UiFormat.N(fx.fi.NetWeightKg), UiFormat.N0(fx.fi.PackageCount),
                                // §قاعدة الوحدات + قاعدة دخول المخازن: اسم الصنف وكراتينه بوزنها،
                                // وكم دخل المخزن بوزن كم.
                                ReportUnits.TidyLine(
                                    $"{ProdName(fx.fi.ProductId)}: {ReportUnits.CartonWithWeight(fx.fi.PackageCount, fx.fi.PackageCount > 0 ? fx.fi.NetWeightKg / fx.fi.PackageCount : 0, fx.fi.NetWeightKg)}",
                                    ReportUnits.WarehouseEntry(fx.fi.ReceivedQtyKg, fx.fi.PackageCount),
                                    "دخل مخزن التام باسم العميل")
                            });
                            r.RowLinks.Add(new DocLinkDto { DocType = "finishedgoods", Id = fx.f.Id });
                        }

                        // ── 4) المسلَّم للعميل من هذه الدفعة ──
                        var dlvRows = Db.CustomerDeliveryItems.AsNoTracking()
                            .Where(di => di.LotId == lot.Id)
                            .Join(Db.CustomerDeliveries.AsNoTracking().Where(d => d.IsApproved), di => di.DeliveryId, d => d.Id, (di, d) => new { di, d })
                            .OrderBy(x => x.d.DeliveryDate ?? x.d.CreatedDate).ToList();
                        foreach (var dx in dlvRows)
                        {
                            r.Rows.Add(new object[]
                            {
                                "🚚 4. تسليم للعميل", dx.d.DocumentNumber, UiFormat.D(dx.d.DeliveryDate ?? dx.d.CreatedDate), cust,
                                ProdName(dx.di.ProductId), "—", UiFormat.N0(dx.di.PackageCount),
                                UiFormat.N(dx.di.QtyKg), UiFormat.N0(dx.di.PackageCount), "سند تسليم معتمد"
                            });
                            r.RowLinks.Add(new DocLinkDto { DocType = "delivery", Id = dx.d.Id });
                            gDelivered += dx.di.QtyKg;
                        }

                        // ── 5) المتبقي الآن: خام في المخزن + تام لم يُسلَّم ──
                        double finRemain = Db.StockBalances.AsNoTracking()
                            .Where(b => b.WarehouseId == whFg && b.LotId == lot.Id).Sum(b => b.QtyKg);
                        int finRemainCartons = Db.StockBalances.AsNoTracking()
                            .Where(b => b.WarehouseId == whFg && b.LotId == lot.Id).Sum(b => b.PackageCount);
                        bool exhausted = lot.InStockQtyKg <= 0.001 && orderItems.Count > 0;
                        var lastOrder = orderItems.OrderByDescending(z => z.o.ProductionDate ?? z.o.CreatedDate).FirstOrDefault();
                        string statusTxt = exhausted
                            ? "مستنفدة/مغلقة (رصيدها صفر)" : "مفتوحة (بها رصيد أو تحت تصريف)";
                        string lastUseTxt = lastOrder != null
                            ? $" | آخر استخدام: {lastOrder.o.DocumentNumber} بتاريخ {UiFormat.D(lastOrder.o.ProductionDate ?? lastOrder.o.CreatedDate)}" : "";
                        r.Rows.Add(new object[]
                        {
                            "⏳ 5. المتبقي الآن", lot.LotCode, "—", cust, rawName + " (خام)", "—", "—",
                            UiFormat.N(lot.InStockQtyKg), "—",
                            $"رصيد الدفعة الخام الحالي — المنتج منها: {UiFormat.N(lot.ProducedQtyKg)} | المسلَّم: {UiFormat.N(lot.DeliveredQtyKg)}{lastUseTxt} | الحالة: {statusTxt}"
                        });
                        r.RowLinks.Add(null);
                        if (finRemain > 0)
                        {
                            string finNames = string.Join(" + ", Db.StockBalances.AsNoTracking()
                                .Where(b => b.WarehouseId == whFg && b.LotId == lot.Id && b.ProductId != null)
                                .Select(b => b.ProductId.Value).Distinct().ToList().Select(pid => ProdName(pid)));
                            r.Rows.Add(new object[]
                            {
                                "🏬 5. متبقي تام", "مخزن التام", "—", cust, finNames, "—", "—",
                                UiFormat.N(finRemain), UiFormat.N0(finRemainCartons),
                                ReportUnits.TidyLine(
                                    ReportUnits.WarehouseStock(finRemain, finRemainCartons),
                                    "تام جاهز في المخزن باسم العميل لم يُسلَّم بعد")
                            });
                            r.RowLinks.Add(null);
                        }
                        gRawRemain += lot.InStockQtyKg;
                        gFinRemain += finRemain;
                    }
                }

                if (r.Rows.Count == 0)
                    r.Summary["تنبيه"] = "لا توجد شحنات مطابقة للفلاتر المحددة.";

                // ── 6) المقارنة النهائية: كم دخلت مقابل كم خرجت ──
                double yield_ = gConsumed > 0 ? gFinished / gConsumed * 100 : 0;
                r.Summary["كم دخلت (إجمالي الاستلام)"] = UiFormat.N(gIn) + " كجم";
                r.Summary["المصروف للإنتاج"] = UiFormat.N(gConsumed) + " كجم";
                r.Summary["المنتج التام"] = UiFormat.N(gFinished) + " كجم";
                r.Summary["المخرجات الثانوية (كجم)"] = UiFormat.N(gBy);
                r.Summary["المسلَّم للعملاء"] = UiFormat.N(gDelivered) + " كجم";
                r.Summary["المتبقي خاماً في المخازن"] = UiFormat.N(gRawRemain) + " كجم";
                r.Summary["المتبقي تاماً في المخازن"] = UiFormat.N(gFinRemain) + " كجم";
                r.Summary["نسبة المردود الصناعي (منتج ÷ مصروف)"] = gConsumed > 0 ? UiFormat.N(yield_, 1) + "%" : "—";
                r.Summary["المعادلة"] = $"دخلت {UiFormat.N(gIn)} = مصروف {UiFormat.N(gConsumed)} + متبقي خام {UiFormat.N(gRawRemain)} | والمصروف = تام {UiFormat.N(gFinished)} + ثانوية {UiFormat.N(gBy)}";
                r.Equation = r.Summary["المعادلة"];   // §الشكل الموحد: شريط المعادلة أسفل الجدول
                return r;
            }
            case "shipment_tracking":
                return ShipmentTracking(p, from, to);
            case "carton_statement":
                return CartonStatement(from, to);
        }
        return null;
    }

    /// <summary>§رابط التنقل من حركة المخزون إلى مستندها الأصلي.</summary>
    // ═══ §B10 routing ═══
    private ReportResult CartonStatementRouted(Dictionary<string, string> p)
    {
        DateTime? from = p != null && p.TryGetValue("from", out var f) && Core.Common.UiFormat.TryParseDate(f, out var fd) ? fd : null;
        DateTime? to = p != null && p.TryGetValue("to", out var t) && Core.Common.UiFormat.TryParseDate(t, out var td) ? td : null;
        return CartonStatement(from, to);
    }

    private DocLinkDto LinkForTxn(Core.Domain.Entities.InventoryTransaction t)
    {
        switch (t.ReferenceDocType)
        {
            case Core.Domain.Enums.ReferenceDocType.ShipmentReceipt:
            {
                var ship = Db.Shipments.AsNoTracking().FirstOrDefault(s => s.DocumentNumber == t.ReferenceDocNumber);
                return ship != null ? new DocLinkDto { DocType = "receiving", Id = ship.Id } : null;
            }
            case Core.Domain.Enums.ReferenceDocType.FinishedGoodsReceipt:
            {
                string docNo = (t.ReferenceDocNumber ?? "").Split('#')[0];
                var rcpt = Db.FinishedGoodsReceipts.AsNoTracking().FirstOrDefault(f => f.ReceiptNumber == docNo || f.DocumentNumber == docNo);
                return rcpt != null ? new DocLinkDto { DocType = "finishedgoods", Id = rcpt.Id } : null;
            }
            case Core.Domain.Enums.ReferenceDocType.CustomerDelivery:
            {
                var d = Db.CustomerDeliveries.AsNoTracking().FirstOrDefault(x => x.DocumentNumber == t.ReferenceDocNumber);
                return d != null ? new DocLinkDto { DocType = "delivery", Id = d.Id } : null;
            }
            case Core.Domain.Enums.ReferenceDocType.ProductionExecution:
            case Core.Domain.Enums.ReferenceDocType.MaterialIssue:
            {
                if (t.OrderId != null) return new DocLinkDto { DocType = "orders", Id = t.OrderId.Value };
                var o = Db.ProductionOrders.AsNoTracking().FirstOrDefault(x => x.DocumentNumber == t.ReferenceDocNumber);
                return o != null ? new DocLinkDto { DocType = "orders", Id = o.Id } : null;
            }
            default:
                return null;
        }
    }

    // ═══ §B10 كشف الكرتون الفارغ ومعادلة التسوية ═══
    private ReportResult CartonStatement(DateTime? from, DateTime? to)
    {
        var r = new ReportResult { TitleAr = "كشف الكرتون الفارغ (تولّد/بيع/عدّ)" };
        r.Columns.AddRange(new[] { "التاريخ", "نوع الحركة", "المستند", "المخزن", "وارد (كرتون)", "صادر/تسوية (كرتون)" });
        var q = Db.InventoryTransactions.AsNoTracking().Where(t =>
            t.ReferenceDocType == ReferenceDocType.CartonReturn || t.ReferenceDocType == ReferenceDocType.CartonSale || t.ReferenceDocType == ReferenceDocType.CartonCount);
        if (from != null) q = q.Where(t => t.TxnDate >= from);
        if (to != null) q = q.Where(t => t.TxnDate <= to.Value.AddDays(1));
        double inb = 0, outb = 0, adj = 0;
        foreach (var t in q.OrderBy(t => t.Id).ToList())
        {
            if (t.ReferenceDocType == ReferenceDocType.CartonReturn) inb += t.PackageCount;
            else if (t.ReferenceDocType == ReferenceDocType.CartonSale) outb += t.PackageCount;
            else adj += t.PackageCount;
            r.Rows.Add(new object[]
            {
                t.TxnDate.ToString("dd/MM/yyyy"),
                t.ReferenceDocType == ReferenceDocType.CartonReturn ? "تولّد من التفريغ" : t.ReferenceDocType == ReferenceDocType.CartonSale ? "بيع" : "عدّ/تسوية",
                t.ReferenceDocNumber,
                Db.Warehouses.AsNoTracking().Where(w => w.Id == t.WarehouseId).Select(w => w.WarehouseNameAr).FirstOrDefault() ?? "-",
                t.PackageCount > 0 ? t.PackageCount : 0,
                t.PackageCount < 0 ? t.PackageCount : 0
            });
        }
        var current = Db.StockBalances.AsNoTracking().Where(b => b.ProductId != null &&
            Db.Products.Any(p => p.Id == b.ProductId && p.GroupCode == "004") && b.LotId == null).Sum(b => b.PackageCount);
        r.Summary["إجمالي المتولّد (كرتون)"] = inb.ToString("N0");
        r.Summary["إجمالي المبيع (كرتون)"] = outb.ToString("N0");
        r.Summary["فروقات العدّ"] = adj.ToString("N0");
        r.Summary["الرصيد الحالي (كل المخازن)"] = current.ToString("N0");
        r.Summary["معادلة التسوية"] = $"{inb:N0} − {outb:N0} ± {adj:N0} = {inb - outb + adj:N0} (يطابق الحالي: {(inb - outb + adj == current ? "نعم ✔" : "لا ✗")})";
        return r;
    }

    // ═══ §تتبع الشحنة: دفتر احترافي وارد/منصرف/رصيد جارٍ لكل دفعات الشحنة ═══
    private ReportResult ShipmentTracking(Dictionary<string, string> p, DateTime? from, DateTime? to)
    {
        var r = new ReportResult { TitleAr = "تقرير تتبع الشحنة — دفتر الحركة والرصيد الجاري" };
        r.Columns.AddRange(new[] { "التاريخ", "رقم المستند", "نوع المستند", "البيان", "الوارد (كجم)", "المنصرف (كجم)", "الرصيد (كجم)" });
        int? shipId = p != null && p.TryGetValue("shipment", out var sv) && int.TryParse(sv, out var siv) ? siv : null;
        if (shipId == null)
        {
            r.Summary["تنبيه"] = "اختر الشحنة من فلتر «الشحنة» ثم اضغط تشغيل.";
            return r;
        }
        var ship = Db.Shipments.AsNoTracking().FirstOrDefault(x => x.Id == shipId);
        if (ship == null) { r.Summary["خطأ"] = "الشحنة غير موجودة."; return r; }
        var lotIds = Db.Lots.AsNoTracking().Where(l => l.ShipmentId == shipId).Select(l => l.Id).ToList();
        var cust = Db.Customers.AsNoTracking().Where(c => c.Id == ship.CustomerId).Select(c => c.CustomerName).FirstOrDefault() ?? "—";
        double running = 0, tIn = 0, tOut = 0;
        var txns = Db.InventoryTransactions.AsNoTracking().Where(t => t.LotId != null && lotIds.Contains(t.LotId.Value))
            .OrderBy(t => t.TxnDate).ThenBy(t => t.Id).ToList();
        if (from != null) txns = txns.Where(t => t.TxnDate >= from).ToList();
        if (to != null) txns = txns.Where(t => t.TxnDate <= to.Value.AddDays(1)).ToList();
        foreach (var t in txns)
        {
            var lotCode = Db.Lots.AsNoTracking().Where(l => l.Id == t.LotId).Select(l => l.LotCode).FirstOrDefault() ?? "—";
            string docType = t.ReferenceDocType switch
            {
                ReferenceDocType.ShipmentReceipt => "فاتورة استلام شحنة",
                ReferenceDocType.MaterialIssue => "أمر صرف مخزني للإنتاج",
                ReferenceDocType.ProductionExecution => "تنفيذ/إقفال إنتاج",
                ReferenceDocType.Adjustment => "تسوية جرد",
                ReferenceDocType.Return => "مردود",
                _ => "حركة مخزنية"
            };
            bool inbound = t.QtyKg >= 0;
            running += t.QtyKg;
            if (inbound) tIn += t.QtyKg; else tOut += -t.QtyKg;
            r.Rows.Add(new object[]
            {
                t.TxnDate.ToString("dd/MM/yyyy"),
                t.ReferenceDocNumber ?? "—",
                docType,
                $"{(inbound ? "لكم وارد" : "عليكم منصرف")} — دفعة {lotCode} | {t.Notes ?? ""}",
                inbound ? t.QtyKg : 0,
                inbound ? 0 : -t.QtyKg,
                running
            });
        }
        r.Summary["الشحنة"] = $"{ship.DocumentNumber} — العميل: {cust}";
        r.Summary["إجمالي الوارد (كجم)"] = tIn.ToString("N1");
        r.Summary["إجمالي المنصرف (كجم)"] = tOut.ToString("N1");
        r.Summary["الرصيد الجاري (كجم)"] = running.ToString("N1");
        r.Summary["المعادلة"] = $"وارد {tIn:N1} − منصرف {tOut:N1} = رصيد {running:N1}";
        return r;
    }
}

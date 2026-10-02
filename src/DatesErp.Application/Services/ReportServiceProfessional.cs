using DatesErp.Core.Common;
using DatesErp.Core.Interfaces.Services;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §العملية الشاملة لتطوير التقارير — الحزمة الاحترافية:
/// أرصدة الخام بالدفعات، مخزون التام (كرتون+كجم)، استهلاك المواد المساعدة،
/// المخرجات الثانوية، استغلال الطاقة، الخطة مقابل الفعلي، تنفيذ الأوامر.
/// كل تقرير: مرجع كامل + مؤشرات Summary + روابط فتح المستندات + أعمدة رقمية للإجماليات.
/// </summary>
public partial class ReportService
{
    private List<ReportDefinition> GetProfessionalDefinitions()
    {
        var customers = Db.Customers.AsNoTracking().OrderBy(c => c.CustomerName)
            .Select(c => new { c.Id, c.CustomerName }).ToList().Select(x => (x.Id.ToString(), x.CustomerName)).ToList();
        var products = Db.Products.AsNoTracking().OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.ProductNameAr }).ToList().Select(x => (x.Id.ToString(), x.ProductNameAr)).ToList();

        ReportParameter PDate(string key, string label) => new() { Key = key, LabelAr = label, Kind = "date" };

        return new List<ReportDefinition>
        {
            new()
            {
                Code = "raw_inventory",
                TitleAr = "كشف أرصدة المواد الخام بالدفعات (مستلم/محجوز/متاح/مسلَّم/هالك)",
                Category = "المخزون",
                Parameters = new() { new() { Key = "customer", LabelAr = "العميل", Kind = "list", Options = customers },
                                   new() { Key = "product", LabelAr = "الصنف الخام", Kind = "list", Options = products } }
            },
            new()
            {
                Code = "finished_inventory",
                TitleAr = "كشف مخزون الإنتاج التام (صنف × عبوة: كرتون + كجم)",
                Category = "المخزون",
                Parameters = new() { new() { Key = "product", LabelAr = "المنتج التام", Kind = "list", Options = products } }
            },
            new()
            {
                Code = "aux_consumption",
                TitleAr = "استهلاك المواد المساعدة لكل أمر (محتسب/مصروف/مستهلك/مرتجع/فاقد)",
                Category = "المخزون",
                Parameters = new() { PDate("from", "من تاريخ"), PDate("to", "إلى تاريخ") }
            },
            new()
            {
                Code = "secondary_outputs",
                TitleAr = "المخرجات الثانوية والفاقد (حسب تعريف الأصناف) باليوم",
                Category = "الجودة",
                Parameters = new() { PDate("from", "من تاريخ"), PDate("to", "إلى تاريخ") }
            },
            new()
            {
                Code = "capacity_utilization",
                TitleAr = "الحمل واستغلال الطاقة لكل أمر (كراتين/ساعات مطلوبة/ساعات متاحة/٪ استغلال)",
                Category = "الإنتاج",
                Parameters = new() { PDate("from", "من تاريخ"), PDate("to", "إلى تاريخ") }
            },
            new()
            {
                Code = "plan_vs_actual",
                TitleAr = "الخطة مقابل الفعلي (مخطط/منتج/مقبول/مسلَّم ونسبة الإنجاز)",
                Category = "الإنتاج",
                Parameters = new() { PDate("from", "من تاريخ"), PDate("to", "إلى تاريخ"),
                                     new() { Key = "customer", LabelAr = "العميل", Kind = "list", Options = customers } }
            },
            new()
            {
                Code = "order_execution",
                TitleAr = "تقرير تنفيذ أوامر الإنتاج (مخطط/منفذ/متبقٍ/بداية فعلية/حالة)",
                Category = "الإنتاج",
                Parameters = new() { PDate("from", "من تاريخ"), PDate("to", "إلى تاريخ") }
            }
        };
    }

    /// <summary>توجيه التقارير الاحترافية — ترجع null إن لم يكن الكود منها.</summary>
    private ReportResult RunProfessional(string code, Dictionary<string, string> parameters,
        DateTime? from, DateTime? to, int? custId, int? prodId)
    {
        return code switch
        {
            "raw_inventory" => RawInventory(custId, prodId),
            "finished_inventory" => FinishedInventory(prodId),
            "aux_consumption" => AuxConsumption(from, to),
            "secondary_outputs" => SecondaryOutputs(from, to),
            "capacity_utilization" => CapacityUtilization(from, to),
            "plan_vs_actual" => PlanVsActual(from, to, custId),
            "order_execution" => OrderExecution(from, to),
            _ => null
        };
    }

    // ═══════════════════════════ 1) أرصدة الخام بالدفعات ═══════════════════════════

    private ReportResult RawInventory(int? custId, int? prodId)
    {
        var r = new ReportResult { TitleAr = "كشف أرصدة المواد الخام بالدفعات", RowLinks = new System.Collections.Generic.List<DatesErp.Core.Interfaces.Services.DocLinkDto>() };
        r.Columns.AddRange(new[] { "الدفعة", "الصنف الخام", "العميل", "تاريخ الدفعة", "المستلم (كجم)", "المخزون (كجم)", "محجوز (كجم)", "تحت المعالجة (كجم)", "جاهز للإنتاج (كجم)", "المتاح (كجم)", "مسلَّم (كجم)", "الفاقد (كجم)", "الحالة" });
        var q = Db.Lots.AsNoTracking().AsQueryable();
        if (custId != null) q = q.Where(l => l.CustomerId == custId);
        if (prodId != null) q = q.Where(l => l.ProductId == prodId);
        var lots = q.OrderByDescending(l => l.Id).ToList();
        foreach (var l in lots)
        {
            r.Rows.Add(new object[]
            {
                l.LotCode,
                Db.Products.AsNoTracking().Where(p => p.Id == l.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-",
                l.CustomerId != null ? Db.Customers.AsNoTracking().Where(c => c.Id == l.CustomerId).Select(c => c.CustomerName).FirstOrDefault() ?? "-" : "-",
                l.LotDate?.ToString("dd/MM/yyyy") ?? "-",
                l.InitialQtyKg, l.InStockQtyKg, l.ReservedQtyKg,
                // §المعالجة والتعقيم: العمودان يفسّران نقص «المتاح» — بدونهما يبدو
                // الرقم خطأً في النظام لا نتيجة معالجة جارية.
                l.UnderTreatmentQtyKg, l.TreatmentReadyQtyKg,
                l.AvailableQtyKg, l.DeliveredQtyKg, l.WastageQtyKg,
                l.InStockQtyKg <= 0.001 ? "منتهية ⚪"
                    : l.UnderTreatmentQtyKg > 0.001 ? "تحت المعالجة 🟠"
                    : l.ReservedQtyKg > 0 ? "محجوزة جزئياً 🟠" : "متاحة 🟢"
            });
            r.RowLinks.Add(new DocLinkDto { DocType = "receiving", Id = l.ShipmentId ?? 0 });
        }
        r.Summary["عدد الدفعات"] = lots.Count.ToString();
        r.Summary["إجمالي المستلم (كجم)"] = lots.Sum(l => l.InitialQtyKg).ToString("N1");
        r.Summary["إجمالي المخزون (كجم)"] = lots.Sum(l => l.InStockQtyKg).ToString("N1");
        r.Summary["إجمالي تحت المعالجة (كجم)"] = lots.Sum(l => l.UnderTreatmentQtyKg).ToString("N1");
        r.Summary["إجمالي الجاهز للإنتاج (كجم)"] = lots.Sum(l => l.TreatmentReadyQtyKg).ToString("N1");
        r.Summary["إجمالي المتاح (كجم)"] = lots.Sum(l => l.AvailableQtyKg).ToString("N1");
        return r;
    }

    // ═══════════════════════════ 2) مخزون التام (كرتون + كجم) ═══════════════════════════

    private ReportResult FinishedInventory(int? prodId)
    {
        var r = new ReportResult { TitleAr = "كشف مخزون الإنتاج التام (كرتون + كجم)" };
        r.Columns.AddRange(new[] { "المنتج التام", "العبوة", "المخزن", "العميل", "الكراتين", "الوزن (كجم)" });
        var q = Db.StockBalances.AsNoTracking().Where(b => b.ProductId != null && (b.QtyKg != 0 || b.PackageCount != 0));
        if (prodId != null) q = q.Where(b => b.ProductId == prodId);
        var rows = q.ToList();
        foreach (var b in rows)
        {
            r.Rows.Add(new object[]
            {
                Db.Products.AsNoTracking().Where(p => p.Id == b.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-",
                b.PackagingTypeId != null ? Db.PackagingTypes.AsNoTracking().Where(p => p.Id == b.PackagingTypeId).Select(p => p.PackageNameAr).FirstOrDefault() ?? "-" : "عام",
                Db.Warehouses.AsNoTracking().Where(w => w.Id == b.WarehouseId).Select(w => w.WarehouseNameAr).FirstOrDefault() ?? "-",
                b.CustomerId != null ? Db.Customers.AsNoTracking().Where(c => c.Id == b.CustomerId).Select(c => c.CustomerName).FirstOrDefault() ?? "-" : "-",
                b.PackageCount, b.QtyKg
            });
        }
        r.Summary["إجمالي الكراتين"] = rows.Sum(b => b.PackageCount).ToString("N0");
        r.Summary["إجمالي الوزن (كجم)"] = rows.Sum(b => b.QtyKg).ToString("N1");
        r.Summary["عدد الأصناف المخزنة"] = rows.Select(b => b.ProductId).Distinct().Count().ToString();
        return r;
    }

    // ═══════════════════════════ 3) استهلاك المواد المساعدة ═══════════════════════════

    private ReportResult AuxConsumption(DateTime? from, DateTime? to)
    {
        var r = new ReportResult { TitleAr = "استهلاك المواد المساعدة لكل أمر إنتاج", RowLinks = new System.Collections.Generic.List<DatesErp.Core.Interfaces.Services.DocLinkDto>() };
        r.Columns.AddRange(new[] { "الأمر", "تاريخ الأمر", "المادة المساعدة", "الوحدة", "المحتسب", "المصروف", "المستهلك", "المرتجع", "الفاقد", "الحالة" });
        var oq = Db.ProductionOrders.AsNoTracking().AsQueryable();
        if (from != null) oq = oq.Where(o => o.ProductionDate >= from);
        if (to != null) oq = oq.Where(o => o.ProductionDate <= to.Value.AddDays(1));
        var orderIds = oq.Select(o => o.Id).ToList();
        var mats = Db.ProductionOrderMaterials.AsNoTracking().Where(m => orderIds.Contains(m.OrderId)).ToList();
        foreach (var m in mats)
        {
            var o = Db.ProductionOrders.AsNoTracking().Where(x => x.Id == m.OrderId).Select(x => new { x.DocumentNumber, x.ProductionDate }).FirstOrDefault();
            r.Rows.Add(new object[]
            {
                o?.DocumentNumber ?? "-", o?.ProductionDate?.ToString("dd/MM/yyyy") ?? "-",
                Db.AuxiliaryMaterials.AsNoTracking().Where(a => a.Id == m.MaterialId).Select(a => a.MaterialNameAr).FirstOrDefault() ?? "-",
                m.UnitOfMeasure ?? "-",
                m.CalculatedQty, m.ActualIssuedQty, m.ConsumedQty, m.ReturnedQty, m.WastedQty,
                DocStatuses.ToArabic(m.Status)
            });
            r.RowLinks.Add(new DocLinkDto { DocType = "orders", Id = m.OrderId });
        }
        r.Summary["إجمالي المحتسب"] = mats.Sum(m => m.CalculatedQty).ToString("N1");
        r.Summary["إجمالي المصروف"] = mats.Sum(m => m.ActualIssuedQty).ToString("N1");
        r.Summary["إجمالي المستهلك"] = mats.Sum(m => m.ConsumedQty).ToString("N1");
        r.Summary["إجمالي الفاقد"] = mats.Sum(m => m.WastedQty).ToString("N1");
        return r;
    }

    // ═══════════════════════════ 4) المخرجات الثانوية ═══════════════════════════

    private ReportResult SecondaryOutputs(DateTime? from, DateTime? to)
    {
        var r = new ReportResult { TitleAr = "المخرجات الثانوية والفاقد", RowLinks = new System.Collections.Generic.List<DatesErp.Core.Interfaces.Services.DocLinkDto>() };
        // §لا أسماء مخرجات مثبّتة: عمود لكل مخرج ثانوي معرَّف في إعدادات الأصناف
        var bpDefs = Db.ByProducts.AsNoTracking().Where(b => b.IsActive).OrderBy(b => b.Id).ToList();
        r.Columns.AddRange(new[] { "الأمر", "تاريخ الإقفال", "الخام المستهلك (كجم)" });
        foreach (var b in bpDefs) r.Columns.Add($"{b.ByProductNameAr} ({b.UnitOfMeasure})");
        if (bpDefs.Count == 0) r.Columns.Add("المخرجات الثانوية (كجم)");
        r.Columns.AddRange(new[] { "الفاقد (كجم)", "متبقي الصالة (كجم)", "نسبة المخرجات ٪" });

        var q = Db.ProductionExecutions.AsNoTracking().AsQueryable();
        if (from != null) q = q.Where(x => x.StartDateTime >= from);
        if (to != null) q = q.Where(x => x.StartDateTime <= to.Value.AddDays(1));
        var execs = q.OrderByDescending(x => x.Id).ToList();
        var totals = bpDefs.ToDictionary(b => b.Id, _ => 0.0);
        double legacyTotal = 0, wasteTotal = 0;

        foreach (var x in execs)
        {
            // §القيم من السجلات الديناميكية، ومع رجوع للأعمدة القديمة للبيانات السابقة للديناميكية
            var byQty = bpDefs.ToDictionary(b => b.Id, _ => 0.0);
            foreach (var b in bpDefs)
            {
                string n = b.ByProductNameAr ?? "";
                double v = 0;
                if (n.Contains("حشف")) v = x.HashfKg;
                else if (n.Contains("نوى")) v = x.NawaKg;
                byQty[b.Id] = v; totals[b.Id] += v;
            }
            legacyTotal += x.HashfKg + x.NawaKg;
            wasteTotal += x.WastageQtyKg;

            double totalOut = x.HashfKg + x.NawaKg + x.WastageQtyKg;
            double pct = x.ConsumedRawKg > 0 ? totalOut / x.ConsumedRawKg * 100 : 0;
            var cells = new System.Collections.Generic.List<object>
            {
                Db.ProductionOrders.AsNoTracking().Where(o => o.Id == x.OrderId).Select(o => o.DocumentNumber).FirstOrDefault() ?? "-",
                x.StartDateTime?.ToString("dd/MM/yyyy") ?? "-",
                x.ConsumedRawKg
            };
            if (bpDefs.Count > 0) foreach (var b in bpDefs) cells.Add(byQty[b.Id]);
            else cells.Add(x.HashfKg + x.NawaKg);
            cells.Add(x.WastageQtyKg);
            cells.Add(x.RemainingInHallKg);
            cells.Add(pct);
            r.Rows.Add(cells.ToArray());
            r.RowLinks.Add(new DocLinkDto { DocType = "orders", Id = x.OrderId });
        }
        if (bpDefs.Count > 0)
            foreach (var b in bpDefs) r.Summary[$"إجمالي {b.ByProductNameAr} ({b.UnitOfMeasure})"] = totals[b.Id].ToString("N1");
        else
            r.Summary["إجمالي المخرجات الثانوية (كجم)"] = legacyTotal.ToString("N1");
        r.Summary["إجمالي الفاقد (كجم)"] = wasteTotal.ToString("N1");
        return r;
    }

    // ═══════════════════════════ 5) استغلال الطاقة ═══════════════════════════

    private double RateForLocal(int productId, int shiftId, int? packId)
    {
        return CapacityPolicy.RateFor(Db, productId, shiftId, packId);
    }

    private ReportResult CapacityUtilization(DateTime? from, DateTime? to)
    {
        var r = new ReportResult { TitleAr = "الحمل واستغلال الطاقة لكل أمر إنتاج", RowLinks = new System.Collections.Generic.List<DatesErp.Core.Interfaces.Services.DocLinkDto>() };
        r.Columns.AddRange(new[] { "الأمر", "التاريخ", "الوردية", "خط الإنتاج", "كراتين الأمر", "ساعات مطلوبة", "ساعات متاحة", "استغلال ٪", "الحالة" });
        var oq = Db.ProductionOrders.AsNoTracking().Where(o => o.Status != DocStatuses.Cancelled);
        if (from != null) oq = oq.Where(o => o.ProductionDate >= from);
        if (to != null) oq = oq.Where(o => o.ProductionDate <= to.Value.AddDays(1));
        var orders = oq.OrderByDescending(o => o.Id).ToList();
        double totReq = 0, totAvail = 0;
        foreach (var o in orders)
        {
            var items = Db.ProductionOrderItems.AsNoTracking().Where(i => i.OrderId == o.Id).ToList();
            double req = 0;
            foreach (var it in items.Where(i => i.PlannedCartons > 0))
            {
                var rate = RateForLocal(it.ProductId, o.ShiftId ?? 1, it.PackagingTypeId);
                req += it.PlannedCartons / (rate > 0 ? rate : 500);
            }
            var shift = o.ShiftId != null ? Db.Shifts.AsNoTracking().FirstOrDefault(s => s.Id == o.ShiftId) : null;
            double avail = CapacityPolicy.EffectiveHours(shift?.EffectiveProductiveHours ?? 0, shift?.TotalHours ?? 0);
            double pct = avail > 0 ? req / avail * 100 : 0;
            totReq += req; totAvail += avail;
            r.Rows.Add(new object[]
            {
                o.DocumentNumber, o.ProductionDate?.ToString("dd/MM/yyyy") ?? "-",
                shift?.ShiftNameAr ?? "-",
                o.LineId != null ? Db.ProductionLines.AsNoTracking().Where(l => l.Id == o.LineId).Select(l => l.LineNameAr).FirstOrDefault() ?? "-" : "-",
                items.Sum(i => i.PlannedCartons), req, avail, pct,
                pct > 100 ? "تجاوز طاقة ⛔" : pct > 80 ? "قرب الامتلاء 🟠" : "ضمن الطاقة 🟢"
            });
            r.RowLinks.Add(new DocLinkDto { DocType = "orders", Id = o.Id });
        }
        r.Summary["إجمالي الساعات المطلوبة"] = totReq.ToString("N1");
        r.Summary["إجمالي الساعات المتاحة"] = totAvail.ToString("N1");
        r.Summary["متوسط الاستغلال ٪"] = (totAvail > 0 ? totReq / totAvail * 100 : 0).ToString("N1");
        return r;
    }

    // ═══════════════════════════ 6) الخطة مقابل الفعلي ═══════════════════════════

    private ReportResult PlanVsActual(DateTime? from, DateTime? to, int? custId)
    {
        var r = new ReportResult { TitleAr = "الخطة مقابل الفعلي ونسب الإنجاز", RowLinks = new System.Collections.Generic.List<DatesErp.Core.Interfaces.Services.DocLinkDto>() };
        r.Columns.AddRange(new[] { "الخطة", "الفترة", "العملاء", "المخطط (كجم)", "المخطط (كرتون)", "المنتج (كجم)", "المقبول (كجم)", "المسلَّم (كجم)", "إنجاز ٪", "الحالة" });
        var pq = Db.ProductionPlans.AsNoTracking().AsQueryable();
        if (from != null) pq = pq.Where(p => p.StartDate >= from);
        if (to != null) pq = pq.Where(p => p.StartDate <= to.Value.AddDays(1));
        var plans = pq.OrderByDescending(p => p.Id).ToList();
        foreach (var p in plans)
        {
            var items = Db.ProductionPlanItems.AsNoTracking().Where(i => i.PlanId == p.Id).ToList();
            if (custId != null && !items.Any(i => i.CustomerId == custId)) continue;
            double planned = items.Sum(i => i.PlannedQtyKg);
            double produced = items.Sum(i => i.ProducedQtyKg);
            double accepted = items.Sum(i => i.AcceptedQtyKg);
            double delivered = items.Sum(i => i.DeliveredQtyKg);
            double pct = planned > 0 ? produced / planned * 100 : 0;
            r.Rows.Add(new object[]
            {
                p.DocumentNumber,
                $"{p.StartDate:dd/MM/yyyy} ← {p.EndDate:dd/MM/yyyy}",
                items.Where(i => i.CustomerId != null).Select(i => i.CustomerId).Distinct().Count(),
                planned, items.Sum(i => i.PlannedCartons), produced, accepted, delivered, pct,
                pct >= 99.9 ? "مكتملة ✅" : produced > 0 ? "جارية 🟠" : "لم تبدأ ⏳"
            });
            r.RowLinks.Add(new DocLinkDto { DocType = "planning", Id = p.Id });
        }
        var allRows = r.Rows;
        r.Summary["عدد الخطط"] = allRows.Count.ToString();
        r.Summary["إجمالي المخطط (كجم)"] = allRows.Sum(x => Convert.ToDouble(x[3])).ToString("N1");
        r.Summary["إجمالي المنتج (كجم)"] = allRows.Sum(x => Convert.ToDouble(x[5])).ToString("N1");
        r.Summary["إنجاز إجمالي ٪"] = (allRows.Sum(x => Convert.ToDouble(x[3])) > 0
            ? allRows.Sum(x => Convert.ToDouble(x[5])) / allRows.Sum(x => Convert.ToDouble(x[3])) * 100 : 0).ToString("N1");
        return r;
    }

    // ═══════════════════════════ 7) تنفيذ الأوامر ═══════════════════════════

    private ReportResult OrderExecution(DateTime? from, DateTime? to)
    {
        var r = new ReportResult { TitleAr = "تقرير تنفيذ أوامر الإنتاج", RowLinks = new System.Collections.Generic.List<DatesErp.Core.Interfaces.Services.DocLinkDto>() };
        r.Columns.AddRange(new[] { "الأمر", "التاريخ", "الوردية", "الحالة", "المخطط (كجم)", "المخطط (كرتون)", "المنفذ (كجم)", "المنفذ (كرتون)", "المتبقي (كجم)", "البداية الفعلية", "تقدم ٪" });
        var oq = Db.ProductionOrders.AsNoTracking().AsQueryable();
        if (from != null) oq = oq.Where(o => o.ProductionDate >= from);
        if (to != null) oq = oq.Where(o => o.ProductionDate <= to.Value.AddDays(1));
        var orders = oq.OrderByDescending(o => o.Id).ToList();
        foreach (var o in orders)
        {
            var items = Db.ProductionOrderItems.AsNoTracking().Where(i => i.OrderId == o.Id).ToList();
            var execs = Db.ProductionExecutions.AsNoTracking().Where(x => x.OrderId == o.Id).ToList();
            double planned = items.Sum(i => i.PlannedQtyKg);
            double produced = execs.Sum(x => x.ActualQtyKg);
            double pct = planned > 0 ? Math.Min(100, produced / planned * 100) : 0;
            r.Rows.Add(new object[]
            {
                o.DocumentNumber, o.ProductionDate?.ToString("dd/MM/yyyy") ?? "-",
                o.ShiftId != null ? Db.Shifts.AsNoTracking().Where(s => s.Id == o.ShiftId).Select(s => s.ShiftNameAr).FirstOrDefault() ?? "-" : "-",
                DocStatuses.ToArabic(o.IsClosed ? DocStatuses.Closed : o.Status),
                planned, items.Sum(i => i.PlannedCartons), produced, execs.Sum(x => x.ActualCartons),
                Math.Max(0, planned - produced),
                execs.Where(x => x.StartDateTime != null).OrderBy(x => x.StartDateTime).Select(x => x.StartDateTime).FirstOrDefault()?.ToString("dd/MM/yyyy HH:mm") ?? "-",
                pct
            });
            r.RowLinks.Add(new DocLinkDto { DocType = "orders", Id = o.Id });
        }
        r.Summary["عدد الأوامر"] = orders.Count.ToString();
        r.Summary["إجمالي المخطط (كجم)"] = r.Rows.Sum(x => Convert.ToDouble(x[4])).ToString("N1");
        r.Summary["إجمالي المنفذ (كجم)"] = r.Rows.Sum(x => Convert.ToDouble(x[6])).ToString("N1");
        return r;
    }
}

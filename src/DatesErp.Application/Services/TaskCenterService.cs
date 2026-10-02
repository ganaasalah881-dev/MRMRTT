using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §B97 — مركز المهام: الموظف لا يبحث عن عمله، النظام يعرض له عمله.
/// المهمة «مشتقة»: ليست سجلاً جديداً بل عرض لحظي على مستند قائم بحالة قابلة للفعل —
/// لا إنشاء، لا تعديل، لا تكرار بيانات، لا التزامن بين المهمة ومستندها.
/// </summary>
public class TaskCenterService : ServiceBase, ITaskCenterService
{
    private readonly ICustomerAvailabilityService _availability;

    public TaskCenterService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering, ICustomerAvailabilityService availability)
        : base(db, session, numbering)
    {
        _availability = availability;
    }

    public TaskBoardDto GetBoard()
    {
        var board = new TaskBoardDto { RoleAr = RoleAr() };
        var today = Db.BusinessNow.Date;
        var uid = Session?.UserId;

        bool canApprovePlan = Session?.Can("planning", "Approve") == true;
        bool canEditPlan = Session?.Can("planning", "Edit") == true;
        bool isProd = Session?.IsInRole("Production") == true
                      || Session?.Can("planning", "Post") == true
                      || Session?.Can("production", "Edit") == true;
        bool isQc = Session?.Can("quality", "Edit") == true;
        bool isWh = Session?.Can("finishedgoods", "Create") == true;
        bool isSales = Session?.Can("delivery", "Create") == true;

        var names = Db.Users.AsNoTracking().ToDictionary(u => u.Id, u => u.FullName);
        string Name(int? id) => id != null && names.TryGetValue(id.Value, out var n) ? n : "—";

        // ═══ أ) صانع الخطط (المدير التنفيذي) ═══
        if (canEditPlan)
        {
            // أعادت لها الإدارة — بالسبب — أولوية قصوى
            foreach (var p in Db.ProductionPlans.AsNoTracking()
                         .Where(p => p.CreatedBy == uid && p.Status == DocStatuses.Draft && p.StatusReason != null)
                         .OrderByDescending(p => p.Id).ToList())
                board.Action.Add(new TaskCardDto
                {
                    Icon = "↩️",
                    Title = $"خطة أُعيدت للتعديل: {p.PlanTitle}",
                    Subtitle = $"رقم: {p.DocumentNumber} | الفترة: {Period(p)}",
                    Sender = "أعادها المعتمد",
                    Reason = p.StatusReason,
                    Due = "الآن",
                    DocType = "Plan", DocId = p.Id, DocNumber = p.DocumentNumber,
                    Priority = 0
                });

            // مسوداتي الجاهزة/القابلة للإرسال
            foreach (var p in Db.ProductionPlans.AsNoTracking()
                         .Where(p => p.CreatedBy == uid && p.Status == DocStatuses.Draft && p.StatusReason == null && !p.IsClosed)
                         .OrderByDescending(p => p.Id).ToList())
                board.Action.Add(new TaskCardDto
                {
                    Icon = "📝",
                    Title = $"خطة مسودة — أكملها وأرسلها للاعتماد: {p.PlanTitle}",
                    Subtitle = $"رقم: {p.DocumentNumber} | الفترة: {Period(p)} | البنود: {Db.ProductionPlanItems.Count(i => i.PlanId == p.Id)}",
                    Due = "عند الإكمال",
                    DocType = "Plan", DocId = p.Id, DocNumber = p.DocumentNumber,
                    Priority = 1
                });

            // مرشحة للاعتماد (بانتظار الإدارة)
            foreach (var p in Db.ProductionPlans.AsNoTracking()
                         .Where(p => p.CreatedBy == uid && (p.Status == "UnderApproval" || p.Status == DocStatuses.Submitted))
                         .OrderBy(p => p.Id).ToList())
                board.InFlight.Add(new TaskCardDto
                {
                    Icon = "⏳",
                    Title = $"مرسَلة للاعتماد: {p.PlanTitle}",
                    Subtitle = $"رقم: {p.DocumentNumber} | الفترة: {Period(p)}",
                    DocType = "Plan", DocId = p.Id, DocNumber = p.DocumentNumber,
                    Priority = 1
                });
        }

        // ═══ ب) المعتمد (المدير العام) ═══
        if (canApprovePlan)
        {
            foreach (var p in Db.ProductionPlans.AsNoTracking()
                         .Where(p => p.Status == "UnderApproval" || p.Status == DocStatuses.Submitted)
                         .OrderBy(p => p.Id).ToList())
            {
                var items = Db.ProductionPlanItems.AsNoTracking().Where(i => i.PlanId == p.Id).ToList();
                board.Action.Add(new TaskCardDto
                {
                    Icon = "🔔",
                    Title = $"خطة إنتاج بانتظار اعتمادك: {p.PlanTitle}",
                    Subtitle = $"رقم: {p.DocumentNumber} | الفترة: {Period(p)} | العملاء: {items.Select(i => i.CustomerId).Distinct().Count()} | الأصناف: {items.Count} | الإجمالي: {items.Sum(i => i.PlannedQtyKg):N1} كجم",
                    Sender = "أنشأها: " + Name(p.CreatedBy),
                    Due = p.StartDate != null ? p.StartDate.Value.ToString("dd/MM/yyyy") : null,
                    Overdue = p.StartDate != null && p.StartDate.Value < today,
                    DocType = "Plan", DocId = p.Id, DocNumber = p.DocumentNumber,
                    Priority = p.StartDate != null && p.StartDate.Value < today ? 0 : 1
                });
            }

            // خطط اعتمدتها اليوم
            foreach (var p in Db.ProductionPlans.AsNoTracking()
                         .Where(p => p.IsApproved && p.ApprovedBy == uid && p.ApprovedDate != null && p.ApprovedDate.Value.Date == today)
                         .OrderByDescending(p => p.Id).ToList())
                board.DoneToday.Add(new TaskCardDto
                {
                    Icon = "✅",
                    Title = $"خطة اعتمدت اليوم: {p.PlanTitle}",
                    Subtitle = $"رقم: {p.DocumentNumber} | الفترة: {Period(p)}",
                    DocType = "Plan", DocId = p.Id, DocNumber = p.DocumentNumber,
                    Priority = 1
                });

            // أوامر متوقفة (رؤية إدارية)
            foreach (var o in Db.ProductionOrders.AsNoTracking()
                         .Where(o => o.Status == DocStatuses.Stopped && !o.IsClosed)
                         .OrderByDescending(o => o.Id).ToList())
                board.InFlight.Add(new TaskCardDto
                {
                    Icon = "⛔",
                    Title = $"أمر إنتاج متوقف: {o.DocumentNumber}",
                    Subtitle = OrderLine(o) + (o.ProductionDate != null ? $" | توقف منذ {o.ModifiedDate?.ToString("dd/MM/yyyy")}" : ""),
                    Reason = o.StatusReason,
                    DocType = "Order", DocId = o.Id, DocNumber = o.DocumentNumber,
                    Priority = 1
                });

            AddManagementAlerts(board, today);
        }

        // ═══ ج) مدير الإنتاج ═══
        if (isProd)
        {
            // المطلوب تشغيله اليوم (+ المتأخر من أيام سابقة) — بنود بلا أوامر صادرة
            var dueIds = Db.ProductionPlanItems.AsNoTracking()
                .Where(i => i.ScheduledDate != null && i.ScheduledDate <= today
                            && i.ExecutionStatus != "Completed")
                .Select(i => i.Id).ToList();
            var approvedPlanIds = Db.ProductionPlans.AsNoTracking()
                .Where(p => p.IsApproved && !p.IsClosed).Select(p => p.Id).ToList();
            var dueFull = Db.ProductionPlanItems.AsNoTracking()
                .Where(i => dueIds.Contains(i.Id) && approvedPlanIds.Contains(i.PlanId))
                .ToList();
            var plansById = Db.ProductionPlans.AsNoTracking()
                .Where(p => approvedPlanIds.Contains(p.Id)).ToDictionary(p => p.Id);
            var orderedItemIds = Db.ProductionOrderItems.AsNoTracking()
                .Where(oi => oi.PlanItemId != null
                             && Db.ProductionOrders.Any(o => o.Id == oi.OrderId && o.Status != DocStatuses.Cancelled))
                .Select(oi => oi.PlanItemId).Distinct().ToList();
            var toIssue = dueFull.Where(i => !orderedItemIds.Contains(i.Id)).ToList();

            foreach (var g in toIssue.GroupBy(i => (i.PlanId, Day: i.ScheduledDate.Value.Date))
                         .OrderBy(g => g.Key.Day))
            {
                var plan = plansById[g.Key.PlanId];
                bool overdue = g.Key.Day < today;
                board.Action.Add(new TaskCardDto
                {
                    Icon = overdue ? "⏰" : "⚡",
                    Title = overdue ? $"خطة متأخرة: {g.Key.Day:dd/MM} — تحتاج مراجعة التخطيط" : $"المطلوب تشغيله اليوم: {g.Key.Day:dd/MM}",
                    Subtitle = $"خطة {plan.DocumentNumber} | العملاء: {g.Select(i => i.CustomerId).Distinct().Count()} | الأصناف: {g.Count()} | {g.Sum(i => i.PlannedQtyKg):N1} كجم",
                    Due = g.Key.Day.ToString("dd/MM/yyyy"),
                    Overdue = overdue,
                    DocType = "Plan", DocId = g.Key.PlanId, DocNumber = plan.DocumentNumber,
                    // Today uses the existing read-only orders screen; overdue work requires formal replanning.
                    Action = overdue ? "ReviewPlan" : "TodayOrders",
                    Priority = overdue ? 0 : 1
                });
            }

            // أوامر جاهزة للبدء
            foreach (var o in Db.ProductionOrders.AsNoTracking()
                         .Where(o => o.IsApproved && o.Status == DocStatuses.Scheduled && !o.IsClosed
                                     && o.ProductionDate != null && o.ProductionDate <= today)
                         .OrderBy(o => o.Id).ToList())
                board.Action.Add(new TaskCardDto
                {
                    Icon = "🚀",
                    Title = $"أمر تشغيل جاهز للبدء: {o.DocumentNumber}",
                    Subtitle = OrderLine(o) + " | " + (o.ProductionDate < today ? "⏰ تأخر عن موعده" : "تاريخ اليوم"),
                    Due = o.ProductionDate.Value.ToString("dd/MM/yyyy"),
                    Overdue = o.ProductionDate < today,
                    DocType = "Order", DocId = o.Id, DocNumber = o.DocumentNumber,
                    Priority = o.ProductionDate < today ? 0 : 1
                });

            // جلسات جارية
            foreach (var e in Db.ProductionExecutions.AsNoTracking()
                         .Where(e => e.Status == DocStatuses.InProgress)
                         .OrderByDescending(e => e.Id).ToList())
            {
                var order = Db.ProductionOrders.AsNoTracking().FirstOrDefault(o => o.Id == e.OrderId);
                board.InFlight.Add(new TaskCardDto
                {
                    Icon = "🏭",
                    Title = $"جلسة تشغيل جارية: {order?.DocumentNumber ?? e.DocumentNumber}",
                    Subtitle = order != null ? OrderLine(order) : null,
                    DocType = "Order", DocId = e.OrderId, DocNumber = order?.DocumentNumber ?? e.DocumentNumber,
                    Priority = 1
                });
            }

            // فحوصات معتمدة (مطابقة) لم يُستلم مقبولها بعد — مصدر تسليم متاح
            var passedQcs = Db.QualityChecks.AsNoTracking()
                .Where(c => c.Status == DocStatuses.Approved && c.Decision == "Passed" && c.OrderId != null)
                .OrderByDescending(c => c.Id).ToList();
            foreach (var c in passedQcs)
            {
                var order = Db.ProductionOrders.AsNoTracking().FirstOrDefault(o => o.Id == c.OrderId);
                if (order == null) continue;
                var rIds = Db.FinishedGoodsReceipts.AsNoTracking()
                    .Where(r => r.OrderId == order.Id).Select(r => r.Id).ToList();
                double received = rIds.Count > 0
                    ? Db.FinishedGoodsReceiptItems.AsNoTracking().Where(i => rIds.Contains(i.ReceiptId)).Sum(i => i.ReceivedQtyKg)
                    : 0;
                if (c.AcceptedKg > received + 0.001)
                    board.Action.Add(new TaskCardDto
                    {
                        Icon = "📤",
                        Title = $"فحص معتمد — متبقٍ قابل للتسليم: {c.DocumentNumber}",
                        Subtitle = OrderLine(order) + $" | مقبول: {c.AcceptedKg:N1} | غير المستلم: {c.AcceptedKg - received:N1} كجم",
                        DocType = "QC", DocId = c.Id, DocNumber = c.DocumentNumber,
                        Priority = 1
                    });
            }

            // ما أُقفل اليوم
            foreach (var o in Db.ProductionOrders.AsNoTracking()
                         .Where(o => o.IsClosed && o.ClosedDate != null && o.ClosedDate.Value.Date == today)
                         .OrderByDescending(o => o.Id).ToList())
                board.DoneToday.Add(new TaskCardDto
                {
                    Icon = "✅",
                    Title = $"أمر أُقفل اليوم: {o.DocumentNumber}",
                    Subtitle = OrderLine(o),
                    DocType = "Order", DocId = o.Id, DocNumber = o.DocumentNumber,
                    Priority = 1
                });
        }

        // ═══ د) الجودة ═══
        if (isQc)
        {
            var qcOrderIds = Db.QualityChecks.AsNoTracking()
                .Where(c => c.OrderId != null).Select(c => c.OrderId).Distinct().ToList();
            var qcOrders = Db.ProductionOrders.AsNoTracking()
                .Where(o => qcOrderIds.Contains(o.Id)).ToList()
                .ToDictionary(o => o.Id);

            foreach (var c in Db.QualityChecks.AsNoTracking()
                         .Where(c => c.Status == DocStatuses.Submitted)
                         .OrderBy(c => c.Id).ToList())
            {
                string cooling = null;
                if (c.ExpectedCheckDate != null && c.ExpectedCheckDate.Value.Date > today)
                    cooling = $" | التبريد حتى {c.ExpectedCheckDate.Value:dd/MM}";
                board.Action.Add(new TaskCardDto
                {
                    Icon = "🔍",
                    Title = $"إنتاج جاهز للفحص: {c.DocumentNumber}",
                    Subtitle = OrderLine(qcOrders.TryGetValue(c.OrderId ?? 0, out var o) ? o : null) +
                               $" | الكمية: {c.TotalCheckedKg:N1} كجم" + cooling,
                    Due = cooling != null ? c.ExpectedCheckDate.Value.ToString("dd/MM/yyyy") : "اليوم",
                    DocType = "QC", DocId = c.Id, DocNumber = c.DocumentNumber,
                    Priority = 0
                });
            }

            // محجوز — إعادة فحص بعد المعالجة
            foreach (var c in Db.QualityChecks.AsNoTracking()
                         .Where(c => c.Status == DocStatuses.Approved && c.Decision == "Quarantine")
                         .OrderByDescending(c => c.Id).ToList())
                board.Action.Add(new TaskCardDto
                {
                    Icon = "🚫",
                    Title = $"كمية محجوزة — إعادة الفحص بعد المعالجة: {c.DocumentNumber}",
                    Subtitle = OrderLine(qcOrders.TryGetValue(c.OrderId ?? 0, out var o2) ? o2 : null),
                    DocType = "QC", DocId = c.Id, DocNumber = c.DocumentNumber,
                    Priority = 0
                });

            foreach (var c in Db.QualityChecks.AsNoTracking()
                         .Where(c => c.Status == DocStatuses.InProgress)
                         .OrderBy(c => c.Id).ToList())
                board.InFlight.Add(new TaskCardDto
                {
                    Icon = "⏳",
                    Title = $"فحص قيد التنفيذ: {c.DocumentNumber}",
                    Subtitle = OrderLine(qcOrders.TryGetValue(c.OrderId ?? 0, out var o3) ? o3 : null),
                    DocType = "QC", DocId = c.Id, DocNumber = c.DocumentNumber,
                    Priority = 1
                });

            foreach (var c in Db.QualityChecks.AsNoTracking()
                         .Where(c => c.Status == DocStatuses.Approved && c.ApprovedDate != null && c.ApprovedDate.Value.Date == today)
                         .OrderByDescending(c => c.Id).ToList())
                board.DoneToday.Add(new TaskCardDto
                {
                    Icon = "✅",
                    Title = $"فحص اعتمد اليوم: {c.DocumentNumber} — {DecisionAr(c.Decision)}",
                    Subtitle = OrderLine(qcOrders.TryGetValue(c.OrderId ?? 0, out var o4) ? o4 : null),
                    DocType = "QC", DocId = c.Id, DocNumber = c.DocumentNumber,
                    Priority = 1
                });
        }

        // ═══ هـ) أمين المخزن ═══
        if (isWh)
        {
            foreach (var d in Db.ProductionDeliveries.AsNoTracking()
                         .Where(d => d.Status == DocStatuses.Issued && d.ReceiptStatus != "Full")
                         .OrderBy(d => d.Id).ToList())
            {
                var items = Db.ProductionDeliveryItems.AsNoTracking().Where(i => i.DeliveryId == d.Id).ToList();
                board.Action.Add(new TaskCardDto
                {
                    Icon = "🏬",
                    Title = $"أمر تسليم بانتظار الاستلام: {d.DocumentNumber}",
                    Subtitle = $"العملاء: {items.Select(i => i.CustomerId).Distinct().Count()} | الأصناف: {items.Count} | {items.Sum(i => i.QtyKg):N1} كجم" +
                               (d.BypassReason != null ? " | ⚠️ تجاوز فحص موثق" : ""),
                    Due = d.DeliveryDate?.ToString("dd/MM/yyyy") ?? "اليوم",
                    DocType = "Delivery", DocId = d.Id, DocNumber = d.DocumentNumber,
                    Priority = 0
                });
            }

            foreach (var r in Db.FinishedGoodsReceipts.AsNoTracking()
                         .Where(r => r.Status == DocStatuses.Issued && r.ReceiptStatus == "Partial")
                         .OrderBy(r => r.Id).ToList())
                board.InFlight.Add(new TaskCardDto
                {
                    Icon = "🟠",
                    Title = $"استلام جزئي — المتبقي: {r.DocumentNumber}",
                    Subtitle = $"مستلم {Db.FinishedGoodsReceiptItems.Where(i => i.ReceiptId == r.Id).Sum(i => (double)i.ReceivedQtyKg):N1} من {Db.FinishedGoodsReceiptItems.Where(i => i.ReceiptId == r.Id).Sum(i => i.NetWeightKg):N1} كجم",
                    DocType = "Receipt", DocId = r.Id, DocNumber = r.DocumentNumber,
                    Priority = 1
                });

            foreach (var r in Db.FinishedGoodsReceipts.AsNoTracking()
                         .Where(r => r.ReceiptStatus == "Full" && r.ModifiedDate != null && r.ModifiedDate.Value.Date == today)
                         .OrderByDescending(r => r.Id).ToList())
                board.DoneToday.Add(new TaskCardDto
                {
                    Icon = "✅",
                    Title = $"استلام اكتمل اليوم: {r.DocumentNumber}",
                    DocType = "Receipt", DocId = r.Id, DocNumber = r.DocumentNumber,
                    Priority = 1
                });
        }

        // ═══ و) التسليم/الفوترة ═══
        if (isSales)
        {
            foreach (var cd in Db.CustomerDeliveries.AsNoTracking()
                         .Where(cd => cd.IsApproved && cd.InvoicedQtyKg < cd.TotalQtyKg)
                         .OrderBy(cd => cd.Id).ToList())
            {
                var card = new TaskCardDto
                {
                    Icon = cd.InvoicedQtyKg > 0 ? "🟠" : "🧾",
                    Title = cd.InvoicedQtyKg > 0
                        ? $"فوترة جزئية — المتبقي: {cd.DocumentNumber}"
                        : $"تسليم جاهز للفوترة: {cd.DocumentNumber}",
                    Subtitle = $"العميل: {Db.Customers.AsNoTracking().FirstOrDefault(cu => cu.Id == cd.CustomerId)?.CustomerName ?? "—"} | غير المفوتر: {Math.Max(0, cd.TotalQtyKg - cd.InvoicedQtyKg):N1} كجم",
                    Due = cd.DeliveryDate?.ToString("dd/MM/yyyy"),
                    DocType = "CustomerDelivery", DocId = cd.Id, DocNumber = cd.DocumentNumber,
                    Priority = 1
                };
                if (cd.InvoicedQtyKg > 0) { card.Bucket = "InFlight"; board.InFlight.Add(card); }
                else board.Action.Add(card);
            }

            foreach (var cd in Db.CustomerDeliveries.AsNoTracking()
                         .Where(cd => cd.IsApproved && cd.InvoicedQtyKg >= cd.TotalQtyKg && cd.TotalQtyKg > 0
                                      && cd.ModifiedDate != null && cd.ModifiedDate.Value.Date == today)
                         .OrderByDescending(cd => cd.Id).ToList())
                board.DoneToday.Add(new TaskCardDto
                {
                    Icon = "✅",
                    Title = $"فوترة اكتملت اليوم: {cd.DocumentNumber}",
                    DocType = "CustomerDelivery", DocId = cd.Id, DocNumber = cd.DocumentNumber,
                    Priority = 1
                });
        }

        // ═══ §B100 — متاح العملاء (الإنتاج/الإدارة/البيع) — ما يمكن تسليمه الآن لكل عميل ═══
        if (isProd || canApprovePlan || isSales)
            board.Customers = _availability.GetBoardSummary();

        board.Action = board.Action.OrderBy(c => c.Priority).ThenBy(c => c.DocId).ToList();
        board.InFlight = board.InFlight.OrderBy(c => c.Priority).ThenBy(c => c.DocId).ToList();
        return board;
    }

    // ═══ أدوات عرض مشتركة ═══

    private string RoleAr()
    {
        if (Session == null) return "—";
        if (Session.IsInRole("Administrator")) return "مدير النظام";
        if (Session.Can("planning", "Approve") && !Session.Can("planning", "Create")) return "المدير العام";
        if (Session.Can("planning", "Create") && !Session.Can("planning", "Approve")) return "المدير التنفيذي";
        if (Session.IsInRole("Production")) return "مدير الإنتاج";
        if (Session.Can("quality", "Edit")) return "مسؤول الجودة";
        if (Session.Can("finishedgoods", "Create")) return "أمين المخزن";
        if (Session.Can("delivery", "Create")) return "التسليم والفوترة";
        return "مستخدم";
    }

    private static string Period(ProductionPlan p)
        => p.StartDate != null && p.EndDate != null
            ? $"{p.StartDate:dd/MM} – {p.EndDate:dd/MM}"
            : (p.StartDate != null ? p.StartDate.Value.ToString("dd/MM") : "—");

    private static string OrderLine(ProductionOrder o)
        => o == null ? "—" : $"أمر {o.DocumentNumber}" +
           (o.ProductionDate != null ? $" | {o.ProductionDate:dd/MM/yyyy}" : "") +
           (o.CustomerId != null ? $" | العميل #{o.CustomerId}" : "");

    private static string DecisionAr(string d) => d switch
    {
        "Passed" => "مطابق",
        "Quarantine" => "محجوز",
        "Rejected" => "مرفوض",
        _ => d ?? "—"
    };

    private void AddManagementAlerts(TaskBoardDto board, DateTime today)
    {
        // ⏰ أوامر تجاوزت موعد إنتاجها ولم تُغلق
        var overdueOrders = Db.ProductionOrders.AsNoTracking()
            .Count(o => o.IsApproved && !o.IsClosed && o.ProductionDate != null && o.ProductionDate < today);
        if (overdueOrders > 0)
            board.Alerts.Add($"⏰ {overdueOrders} أمر إنتاج تجاوز تاريخ إنتاجه ولم يُغلق");

        // ⏰ بنود خطط معتمدة متأخرة بلا تشغيل
        var overdueItems = Db.ProductionPlanItems.AsNoTracking()
            .Where(i => i.ScheduledDate != null && i.ScheduledDate < today && i.ExecutionStatus != "Completed")
            .Select(i => i.Id).ToList();
        var approvedPlanIds2 = Db.ProductionPlans.AsNoTracking()
            .Where(p => p.IsApproved && !p.IsClosed).Select(p => p.Id).ToList();
        var overdueUnissuedCount = Db.ProductionPlanItems.AsNoTracking()
            .Count(i => overdueItems.Contains(i.Id) && approvedPlanIds2.Contains(i.PlanId)
                        && !Db.ProductionOrderItems.Any(oi => oi.PlanItemId == i.Id
                            && Db.ProductionOrders.Any(o => o.Id == oi.OrderId && o.Status != DocStatuses.Cancelled)));
        int overdueUnissued = overdueUnissuedCount;
        if (overdueUnissued > 0)
            board.Alerts.Add($"⏰ {overdueUnissued} بند خطة معتمدة تجاوز موعده دون أمر تشغيل");

        // 🚫 فحوصات بقرار مرفوض أو محجوز
        var held = Db.QualityChecks.AsNoTracking()
            .Count(c => c.IsApproved && (c.Decision == "Rejected" || c.Decision == "Quarantine"));
        if (held > 0)
            board.Alerts.Add($"🚫 {held} فحص جودة بقرار مرفوض أو حجز — ممنوع تسليمها للعميل");

        // ⛔ خام تحت حد إعادة الطلب
        var lowRaw = Db.Products.AsNoTracking()
            .Where(p => p.ItemType == "Raw" && p.IsActive && p.ReorderLevel > 0)
            .ToList()
            .Where(p => Db.StockBalances.AsNoTracking()
                .Where(b => b.ProductId == p.Id).Sum(b => (double?)b.QtyKg) is double q
                ? q < p.ReorderLevel : true)
            .Select(p => p.ProductNameAr).ToList();
        if (lowRaw.Count > 0)
            board.Alerts.Add($"⛔ خام تحت حد إعادة الطلب ({lowRaw.Count}): {string.Join("، ", lowRaw.Take(4))}{(lowRaw.Count > 4 ? "…" : "")}");

        // 🧾 تسليمات معتمدة لم تُفوتر بالكامل
        var unbilled = Db.CustomerDeliveries.AsNoTracking()
            .Count(d => d.IsApproved && d.InvoicedQtyKg < d.TotalQtyKg);
        if (unbilled > 0)
            board.Alerts.Add($"🧾 {unbilled} سند تسليم معتمد لم يُفوتر بالكامل");

        // ── §B100 — تنبيهات أعمق: أرقام سلاسل الجودة والاستلام والتسليم ──

        // 🧊 فحوصات تجاوزت تاريخها المتوقع (انتهى التبريد ولم يُفحص)
        var lateChecks = Db.QualityChecks.AsNoTracking()
            .Count(c => c.Status == DocStatuses.Submitted && c.ExpectedCheckDate != null && c.ExpectedCheckDate < today);
        if (lateChecks > 0)
            board.Alerts.Add($"🧊 {lateChecks} فحص تجاوز تاريخه المتوقع (انتهى التبريد) وما زال بلا نتيجة — العيب لا يظهر قبل أن يبرد");

        // 🏬 أوامر تسليم تجاوزت موعدها وبانتظار استلام المخزن
        var lateDels = Db.ProductionDeliveries.AsNoTracking()
            .Count(d => d.Status == DocStatuses.Issued && d.ReceiptStatus != "Full"
                        && d.DeliveryDate != null && d.DeliveryDate < today);
        if (lateDels > 0)
            board.Alerts.Add($"🏬 {lateDels} أمر تسليم تجاوز موعده وبانتظار استلام أمين المخزن");

        // 🟠 سندات استلام جزئية معلّقة
        var partialRcpts = Db.FinishedGoodsReceipts.AsNoTracking().Count(r => r.ReceiptStatus == "Partial");
        if (partialRcpts > 0)
            board.Alerts.Add($"🟠 {partialRcpts} سند استلام جزئي بانتظار استكمال البقيّة");

        // 📦 جاهز للشحن معلق في مخزن التام (دائنات عملاء على الإنتاج)
        var custAvail = _availability.GetBoardSummary();
        double deliverable = custAvail.Sum(c => c.DeliverableKg);
        if (deliverable > 0.001)
        {
            int withDue = custAvail.Count(c => c.DeliverableKg > 0.001);
            int late = custAvail.Count(c => c.Overdue && c.DeliverableKg > 0.001);
            board.Alerts.Add($"📦 {deliverable:N0} كجم جاهز للشحن عند {withDue} عميل" +
                             (late > 0 ? $" — منها {late} عميل على أيام متعثرة" : ""));
        }

        // 🛡️ تجاوزات فحص نشطة (B96) — قرار المطوّر §15-5
        var bypasses = Db.ProductionDeliveries.AsNoTracking()
            .Where(d => d.BypassReason != null && d.Status != DocStatuses.Cancelled)
            .OrderByDescending(d => d.Id).ToList();
        if (bypasses.Count > 0)
            board.Alerts.Add($"🛡️ {bypasses.Count} تجاوز فحص نشط: أوامر تسليم بدون محضر معتمد — {string.Join("، ", bypasses.Take(3).Select(d => d.DocumentNumber))}");
    }
}

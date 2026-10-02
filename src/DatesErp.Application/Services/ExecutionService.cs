using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>§7 — تنفيذ وإقفال الإنتاج مع حراس §8 (لا تنفيذ على أمر مكتمل/غير معتمد، لا تجاوز للكميات).</summary>
public class ExecutionService : ServiceBase, IExecutionService
{
    public ExecutionService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    // The simplified screen supplies NET actual raw consumption, never a gross issue estimate.
    internal bool RecordingActualDelivery { get; init; }

    // §حُذف StartExecution وCompleteExecution في B40: كانا مساراً موازياً لا تستدعيه أي شاشة.
    // المسار الفعلي: IProductionOrderService.StartOrder ثم IExecutionService.CloseProductionDay.
    // كانت 25 استدعاء اختبار تسلكهما — حُوّلت إلى المسار الحقيقي في B36.

    /// <summary>
    /// §نموذج إقفال الخطة اليومي — بديل جلسة التنفيذ:
    /// إنزال من أمر التشغيل: المفترض إنتاجه × كم خاماً استلمنا (صُرف) × كم أنتجنا × ماذا خرج
    /// (كراتين بوزنها + حشف + نوى + هالك بالكيلو) والمتبقي في صالة الإنتاج يُرحَّل اختيارياً
    /// لخطة اليوم التالي + التوقفات (كم ساعة ولماذا) + الإرسال للجودة (فحص بعد يومَي تبريد)
    /// ثم إقفال اليوم — وإن اكتملت الخطة تُقفل تلقائياً استعداداً لأمر جديد.
    /// </summary>
    public OpResult CloseProductionDay(int orderId, double producedKg, int producedCartons,
        double hashfKg, double nawaKg, double wastageKg, bool carryToNextDay,
        List<DowntimeDto> downtimes, bool sendToQuality, string notes = null,
        List<ByProductQtyDto> byProducts = null, double consumedRawKg = 0,
        List<CloseItemQtyDto> itemQtys = null,
        List<AuxActualDto> actualAux = null, double? emptyCartonsActual = null, int? cartonWarehouseId = null)
    {
        Require("execution", "Edit");
        ExecutionCloseTrace.Write($"CloseProductionDay ENTER OrderId={orderId} ProducedKg={producedKg} ProducedCartons={producedCartons}");
        var order = Db.ProductionOrders.Include(o => o.Items).FirstOrDefault(o => o.Id == orderId);
        if (order == null) return OpResult.Fail("أمر التشغيل غير موجود.");
        if (!order.IsApproved) return OpResult.Fail("لا يمكن إقفال يوم أمر غير معتمد.");
        if (order.IsClosed) return OpResult.Fail("هذا الأمر مقفل مسبقاً.");
        if (Db.ProductionExecutions.Any(e => e.OrderId == orderId && e.IsDayClosed))
            return OpResult.Fail("يوم الإنتاج لهذا الأمر مقفل مسبقاً — لا يسمح بتكرار الإقفال.");
        // §B85/H1: منع الازدواج — بنود أُقفلت عبر مسار «بنود الخطة» القديم (المحذوف في B95) لا يُقفل يوم أمرها أيضاً
        if (order.Items.Any(i => i.IsClosed))
            return OpResult.Fail("بنود هذا الأمر مقفلة مسبقاً عبر مسار قديم — لا يجوز إقفال يومه أيضاً (منعاً لازدواج الإنتاج والخام). راجع إدارة النظام لمعالجة البيانات القديمة.");

        return RunOp(() =>
        {
            // §1.50.66.2/4/7/10 — إعادة التحقق قبل الإقفال: الخطة ما زالت معتمدة، والكميات ضمن المسموح، والفصل بين المراحل
            // §6 — ربط التنفيذ الفعلي بالخطة والأمر والدفعة والمنتج والعميل
            if (order.SourcePlanId != null)
            {
                var planCheck = Db.ProductionPlans.AsNoTracking().FirstOrDefault(p => p.Id == order.SourcePlanId);
                if (planCheck == null) throw new DomainException("الخطة المرجعية غير موجودة — لا يمكن إقفال يوم الإنتاج.", "PLAN_MISSING");
                if (!planCheck.IsApproved || planCheck.Status != DocStatuses.Approved || planCheck.IsClosed)
                    throw new DomainException($"لا يمكن إقفال يوم الإنتاج: الخطة المرجعية {planCheck.DocumentNumber} ليست معتمدة حالياً (حالتها: {DocStatuses.ToArabic(planCheck.Status)}).", "PLAN_NOT_APPROVED");
            }
            // §1.50.66.7 — منع المنتَج > المخطط أو > المتاح
            double plannedTotalCheck = order.Items.Sum(i => i.PlannedQtyKg);
            double producedSoFarCheck = order.Items.Sum(i => i.ProducedQtyKg);
            if (producedSoFarCheck + producedKg > plannedTotalCheck + 0.001)
                throw new DomainException($"⛔ كمية الإنتاج الفعلية ({producedKg:N1} كجم) مع ما سبق ({producedSoFarCheck:N1}) تتجاوز المخطط الكلي ({plannedTotalCheck:N1} كجم).", "OVER_PLAN");
            // §1.50.66 — فصل واضح: Production → Quality → Finished Goods Available مع سجل المستخدم والتاريخ
            // هذه المرحلة Production فقط — لا تمنح رصيد بيع قبل Quality Gate
            // §8 — حراس الكميات
            if (producedKg < 0 || hashfKg < 0 || nawaKg < 0 || wastageKg < 0 || producedCartons < 0)
                throw new DomainException("الكميات لا يمكن أن تكون سالبة.");
            // §B95 — إقفال يوم بلا إنتاج مرفوض: يحمي من الإقفال الفارغ بالخطأ
            // (ورث دور اختبار «قائمة الإقفال الفارغة» من مسار بنود الخطة المحذوف)
            bool noOutput = producedKg <= 0 && producedCartons <= 0 && hashfKg <= 0 && nawaKg <= 0 && wastageKg <= 0
                && (byProducts == null || !byProducts.Any(b => b != null && b.QtyKg > 0))
                && (itemQtys == null || !itemQtys.Any(q => q != null && (q.ProducedKg > 0 || q.ProducedCartons > 0)));
            if (noOutput)
                throw new DomainException("⛔ لا يمكن إقفال يوم بلا إنتاج — أدخل الكمية المنتجة أو المخرجات الفعلية.");
            // §B88/M13: الإقفال متعدد الأصناف — كميات كل بند تُفحص بهوية صنفه وعبوته (كجم + كراتين) وتُكتب مباشرة
            bool perItem = itemQtys != null && itemQtys.Count > 0;
            Dictionary<int, (double kg, int boxes)> itemTake = null;
            if (perItem)
            {
                itemTake = new Dictionary<int, (double kg, int boxes)>();
                foreach (var q in itemQtys)
                {
                    if (q == null) continue;
                    if (q.ProducedKg < 0 || q.ProducedCartons < 0)
                        throw new DomainException("كميات البنود لا يمكن أن تكون سالبة.");
                    var oi0 = order.Items.FirstOrDefault(i => i.Id == q.OrderItemId)
                        ?? throw new DomainException("بند الإقفال غير تابع لهذا الأمر — حدّث الشاشة وأعد الإدخال.", "UNKNOWN_CLOSE_ITEM");
                    var prev = itemTake.TryGetValue(oi0.Id, out var pv) ? pv : (kg: 0.0, boxes: 0);
                    itemTake[oi0.Id] = (prev.kg + q.ProducedKg, prev.boxes + q.ProducedCartons);
                }
                foreach (var kv in itemTake)
                {
                    var oi = order.Items.First(i => i.Id == kv.Key);
                    string prodName = Db.Products.AsNoTracking().Where(x => x.Id == oi.ProductId).Select(x => x.ProductNameAr).FirstOrDefault() ?? $"صنف #{oi.ProductId}";
                    if (oi.ProducedQtyKg + kv.Value.kg > oi.PlannedQtyKg + 0.001)
                        throw new DomainException(
                            $"كمية الإنتاج للبند «{prodName}» أكبر من متبقيه.\nالمخطط: {oi.PlannedQtyKg:N1} كجم | المنتَج حتى الآن: {oi.ProducedQtyKg:N1} | المطلوب تسجيله: {kv.Value.kg:N1}",
                            "OVER_PRODUCTION");
                    if (oi.ProducedCartons + kv.Value.boxes > oi.PlannedCartons)
                        throw new DomainException(
                            $"كراتين الإنتاج للبند «{prodName}» أكبر من متبقيه.\nالمخطط: {oi.PlannedCartons:N0} | المنتَج حتى الآن: {oi.ProducedCartons:N0} | المطلوب تسجيله: {kv.Value.boxes:N0}",
                            "OVER_PRODUCTION");
                    // §لا تناقض بين الكراتين والوزن — لكل بند بهوية صنفه وعبوته (لا إجمالي أعمى على صنف واحد)
                    UnitsPolicy.RequireCartonWeight(Db, oi.ProductId, oi.PackagingTypeId, kv.Value.boxes,
                        $"إقفال يوم الإنتاج — بند «{prodName}»");
                    UnitsPolicy.EnsureCartonKgConsistency(Db, oi.ProductId, oi.PackagingTypeId,
                        kv.Value.kg, kv.Value.boxes, $"إقفال يوم الإنتاج — بند «{prodName}»");
                }
                producedKg = itemTake.Values.Sum(v => v.kg);
                producedCartons = itemTake.Values.Sum(v => v.boxes);
            }
            else
            {
                double producedSoFar = order.Items.Sum(i => i.ProducedQtyKg);
                double plannedTotal = order.Items.Sum(i => i.PlannedQtyKg);
                if (producedSoFar + producedKg > plannedTotal + 0.001)
                    throw new DomainException(
                        $"كمية الإنتاج أكبر من المسموح بها للأمر.\nالمخطط: {plannedTotal:N1} كجم | المنتَج حتى الآن: {producedSoFar:N1} | المطلوب تسجيله: {producedKg:N1}",
                        "OVER_PRODUCTION");

                // §المسار الإجمالي القديم (بلا تفصيل بنود — للتوافق): يُفحص الإجمالي على الصنف الأول.
                // الشاشة تمرر تفصيل البنود دائماً فيُفحص كل بند بهويته (M13).
                foreach (var oiChk in order.Items)
                {
                    UnitsPolicy.RequireCartonWeight(Db, oiChk.ProductId, oiChk.PackagingTypeId, producedCartons,
                        "إقفال يوم الإنتاج");
                    UnitsPolicy.EnsureCartonKgConsistency(Db, oiChk.ProductId, oiChk.PackagingTypeId,
                        producedKg, producedCartons, "إقفال يوم الإنتاج");
                    break;
                }
            }

            // The finished-goods target is not the raw-input target (process water
            // may increase output weight). Prefer each plan line's *explicit* raw
            // draw; old lines without one still fall back conservatively to output
            // weight. A split order takes only its fraction of its parent plan line.
            if (consumedRawKg < 0)
                throw new DomainException("الخام المستهلك فعلياً لا يمكن أن يكون سالباً.", "RAW_NEGATIVE");
            var planItemIds = order.Items.Where(i => i.PlanItemId != null)
                .Select(i => i.PlanItemId!.Value).Distinct().ToList();
            var sourceByPlanItem = Db.ProductionPlanItems.AsNoTracking()
                .Where(i => planItemIds.Contains(i.Id))
                .Select(i => new { i.Id, i.PlannedQtyKg, i.SourceQtyKg })
                .ToDictionary(i => i.Id);
            double RawAllocation(ProductionOrderItem item)
            {
                if (item.PlanItemId is int id && sourceByPlanItem.TryGetValue(id, out var source)
                    && source.PlannedQtyKg > 0 && source.SourceQtyKg > 0)
                    return source.SourceQtyKg * item.PlannedQtyKg / source.PlannedQtyKg;
                return item.PlannedQtyKg;
            }
            var rawByItem = order.Items.Where(i => i.LotId != null)
                .ToDictionary(i => i.Id, RawAllocation);
            double plannedRaw = rawByItem.Values.Sum();
            double consumed = consumedRawKg > 0 ? consumedRawKg : plannedRaw;

            // صرف الخام فعلياً من الدفعات — هنا لا عند الاعتماد
            var whRawForClose = WarehouseId("WRM");
            var takeByLot = new Dictionary<int, double>(); // §B86/M12: المصروف الفعلي لكل دفعة — أساس توزيع المرتجع
            var custByLot = new Dictionary<int, int?>();   // §B88: عميل حركة الصرف لكل دفعة (أول بنودها)
            foreach (var oi in order.Items.Where(i => i.LotId != null))
            {
                double share = plannedRaw > 0 ? rawByItem[oi.Id] / plannedRaw : 0;
                double take = RecordingActualDelivery ? consumed * share : Math.Round(consumed * share, 1);
                if (take <= 0) continue;
                takeByLot[oi.LotId.Value] = (takeByLot.TryGetValue(oi.LotId.Value, out var tv) ? tv : 0) + take;
                if (!custByLot.ContainsKey(oi.LotId.Value))
                {
                    // §B102 — حركة الصرف تتبع مالك الدفعة (صاحب الخام الفعلي) لا عميل البند التام:
                    // في الأوامر متعددة العملاء كان الصرف يُسجَّل لعميل لا يملك الخام فينهار بـ«رصيد 0».
                    var lotOwner = Db.Lots.AsNoTracking().Where(l => l.Id == oi.LotId.Value).Select(l => l.CustomerId).FirstOrDefault();
                    custByLot[oi.LotId.Value] = lotOwner ?? oi.CustomerId ?? order.CustomerId;
                }
            }
            if (RecordingActualDelivery && takeByLot.Count > 0)
            {
                // Preserve the existing proportional lot allocation, but never lose input to rounding.
                int lastLot = takeByLot.Keys.Last();
                takeByLot[lastLot] += consumed - takeByLot.Values.Sum();
            }
            // §B88: حركة صرف واحدة لكل دفعة — بنود الدفعة الواحدة كانت تنشر حركات مكررة بنفس المرجع (DUPLICATE)
            // §1.50.72 P3-6: عند غياب «الخام الفعلي» يُعتمد المخطط (سلوك موثق) — الحركة تُوسم بذلك
            // حتى لا يتحول الانحراف في دفتر الخام إلى صمت.
            string rawTag = consumedRawKg > 0 ? "" : " (بقيمة المخطط — لم يُدخل الفعلي)";
            foreach (var kvLot in takeByLot)
            {
                ConsumeLot(kvLot.Key, kvLot.Value, "إقفال يوم الإنتاج");
                // البند الحديث يعود بعد المعالجة إلى مخزن استلامه الفعلي، والصرف يجب أن يتبعه.
                // السجلات السابقة تظل على مسار WRM القديم، فلا تتغير حركة تاريخية ضمنياً.
                var sourceLot = Db.Lots.Single(l => l.Id == kvLot.Key);
                var sourceWarehouse = Db.Shipments.Where(s => s.Id == sourceLot.ShipmentId)
                    .Select(s => s.ReceivingWarehouseId).FirstOrDefault() ?? whRawForClose;
                PostStockMovement(sourceWarehouse, MovementType.Outbound, kvLot.Value, 0,
                    ReferenceDocType.ProductionExecution, order.DocumentNumber,
                    productId: sourceLot.ProductId, lotId: kvLot.Key,
                    customerId: custByLot.TryGetValue(kvLot.Key, out var cc) ? cc : order.CustomerId,
                    packagingTypeId: sourceLot.PackagingTypeId, orderId: order.Id,
                    notes: "صرف خام فعلي عند إقفال يوم الإنتاج" + rawTag);
            }
            // §المخرجات الثانوية: القائمة الديناميكية هي المرجع إن وُجدت. وإن غابت يُعتمد على
            // العمودين القديمين — ولا يُجمعان معاً أبداً، وإلا عُدّ المخرج مرتين
            // (CloseDayDialog يملأ الاثنين معاً مطابقةً بالاسم للبيانات السابقة).
            double byTotal = byProducts?.Where(b => b != null && b.QtyKg > 0).Sum(b => b.QtyKg) ?? 0;
            if (RecordingActualDelivery)
            {
                // Non-weight units remain recorded in their configured unit; never count pieces/litres as kg.
                var kgDefinitions = Db.Products.AsNoTracking().Where(p => p.ItemType == "ByProduct").ToList().Where(b =>
                    (b.UnitOfMeasure ?? "").Trim().ToLowerInvariant() is "كجم" or "كغ" or "kg" or "كيلوجرام").Select(b => b.Id).ToHashSet();
                byTotal = byProducts?.Where(b => b != null && kgDefinitions.Contains(b.ByProductId)).Sum(b => b.QtyKg) ?? 0;
            }
            double secondary = byTotal > 0 ? byTotal : hashfKg + nawaKg;
            double outputs = producedKg + secondary + wastageKg;
            // §قاعدة توازن الإنتاج: لا معادلة ثابتة ولا رفض.
            // في تصنيع التمور يزيد وزن الخارج عن الداخل لإضافة الماء أثناء التشغيل،
            // والماء لا يُسجَّل صنفاً ولا مدخلاً مستقلاً. فالنظام يقبل الكميات الفعلية
            // ويحسب الفرق ويعرضه في «تقرير توازن الإنتاج» إجراءً رقابياً — لا يمنع العملية.
            double remainingInHall = RecordingActualDelivery ? 0 : Math.Round(Math.Max(0, consumed - outputs), 1);
            // §1.50.66.9 — لا يُمنح رصيد بيع (WFG) في هذه المرحلة؛ الإنتاج غير صالح للبيع عند SaveActualProduction
            // المسار: Production → Quality Gate (IsApproved) → Finished Goods Available — فصل صارم

            // §B85/H3: انحراف معامل الإنتاجية — المتوقع من المعامل مقابل الفعلي (تنبيه فقط، لا رفض)
            string yieldMsg = "";
            // §B88/M13: متعدد الأصناف — سطر تنبيه لكل صنف بنسبة إنتاجه من المستهلك
            var closeDistinctProds = order.Items.Select(i => i.ProductId).Distinct().ToList();
            if (perItem && closeDistinctProds.Count > 1 && producedKg > 0)
            {
                var yLines = new List<string>();
                foreach (var pid in closeDistinctProds)
                {
                    double pKg = itemTake!.Where(kv => order.Items.First(i => i.Id == kv.Key).ProductId == pid).Sum(kv => kv.Value.kg);
                    if (pKg <= 0) continue;
                    double? pf = Db.Products.AsNoTracking().Where(x => x.Id == pid).Select(x => x.YieldFactor).FirstOrDefault();
                    if (pf == null || pf <= 0) continue;
                    double pShare = pKg / producedKg;
                    double pConsumed = consumed * pShare;
                    double pExpected = pKg / pf.Value;
                    double pVar = pConsumed - pExpected;
                    double pPct = pExpected > 0 ? pVar / pExpected * 100 : 0;
                    string pName = Db.Products.AsNoTracking().Where(x => x.Id == pid).Select(x => x.ProductNameAr).FirstOrDefault() ?? $"صنف #{pid}";
                    yLines.Add($"{pName}: المتوقع {pExpected:N1} مقابل فعلي {pConsumed:N1} — الانحراف {(pVar >= 0 ? "+" : "")}{pVar:N1} ({(pPct >= 0 ? "+" : "")}{pPct:0.0}%)" + (Math.Abs(pPct) > 5 ? " ⚠" : ""));
                }
                if (yLines.Count > 0)
                    yieldMsg = "\n📊 معامل الإنتاجية (موزَّع بنسبة الإنتاج): " + string.Join("؛ ", yLines) + ".";
            }
            else
            {
            int? firstProdId = order.Items.FirstOrDefault()?.ProductId;
            double? yf = firstProdId != null
                ? Db.Products.AsNoTracking().Where(p => p.Id == firstProdId.Value).Select(p => p.YieldFactor).FirstOrDefault()
                : null;
            if (yf != null && yf > 0 && outputs > 0)
            {
                double expectedConsumed = outputs / yf.Value;
                double variance = consumed - expectedConsumed;
                double varPct = expectedConsumed > 0 ? variance / expectedConsumed * 100 : 0;
                string vSign = variance >= 0 ? "+" : "";
                string pSign = varPct >= 0 ? "+" : "";
                yieldMsg = $"\n📊 معامل الإنتاجية ({yf.Value:0.###}): المتوقع {expectedConsumed:N1} كجم مقابل فعلي {consumed:N1} — الانحراف {vSign}{variance:N1} كجم ({pSign}{varPct:0.0}%).";
                if (Math.Abs(varPct) > 5)
                    yieldMsg += " ⚠ يتجاوز ±5% — راجع القياس أو حدّث المعامل من بطاقة الصنف.";
            }
            else if (outputs > consumed + 0.001)
            {
                // §B85/H2: زيادة الخارج عن الداخل = ماء التشغيل غالباً — تُعرض ولا تُرفض
                yieldMsg = $"\n💧 الخارج يزيد عن الداخل بـ {outputs - consumed:N1} كجم (ماء التشغيل غالباً) — حدّد معامل الإنتاجية في بطاقة الصنف لضبط الانحراف تلقائياً.";
            }
            }

            // جلسة الإقفال: نحدد التنفيذ المفتوح المرتبط بهذا الأمر فقط.
            // لا نعتمد على Status وحده؛ فقد بقيت بعض السجلات القديمة بحالة Completed
            // مع IsDayClosed=false، وكان ذلك ينشئ سجلاً ثانياً بدلاً من إكمال السجل الصحيح.
            var openExecutions = Db.ProductionExecutions
                .Include(x => x.Downtimes).Include(x => x.ByProducts)
                .Where(e => e.OrderId == orderId && !e.IsDayClosed)
                .OrderByDescending(e => e.Id).ToList();
            ExecutionCloseTrace.Write($"CloseProductionDay BEFORE_SELECT OrderId={orderId} OpenExecutions=[{string.Join(" | ", openExecutions.Select(e => $"Id={e.Id},OrderId={e.OrderId},IsDayClosed={e.IsDayClosed},Status={e.Status},EndDateTime={e.EndDateTime:O}"))}]");
            if (openExecutions.Count > 1)
                throw new DomainException("يوجد أكثر من سجل تنفيذ مفتوح لنفس أمر الإنتاج — لا يمكن تحديد جلسة واحدة للإقفال. راجع سجل التنفيذ قبل المتابعة.", "MULTIPLE_OPEN_EXECUTIONS");
            var exe = openExecutions.SingleOrDefault();
            bool isNew = exe == null;
            if (isNew)
            {
                exe = new ProductionExecution
                {
                    DocumentNumber = Numbering.Next("EXE"),
                    OrderId = orderId,
                    LineId = order.LineId,
                    ShiftId = order.ShiftId,
                    StartDateTime = DateTime.Now,
                    CreatedBy = Session?.UserId,
                    CreatedDate = DateTime.Now
                };
            }
            // §1.50.66.6 — ربط التنفيذ بالخطة والأمر والدفعة والمنتج والعميل + سجل المستخدم والتاريخ
            // OrderId يربط بـ SourcePlanId و CustomerId و Items (LotId/ProductId) — لا حاجة لحقول إضافية، لكن نوثق المستخدم والتاريخ
            exe.EndDateTime = DateTime.Now;
            exe.ModifiedBy = Session?.UserId;
            exe.ModifiedDate = DateTime.Now;
            exe.Status = DocStatuses.Completed;
            exe.ActualQtyKg = producedKg;
            exe.ActualCartons = producedCartons;
            exe.WastageQtyKg = wastageKg;
            exe.HashfKg = hashfKg;
            exe.NawaKg = nawaKg;
            exe.ConsumedRawKg = consumed;
            exe.RemainingInHallKg = remainingInHall;
            exe.CarryToNextDay = carryToNextDay && remainingInHall > 0;
            exe.QualitySent = sendToQuality;
            exe.ExpectedQualityDate = sendToQuality ? DateTime.Today.AddDays(2) : null;
            exe.IsDayClosed = true;
            exe.ClosingNotes = notes;
            ExecutionCloseTrace.Write($"CloseProductionDay MARKED OrderId={orderId} ExecutionId={exe.Id} IsDayClosed={exe.IsDayClosed} Status={exe.Status} EndDateTime={exe.EndDateTime:O}");

            // §المخرجات الثانوية بأصنافها المعرَّفة (لا «حشف/نوى» مفروضة)
            if (byProducts != null)
                foreach (var bp in byProducts)
                {
                    if (bp == null || bp.QtyKg <= 0) continue;
                    if (!Db.Products.Any(b => b.Id == bp.ByProductId && b.IsActive && b.ItemType == "ByProduct"))
                        throw new DomainException("المخرج الثانوي غير موجود في بطاقة الأصناف كـ(مخرج ثانوي) أو موقوف — عرّفه من إعدادات الأصناف.");
                    exe.ByProducts.Add(new ExecutionByProduct { ByProductId = bp.ByProductId, Qty = (decimal)bp.QtyKg });
                }

            // §التوقفات: كم ساعة ولماذا
            if (downtimes != null)
                foreach (var dt in downtimes)
                    if (dt != null && dt.Hours > 0 && !string.IsNullOrWhiteSpace(dt.ReasonAr))
                        exe.Downtimes.Add(new ExecutionDowntime { Hours = dt.Hours, ReasonAr = dt.ReasonAr.Trim(), StartTime = dt.StartTime, EndTime = dt.EndTime });

            if (perItem)
            {
                // §B88/M13: كتابة مباشرة لكل بند بكميته المفحوصة — لا توزيع إجمالي أعمى بالترتيب
                foreach (var kv in itemTake!)
                {
                    var item = order.Items.First(i => i.Id == kv.Key);
                    item.ProducedQtyKg += kv.Value.kg;
                    item.ProducedCartons += kv.Value.boxes;
                }
            }
            else
            {
                // توزيع المنتَج على بنود الأمر بالترتيب (المسار الإجمالي القديم)
                double remaining = producedKg;
                foreach (var item in order.Items.Where(i => i.ProducedQtyKg < i.PlannedQtyKg))
                {
                    double take = Math.Min(remaining, item.PlannedQtyKg - item.ProducedQtyKg);
                    if (take <= 0) continue;
                    item.ProducedQtyKg += take;
                    remaining -= take;
                    if (remaining <= 0.001) break;
                }
                // §B86/H7: توزيع الكراتين على البنود بالترتيب وبحدود مخطط كل بند — كانت لا تُكتب أبداً فانكسر تتبع الكراتين
                int remainingBoxes = producedCartons;
                foreach (var item in order.Items.Where(i => i.ProducedCartons < i.PlannedCartons))
                {
                    int boxTake = Math.Min(remainingBoxes, item.PlannedCartons - item.ProducedCartons);
                    if (boxTake <= 0) continue;
                    item.ProducedCartons += boxTake;
                    remainingBoxes -= boxTake;
                    if (remainingBoxes <= 0) break;
                }
            }
            // §حالة الأمر: أُقفل يوم الإنتاج أو اكتملت البنود ← «مكتمل» وإقفال الأمر
            order.Status = DocStatuses.Completed;
            order.IsClosed = true;
            order.ClosedDate = DateTime.Now;
            foreach (var item in order.Items)
            {
                item.IsClosed = true;
                item.Status = DocStatuses.Completed;
            }

            if (isNew) Db.ProductionExecutions.Add(exe);
            // حفظ مبكر مقصود: يثبت سجل التنفيذ قبل متابعة الترحيلات اللاحقة،
            // مع بقائه داخل نفس المعاملة الذرية حتى يُلغى كله إذا فشل أي حارس لاحق.
            Db.SaveChanges();
            var persistedExecution = Db.ProductionExecutions.AsNoTracking()
                .FirstOrDefault(e => e.Id == exe.Id && e.OrderId == orderId);
            ExecutionCloseTrace.Write($"CloseProductionDay AFTER_SAVE OrderId={orderId} ExecutionId={persistedExecution?.Id.ToString() ?? "<null>"} IsDayClosed={persistedExecution?.IsDayClosed.ToString() ?? "<null>"} Status={persistedExecution?.Status ?? "<null>"} EndDateTime={persistedExecution?.EndDateTime?.ToString("O") ?? "<null>"}");
            if (persistedExecution == null || !persistedExecution.IsDayClosed
                || persistedExecution.Status != DocStatuses.Completed || persistedExecution.EndDateTime == null)
                throw new DomainException(
                    "تعذر تثبيت إقفال يوم الإنتاج في سجل التنفيذ — لم تُحفظ العملية.",
                    "EXECUTION_CLOSE_NOT_PERSISTED");

            // §المسار الموحد يرجع المتبقي من الخام إلى مخزن الخام بحركة مرتجع موثقة
            // ويزيد رصيد الدفعة — فلا يختفي الخام المتبقي من الحساب. يُوزَّع بحسب الدفعات.
            if (remainingInHall > 0.001)
            {
                // §B86/M12: المرتجع بنسبة المصروف الفعلي لكل دفعة (نفس الوحدة فيجمع لـ1 — كان المخطط/المستهلك مخلوطاً)
                // مع سد كسور التقريب في آخر دفعة، وحركة واحدة للدفعة (بندان من دفعة واحدة لا يكرران الحركة)
                double totalTake = takeByLot.Values.Sum();
                double backAssigned = 0;
                var backLots = takeByLot.Keys.ToList();
                for (int li = 0; li < backLots.Count; li++)
                {
                    var lotBack = Db.Lots.FirstOrDefault(l => l.Id == backLots[li]);
                    if (lotBack == null) continue;
                    bool lastBack = li == backLots.Count - 1;
                    double back = lastBack
                        ? Math.Round(remainingInHall - backAssigned, 1)
                        : Math.Round(remainingInHall * (totalTake > 0 ? takeByLot[backLots[li]] / totalTake : 0), 1);
                    if (back <= 0) continue;
                    backAssigned += back;
                    lotBack.InStockQtyKg += back;
                    // Reverse into the SAME warehouse/owner/package stock key used
                    // for the outbound raw movement (which may be a receiving
                    // warehouse other than WRM and may carry a package type).
                    int rawWarehouse = Db.Shipments.Where(s => s.Id == lotBack.ShipmentId)
                        .Select(s => s.ReceivingWarehouseId).FirstOrDefault() ?? whRawForClose;
                    PostStockMovement(rawWarehouse, MovementType.Inbound, back, 0,
                        ReferenceDocType.Return, exe.DocumentNumber,
                        productId: lotBack.ProductId, lotId: lotBack.Id,
                        customerId: custByLot.TryGetValue(lotBack.Id, out var rawOwner) ? rawOwner : lotBack.CustomerId,
                        packagingTypeId: lotBack.PackagingTypeId, orderId: order.Id,
                        notes: $"مرتجع متبقي إقفال يوم الإنتاج إلى مخزن الخام — نفس العميل والدفعة ({lotBack.LotCode})");
                }
            }

            // ═══════════════════════════════════════════════════════════════════
            // §B95 — تسوية المواد المساعدة مقابل الفعلي (مُرحَّلة من مسار بنود الخطة المحذوف):
            // المستهلك = فعلي × معادلة لكل منتج + إدخالات الفعلي اليدوية؛ والفرق عن المصروف
            // يُرتجع/يُصرف آلياً بحركة موثقة. يُحسب لكل منتج بكراتينه المكتوبة أعلاه —
            // وهي كراتين هذا الإقفال حصراً لأن الأمر يُقفل مرة واحدة فقط (الحارس أعلاه).
            // ═══════════════════════════════════════════════════════════════════
            var perProdCarts = order.Items.GroupBy(i => i.ProductId)
                .ToDictionary(g => g.Key, g => (double)g.Sum(i => i.ProducedCartons));
            Db.Entry(order).Collection(o => o.Materials).Load();
            var ordSvc = new ProductionOrderService(Db, Session, Numbering);
            var whAux = WarehouseId("WAUX");
            foreach (var mat in order.Materials.Where(m => m.ActualIssuedQty > 0 || m.AuxiliaryProductId is > 0).ToList())
            {
                if (mat.AuxiliaryProductId is > 0)
                {
                    // Modern BOM: consume the ProductId actually stocked, by
                    // each produced line's customer/package identity. A legacy
                    // MaterialId=0 must never create a phantom stock movement.
                    double used = mat.Status == DocStatuses.Completed ? mat.ConsumedQty : 0;
                    if (mat.Status != DocStatuses.Completed)
                    {
                        foreach (var producedItem in order.Items.Where(i => i.ProducedQtyKg > 0.001))
                        {
                            var reqs = Db.ProductAuxiliaryRequirements.AsNoTracking()
                                .Where(r => r.IsActive && r.FinishedProductId == producedItem.ProductId).ToList();
                            foreach (var req in reqs)
                            {
                                int effectiveId = ordSvc.ResolveAuxProductForCustomer(req.AuxiliaryProductId,
                                    producedItem.CustomerId ?? order.CustomerId,
                                    producedItem.ProductId, producedItem.PackagingTypeId);
                                if (effectiveId != mat.AuxiliaryProductId) continue;
                                used += (double)req.QtyPerCarton * (req.CalculationMethod == "PerKg"
                                    ? producedItem.ProducedQtyKg : producedItem.ProducedCartons);
                            }
                        }
                        mat.ConsumedQty = used;
                    }
                    double difference = mat.ActualIssuedQty - mat.ReturnedQty - used - mat.WastedQty;
                    if (difference > 0.001)
                    {
                        var (kg, packages) = AuxiliaryStockPolicy.Movement(difference, mat.UnitOfMeasure);
                        PostStockMovement(whAux, MovementType.Inbound, kg, packages,
                            ReferenceDocType.MaterialReturn, exe.DocumentNumber,
                            productId: mat.AuxiliaryProductId, orderId: order.Id,
                            notes: $"مرتجع مساعد بعد إقفال يوم الإنتاج — صافي الفائض {difference:N3}");
                        mat.ReturnedQty += difference;
                    }
                    else if (difference < -0.001)
                    {
                        var (kg, packages) = AuxiliaryStockPolicy.Movement(-difference, mat.UnitOfMeasure);
                        PostStockMovement(whAux, MovementType.Outbound, kg, packages,
                            ReferenceDocType.MaterialIssue, exe.DocumentNumber,
                            productId: mat.AuxiliaryProductId, orderId: order.Id,
                            notes: $"صرف تكميلي مساعد بعد إقفال يوم الإنتاج {(-difference):N3}");
                        mat.ActualIssuedQty += -difference;
                    }
                    continue;
                }
                // §B95 — الاستهلاك المسجل يدوياً (ConsumeMaterials) يُحترم ولا يُعاد حسابه
                double consumedAux;
                if (mat.Status == DocStatuses.Completed && mat.ConsumedQty > 0)
                    consumedAux = mat.ConsumedQty;
                else
                {
                    consumedAux = 0;
                    foreach (var kvProd in perProdCarts)
                    {
                        if (kvProd.Value <= 0) continue;
                        var fms = Db.ConsumptionFormulas.AsNoTracking().Where(f => f.ProductId == kvProd.Key && f.IsActive
                            && f.Mode == "PerCarton" && (f.CustomerId == null || f.CustomerId == order.CustomerId)).ToList();
                        foreach (var f in fms)
                            if (ordSvc.ResolveAuxMaterial(f, order.CustomerId) == mat.MaterialId)
                                consumedAux += f.QtyPerUnit * kvProd.Value;
                    }
                    if (actualAux != null)
                        consumedAux += actualAux.Where(a => a.OrderId == order.Id && a.MaterialId == mat.MaterialId).Sum(a => a.Qty);
                    mat.ConsumedQty = consumedAux;
                }
                // §B95 — الهالك المسجل يُستبعد من المرتجع: كان يُعاد للمخزن كسليم في المسار المحذوف
                double diff = mat.ActualIssuedQty - mat.ReturnedQty - consumedAux - mat.WastedQty;
                // §B95 — اصطلاح الدفتر: الكمية موقعة (الصرف سالب) والرصيد المساعد إجمالي بلا عميل (كصرف الاعتماد)
                if (diff > 0.001)
                {
                    mat.ReturnedQty += diff;
                    Db.InventoryTransactions.Add(new InventoryTransaction
                    {
                        TxnNumber = Numbering.Next("TXN"), WarehouseId = whAux, MaterialId = mat.MaterialId,
                        MovementType = MovementType.Inbound, QtyKg = diff,
                        ReferenceDocType = ReferenceDocType.MaterialReturn, ReferenceDocNumber = exe.DocumentNumber,
                        OrderId = order.Id, IsApproved = true,
                        Notes = $"مرتجع آلي عند إقفال يوم الإنتاج: مصروف {mat.ActualIssuedQty:N1} − مستهلك {consumedAux:N1}"
                    });
                    var bal = Db.StockBalances.FirstOrDefault(s => s.WarehouseId == whAux && s.MaterialId == mat.MaterialId && s.ProductId == null && s.LotId == null && s.CustomerId == null && s.PackagingTypeId == null);
                    if (bal == null) { bal = new StockBalance { WarehouseId = whAux, MaterialId = mat.MaterialId, LotId = null, CustomerId = null, PackagingTypeId = null }; Db.StockBalances.Add(bal); }
                    bal.QtyKg += diff;
                }
                else if (diff < -0.001)
                {
                    // §1.50.72 P2-3: حارس الرصيد السالب — كانت التسوية تكتب الرصيد مباشرة
                    // (بلا حارس PostStockMovement) فيمكن للمستهلك > المصروف أن يسلب مخزن WAUX.
                    var bal2 = Db.StockBalances.FirstOrDefault(s => s.WarehouseId == whAux && s.MaterialId == mat.MaterialId && s.ProductId == null && s.LotId == null && s.CustomerId == null && s.PackagingTypeId == null);
                    double bal2Before = bal2?.QtyKg ?? 0;
                    if (bal2Before + diff < -0.001)
                    {
                        string matName2 = Db.AuxiliaryMaterials.AsNoTracking().Where(m => m.Id == mat.MaterialId).Select(m => m.MaterialNameAr).FirstOrDefault() ?? $"#{mat.MaterialId}";
                        throw new DomainException(
                            $"⛔ تعذر إقفال اليوم: رصيد الصنف المساعد «{matName2}» في مخزن المساعدة ({bal2Before:N1}) لا يغطي الفارق التكميلي ({-diff:N1}).\nالمستهلك {consumedAux:N1} أكبر من المصروف {mat.ActualIssuedQty:N1} — قيّد صرفاً/تسوية تغطي الفرق أولاً ثم أقرِل اليوم.",
                            "AUX_NEGATIVE_BALANCE");
                    }
                    if (bal2 == null) { bal2 = new StockBalance { WarehouseId = whAux, MaterialId = mat.MaterialId, LotId = null, CustomerId = null, PackagingTypeId = null }; Db.StockBalances.Add(bal2); }
                    bal2.QtyKg += diff;
                    Db.InventoryTransactions.Add(new InventoryTransaction
                    {
                        TxnNumber = Numbering.Next("TXN"), WarehouseId = whAux, MaterialId = mat.MaterialId,
                        MovementType = MovementType.Outbound, QtyKg = diff,
                        ReferenceDocType = ReferenceDocType.MaterialIssue, ReferenceDocNumber = exe.DocumentNumber,
                        OrderId = order.Id, IsApproved = true,
                        Notes = $"صرف تكميلي آلي عند إقفال يوم الإنتاج: مستهلك {consumedAux:N1} − مصروف {mat.ActualIssuedQty:N1}"
                    });
                    mat.ActualIssuedQty += -diff; // The additional real issue is part of gross issued stock.
                }
            }
            // §مواد الإدخال الفعلي غير المصروفة عند الاعتماد (ديزل/وقود): تُخصم من مخزن المساعدة عند الإقفال
            if (actualAux != null)
                foreach (var aa in actualAux.Where(a => a.OrderId == order.Id && a.Qty > 0 && !order.Materials.Any(m => m.MaterialId == a.MaterialId)))
                {
                    // §1.50.72 P2-3: نفس حارس الرصيد السالب — الإدخال الفعلي غير المصروف
                    // كان يخصم WAUX مباشرة ويمكن أن يسلبه.
                    var bb = Db.StockBalances.FirstOrDefault(s => s.WarehouseId == whAux && s.MaterialId == aa.MaterialId && s.ProductId == null && s.LotId == null && s.CustomerId == null && s.PackagingTypeId == null);
                    double bbBefore = bb?.QtyKg ?? 0;
                    if (bbBefore - aa.Qty < -0.001)
                    {
                        string aaName = Db.AuxiliaryMaterials.AsNoTracking().Where(m => m.Id == aa.MaterialId).Select(m => m.MaterialNameAr).FirstOrDefault() ?? $"#{aa.MaterialId}";
                        throw new DomainException(
                            $"⛔ تعذر إقفال اليوم: رصيد الصنف المساعد «{aaName}» في مخزن المساعدة ({bbBefore:N1}) لا يغطي الإدخال الفعلي ({aa.Qty:N1}).\nقيّد صرفه مسبقاً (أو تسوية) ثم أقرِل اليوم.",
                            "AUX_NEGATIVE_BALANCE");
                    }
                    if (bb == null) { bb = new StockBalance { WarehouseId = whAux, MaterialId = aa.MaterialId, LotId = null, CustomerId = null, PackagingTypeId = null }; Db.StockBalances.Add(bb); }
                    bb.QtyKg -= aa.Qty;
                    Db.InventoryTransactions.Add(new InventoryTransaction
                    {
                        TxnNumber = Numbering.Next("TXN"), WarehouseId = whAux, MaterialId = aa.MaterialId,
                        MovementType = MovementType.Outbound, QtyKg = -aa.Qty,
                        ReferenceDocType = ReferenceDocType.MaterialIssue, ReferenceDocNumber = exe.DocumentNumber,
                        OrderId = order.Id, IsApproved = true,
                        Notes = "صرف فعلي غير مصروف مسبقاً عند إقفال يوم الإنتاج"
                    });
                }
            Db.SaveChanges();

            // ═══════════════════════════════════════════════════════════════════
            // §B95 — توريد الكرتون الفارغ الناتج عن تفريغ الخام (مُرحَّل من المسار المحذوف):
            // الفعلي المؤكد إن أُدخل وإلا فتقدير النظام من الخام المصروف لكل نوع تعبئة.
            // ═══════════════════════════════════════════════════════════════════
            var cartonSvc = new CartonService(Db, Session, Numbering);
            const int NoPack = int.MinValue;
            var estByPack = new Dictionary<int, double>();
            foreach (var kvLot in takeByLot)
            {
                int packKey = Db.Lots.AsNoTracking().Where(l => l.Id == kvLot.Key).Select(l => l.PackagingTypeId).FirstOrDefault() ?? NoPack;
                int lotProd = Db.Lots.AsNoTracking().Where(l => l.Id == kvLot.Key).Select(l => l.ProductId).FirstOrDefault();
                double w = CartonService.RawCartonWeight(Db, kvLot.Key, lotProd);
                estByPack[packKey] = (estByPack.TryGetValue(packKey, out var pv) ? pv : 0) + (w > 0 ? kvLot.Value / w : 0);
            }
            if (emptyCartonsActual != null)
            {
                int domKey = estByPack.Count > 0
                    ? estByPack.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault()
                    : NoPack;
                cartonSvc.PostEmptyCartons(emptyCartonsActual.Value, exe.DocumentNumber, cartonWarehouseId, domKey == NoPack ? null : domKey);
            }
            else
                foreach (var kv in estByPack)
                    cartonSvc.PostEmptyCartons(Math.Round(kv.Value), exe.DocumentNumber, cartonWarehouseId, kv.Key == NoPack ? null : kv.Key);

            PlanSync.SyncProduced(Db, order.Id);
            // §B103 — علة مُصلَحة: إعادة احتساب الحجز أدناه تقرأ من القاعدة باستعلام SQL،
            // وقبل هذا الحفظ كانت ترى المنتَج صفراً فيبقى الحجز كاملاً بعد الإقفال الجزئي.
            Db.SaveChanges();

            // §B85/H1: تحديث الحجز المخزن بعد الإنتاج — كان يبقى مرتفعاً حتى الإقفال النهائي فيُخفي المتاح عن الخطط
            if (order.SourcePlanId is int srcPlanId)
            {
                var srcPlan = Db.ProductionPlans.Include(p => p.Items).FirstOrDefault(p => p.Id == srcPlanId);
                if (srcPlan != null) ApplyReservationsViaPlanning(srcPlan);
            }
            else
            {
                RefreshLotReservations(order.Items.Where(i => i.LotId != null).Select(i => i.LotId.Value));
            }

            // §جودة التمور: الإرسال للفحص — النتيجة متوقعة بعد يومَي تبريد (العيب لا يظهر إلا بعد أن يبرد المنتج)
            if (sendToQuality)
            {
                Db.QualityChecks.Add(new QualityCheck
                {
                    DocumentNumber = Numbering.Next("QC"),
                    OrderId = order.Id,
                    ExecutionId = exe.Id,
                    CheckDate = DateTime.Now,
                    CheckType = "نهائي — بعد التبريد (يومان)",
                    TotalCheckedKg = producedKg,
                    ExpectedCheckDate = DateTime.Today.AddDays(2),
                    Status = DocStatuses.Submitted
                });
            }
            Db.SaveChanges();
            var finalExecution = Db.ProductionExecutions.AsNoTracking()
                .FirstOrDefault(e => e.Id == exe.Id && e.OrderId == orderId);
            ExecutionCloseTrace.Write($"CloseProductionDay FINAL OrderId={orderId} ExecutionId={finalExecution?.Id.ToString() ?? "<null>"} IsDayClosed={finalExecution?.IsDayClosed.ToString() ?? "<null>"} Status={finalExecution?.Status ?? "<null>"} EndDateTime={finalExecution?.EndDateTime?.ToString("O") ?? "<null>"}");
            if (finalExecution == null || !finalExecution.IsDayClosed
                || finalExecution.Status != DocStatuses.Completed || finalExecution.EndDateTime == null)
                throw new DomainException(
                    "تم إيقاف العملية: سجل التنفيذ لم يثبت كإقفال مكتمل في قاعدة البيانات.",
                    "EXECUTION_CLOSE_NOT_PERSISTED");

            // لا تُقفل الخطة عند تسجيل الفعلي. إقفالها يحدث فقط عند تحرير
            // أمر تسليم الإنتاج من إدارة الإنتاج (ProductionDeliveryService.IssueDelivery).
            string planMsg = "";

            // §B88/M13: تفصيل البنود في رسالة النجاح عند الإقفال متعدد البنود
            string itemsMsg = "";
            if (perItem && itemTake!.Count > 1)
            {
                var parts = new List<string>();
                foreach (var kv in itemTake)
                {
                    var oi = order.Items.First(i => i.Id == kv.Key);
                    string pName = Db.Products.AsNoTracking().Where(x => x.Id == oi.ProductId).Select(x => x.ProductNameAr).FirstOrDefault() ?? $"صنف #{oi.ProductId}";
                    parts.Add($"{pName}: {kv.Value.kg:N1} كجم ({kv.Value.boxes:N0} كرتون)");
                }
                itemsMsg = "\n📦 تفصيل البنود: " + string.Join("؛ ", parts) + ".";
            }

            // §B86/L4: صياغة صادقة — المتبقي يعود لخام دفعته ولا يرتبط تلقائياً بخطة الغد
            string carryMsg = exe.CarryToNextDay
                ? $"\n⏪ المتبقي في الصالة {remainingInHall:N1} كجم أُعيد لخام دفعته — أعد تخطيطه يدوياً في خطة اليوم التالي."
                : (remainingInHall > 0 ? $"\nالمتبقي في الصالة: {remainingInHall:N1} كجم (أُعيد لخام الدفعة)." : "");
            // §1.50.72 P3-6: تنبيه صريح في نتيجة الإقفال عندما أُسقط الخام «بالمخطط»
            string rawMsg = consumedRawKg > 0 ? "" : "\n⚠ خام المستهلك رُحِّل بالقيمة المخططة (لم يُدخل الفعلي) — راجع دفتر الخام.";
            string qMsg = sendToQuality
                ? $"\n🔬 أُرسل للجودة — الفحص متوقع {DateTime.Today.AddDays(2):dd/MM/yyyy} (فترة تبريد يومان)." +
                  "\nيُسمح بالتسليم لمخزن التام الآن؛ تسليم العميل بانتظار اعتماد الفحص."
                : "";
            return OpResult.Success(
                $"🔒 أُقفل يوم الإنتاج للأمر {order.DocumentNumber}: المنتَج {producedKg:N1} كجم ({producedCartons:N0} كرتون)" +
                $" | حشف {hashfKg:N1} | نوى {nawaKg:N1} | هالك {wastageKg:N1} | خام مستهلك {consumed:N1}." +
                rawMsg + carryMsg + qMsg + planMsg + itemsMsg + yieldMsg, exe.Id, exe.DocumentNumber);
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    // §B95 — حُذف ClosePlanItems نهائياً (كان ~320 سطراً): مسار إقفال موازٍ ميت —
    // لا تستدعيه أي شاشة — يكرر منطق الإقفال اليومي برياضيات مختلفة. المسار الرسمي
    // الوحيد هو CloseProductionDay عبر أمر الإنتاج، وقد استوعب منه القيمتين
    // الوحيدتين: تسوية المواد المساعدة (SettleAuxMaterials) وتوريد الكرتون الفارغ
    // (PostEmptyCartonsForClose). جداول PlanClosing* باقية للقراءة التاريخية فقط.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>إعادة احتساب الحجوزات عبر خدمة التخطيط (تفادياً لازدواج المنطق).</summary>
    private void ApplyReservationsViaPlanning(ProductionPlan plan)
        => RefreshLotReservations(plan.Items.Where(i => i.LotId != null).Select(i => i.LotId.Value));

    /// <summary>§B85/H1: إعادة احتساب الحجز المخزن للدفعات — تُستدعى بعد كل إقفال (يوم/بنود).</summary>
    private void RefreshLotReservations(IEnumerable<int> lotIds)
    {
        // الحجز = مجموع (المخطط − المنتَج) في الخطط النشطة
        foreach (var lid in lotIds.Distinct())
        {
            var lot = Db.Lots.FirstOrDefault(l => l.Id == lid);
            if (lot == null) continue;
            double reserved = Db.ProductionPlanItems
                .Where(i => i.LotId == lid)
                .Join(Db.ProductionPlans, i => i.PlanId, p => p.Id, (i, p) => new { i, p })
                .Where(x => x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed)
                .Where(x => !x.i.IsClosed)
                .Select(x => x.i).RemainingKg().Sum();
            lot.ReservedQtyKg = Math.Max(0, reserved);
        }
    }
}

/// <summary>§7 — فحص الجودة مع منع تكرار الفحص لنفس الجلسة (§8).</summary>
public class QualityService : ServiceBase, IQualityService
{
    protected override System.Data.IsolationLevel TransactionIsolation => System.Data.IsolationLevel.Serializable;
    private readonly IAuditService _audit;

    public QualityService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering, IAuditService audit)
        : base(db, session, numbering)
    {
        _audit = audit;
    }

    public OpResult SaveCheck(int? orderId, int? executionId, string checkDate, string checkType, List<QualityItemDto> items,
        List<(int byProductId, double qtyKg)> byProducts = null, QualityLabDto lab = null)
    {
        Require("quality", "Create");
        if (items == null || items.Count == 0) return OpResult.Fail("أدخل بنداً واحداً على الأقل في الفحص.");
        return RunOp(() =>
        {
            if (executionId != null && Db.QualityChecks.Any(c => c.ExecutionId == executionId && c.IsApproved))
                throw new DomainException("يوجد فحص معتمد مسبقاً لجلسة التنفيذ هذه — لا يسمح بتكرار الفحص.");
            var check = executionId != null
                ? Db.QualityChecks.Include(c => c.Items).FirstOrDefault(c => c.ExecutionId == executionId && !c.IsApproved)
                : null;
            var order = Db.ProductionOrders.AsNoTracking().Include(o => o.Items).FirstOrDefault(o => o.Id == orderId);
            if (orderId != null && order == null)
                throw new DomainException("أمر التشغيل غير موجود — تحقق من رقم الأمر.");
            if (order != null && order.Items.Sum(i => i.ProducedQtyKg) <= 0)
                throw new DomainException($"لا يوجد إنتاج مسجل لأمر التشغيل {order.DocumentNumber} — لا يمكن الفحص قبل تسجيل الإنتاج.");
            if (executionId != null && !Db.ProductionExecutions.AsNoTracking()
                .Any(e => e.Id == executionId && e.OrderId == orderId && e.IsDayClosed))
                throw new DomainException("جلسة الإنتاج المرتبطة بالفحص لم تُقفل بعد أو لا تتبع أمره.", "QC_SOURCE");
            // §B95 — الفحص المعتمد مقفل: لا تعديل إلا عبر «تصحيح معتمد» بسبب مسجل
            if (check != null && check.IsApproved)
                throw new DomainException("الفحص معتمد — لا يسمح بالتعديل إلا عبر «تصحيح معتمد» بسبب مسجل.");
            if (check == null)
            {
                check = new QualityCheck
                {
                    DocumentNumber = Numbering.Next("QC"),
                    OrderId = orderId,
                    ExecutionId = executionId,
                    Status = DocStatuses.Draft
                };
                Db.QualityChecks.Add(check);
            }
            else
            {
                // استكمال فحص الإقفال المعلَّق: استبدال بنوده المؤقتة بالنتيجة الفعلية
                if (check.Items.Count > 0) Db.QualityCheckItems.RemoveRange(check.Items);
                check.Items.Clear();
                check.OrderId = orderId;
            }
            check.CheckDate = UiFormat.TryParseDate(checkDate, out var d) ? d : DateTime.Now;
            check.CheckType = checkType ?? "نهائي";
            // §إصلاح: تاريخ الفحص المتوقع كان يُملأ من مسار الإقفال فقط، فيظهر فارغاً
            // في رسالة تسليم التام عندما يُنشأ الفحص من شاشة الجودة مباشرة.
            if (check.ExpectedCheckDate == null)
                check.ExpectedCheckDate = (UiFormat.TryParseDate(checkDate, out var cd2) ? cd2 : DateTime.Today).AddDays(2);
            foreach (var it in items)
            {
                if (it.AcceptedQtyKg < 0 || it.RejectedQtyKg < 0) throw new DomainException("الكميات لا يمكن أن تكون سالبة.");
                // §B95 — الكراتين (وحدة التام الأساسية): لا سالب
                if (it.CheckedCartons < 0 || it.AcceptedCartons < 0 || it.RejectedCartons < 0)
                    throw new DomainException("عدد الكراتين لا يمكن أن يكون سالباً.");
                // §B95 — معادلة التلخيص (1000 = 900 + 80 + 20): المفحوص = مقبول + مرفوض —
                // يُشتق تلقائياً عند إغفاله (توافقاً مع الإدخالات القديمة) ويُفرض عند إدخاله
                string eqName = Db.Products.AsNoTracking().Where(p => p.Id == it.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? $"#{it.ProductId}";
                if (it.CheckedQtyKg <= 0) it.CheckedQtyKg = it.AcceptedQtyKg + it.RejectedQtyKg;
                else if (Math.Abs(it.CheckedQtyKg - (it.AcceptedQtyKg + it.RejectedQtyKg)) > 0.01)
                    throw new DomainException($"⛔ معادلة الفحص مختلة للصنف «{eqName}»: المفحوص ({it.CheckedQtyKg:N1} كجم) ≠ مقبول ({it.AcceptedQtyKg:N1}) + مرفوض ({it.RejectedQtyKg:N1}).");
                if (it.CheckedCartons <= 0) it.CheckedCartons = it.AcceptedCartons + it.RejectedCartons;
                else if (Math.Abs(it.CheckedCartons - (it.AcceptedCartons + it.RejectedCartons)) > 0.001)
                    throw new DomainException($"⛔ معادلة الفحص مختلة للصنف «{eqName}»: المفحوص ({it.CheckedCartons:N0} كرتون) ≠ مقبول ({it.AcceptedCartons:N0}) + مرفوض ({it.RejectedCartons:N0}).");

                // §نظام الوحدات: بنود الفحص منتجات تامة فقط (المجموعة 002)
                UnitsPolicy.RequireItemType(Db, it.ProductId, "Finished", "بند فحص الجودة");

                // §قاعدة الوحدات: الإنتاج التام بالكرتون والكيلو وزن مكافئ يُشتق من تعريف العبوة.
                // الجودة كانت المرحلة الوحيدة التي لا تربط الرقمين، فيمرّ محضر بكراتين
                // وكيلو متناقضين (مقبول 100 كرتون = 750 كجم مع تسجيل 500 كجم) — والمحضر
                // مصدر سقف التسليم، فيتسرب الخلل إلى التام وسند العميل.
                ProductionOrderItem orderItem = null;
                if (order != null)
                {
                    // Differentiate a foreign product from an ambiguous/mistyped
                    // line. Both are rejected, but the operator must know which
                    // identity to correct rather than guessing an order row.
                    if (!order.Items.Any(oi => oi.ProductId == it.ProductId))
                        throw new DomainException($"⛔ الصنف «{eqName}» ليس من بنود أمر الإنتاج {order.DocumentNumber}.", "FOREIGN_PRODUCT");
                    var candidates = order.Items.Where(oi => oi.ProductId == it.ProductId
                        && (it.OrderItemId == null || oi.Id == it.OrderItemId)
                        && (it.LotId == null || oi.LotId == it.LotId)
                        && (it.PackagingTypeId == null || oi.PackagingTypeId == it.PackagingTypeId)
                        && (it.CustomerId == null || (oi.CustomerId ?? order.CustomerId) == it.CustomerId)).ToList();
                    if (candidates.Count != 1)
                        throw new DomainException(candidates.Count == 0
                            ? "هوية بند الفحص/الدفعة/العبوة لا تتطابق مع أمر الإنتاج — لا بديل ضمنياً بالصنف."
                            : "يوجد أكثر من بند للصنف نفسه؛ حدّد معرّف بند أمر الإنتاج والعبوة في الفحص.",
                            "QC_LINE_IDENTITY");
                    orderItem = candidates[0];
                    it.OrderItemId = orderItem.Id;
                    it.CustomerId = orderItem.CustomerId ?? order.CustomerId;
                    it.LotId = orderItem.LotId;
                    it.PackagingTypeId = orderItem.PackagingTypeId;
                }
                int? packOfItem = it.PackagingTypeId;
                double ctnW = UnitsPolicy.CartonWeight(Db, it.ProductId, packOfItem);
                if (ctnW > 0)
                {
                    // الكراتين مُدخَلة ⟵ الكيلو يجب أن يطابقها (لكل مقدار على حدة)
                    it.AcceptedQtyKg = UnitsPolicy.EnsureCartonKgConsistency(Db, it.ProductId, packOfItem,
                        it.AcceptedQtyKg, (int)Math.Round(it.AcceptedCartons), "بند فحص الجودة — المقبول");
                    it.RejectedQtyKg = UnitsPolicy.EnsureCartonKgConsistency(Db, it.ProductId, packOfItem,
                        it.RejectedQtyKg, (int)Math.Round(it.RejectedCartons), "بند فحص الجودة — المرفوض");
                    it.CheckedQtyKg = UnitsPolicy.EnsureCartonKgConsistency(Db, it.ProductId, packOfItem,
                        it.CheckedQtyKg, (int)Math.Round(it.CheckedCartons), "بند فحص الجودة — المفحوص");
                    // الكيلو وحده مُدخَل ⟵ تُشتق الكراتين فلا يبقى المحضر بلا وحدته الأساسية
                    if (it.AcceptedCartons <= 0 && it.AcceptedQtyKg > 0) it.AcceptedCartons = Math.Round(it.AcceptedQtyKg / ctnW, 2);
                    if (it.RejectedCartons <= 0 && it.RejectedQtyKg > 0) it.RejectedCartons = Math.Round(it.RejectedQtyKg / ctnW, 2);
                    if (it.CheckedCartons <= 0 && it.CheckedQtyKg > 0) it.CheckedCartons = Math.Round(it.CheckedQtyKg / ctnW, 2);
                }

                if (orderItem != null && (it.CheckedQtyKg > orderItem.ProducedQtyKg + 0.01
                    || (orderItem.ProducedCartons > 0 && it.CheckedCartons > orderItem.ProducedCartons + 1.001)))
                    throw new DomainException("الكمية المفحوصة تتجاوز الكمية المنتجة لبند أمر الإنتاج المحدد.", "QC_LINE_OVERFLOW");

                // §تتبع الصنف: لا يُفحص خلاص مرتبط بدفعة سكري
                ProductIdentityGuard.EnsureConversionAllowed(Db, it.ProductId, it.LotId);

                check.Items.Add(new QualityCheckItem
                {
                    OrderItemId = it.OrderItemId, CustomerId = it.CustomerId,
                    ProductId = it.ProductId, LotId = it.LotId, PackagingTypeId = it.PackagingTypeId,
                    CheckedQtyKg = it.CheckedQtyKg > 0 ? it.CheckedQtyKg : it.AcceptedQtyKg + it.RejectedQtyKg,
                    AcceptedQtyKg = it.AcceptedQtyKg,
                    RejectedQtyKg = it.RejectedQtyKg,
                    CheckedCartons = it.CheckedCartons > 0 ? it.CheckedCartons : it.AcceptedCartons + it.RejectedCartons,
                    AcceptedCartons = it.AcceptedCartons,
                    RejectedCartons = it.RejectedCartons,
                    Notes = it.Notes
                });
            }

            // §B95 — سقف المنتَج لكل صنف (كراتين إن سُجلت + كيلو) ثم تحديد حالة المحضر
            if (order != null)
            {
                foreach (var line in check.Items.GroupBy(i => i.OrderItemId))
                {
                    var producedLine = order.Items.Single(i => i.Id == line.Key);
                    var otherChecked = Db.QualityCheckItems.AsNoTracking()
                        .Where(i => i.CheckId != check.Id && i.OrderItemId == line.Key)
                        .Join(Db.QualityChecks.AsNoTracking(), i => i.CheckId, c => c.Id,
                            (i, c) => new { Item = i, Check = c })
                        .Where(x => x.Check.OrderId == order.Id)
                        .Select(x => x.Item).ToList();
                    double checkKg = line.Sum(i => i.CheckedQtyKg) + otherChecked.Sum(i => i.CheckedQtyKg);
                    double checkCartons = line.Sum(i => i.CheckedCartons) + otherChecked.Sum(i => i.CheckedCartons);
                    if (checkKg > producedLine.ProducedQtyKg + 0.01
                        || (producedLine.ProducedCartons > 0 && checkCartons > producedLine.ProducedCartons + 1.001))
                        throw new DomainException(otherChecked.Count > 0
                            ? $"بند الأمر #{producedLine.Id} مغطى بفحص سابق؛ مجموع الفحوصات تجاوز الكمية المنتجة."
                            : $"فحوصات بند الأمر #{producedLine.Id} تجاوزت الكمية المنتجة.", "QC_LINE_OVERFLOW");
                }
                foreach (var g in check.Items.GroupBy(i => i.ProductId))
                {
                    double producedKg = order.Items.Where(i => i.ProductId == g.Key).Sum(i => i.ProducedQtyKg);
                    int producedCtn = order.Items.Where(i => i.ProductId == g.Key).Sum(i => i.ProducedCartons);
                    double checkedKg = g.Sum(i => i.CheckedQtyKg);
                    double checkedCtn = g.Sum(i => i.CheckedCartons);
                    string pname = Db.Products.AsNoTracking().Where(p => p.Id == g.Key).Select(p => p.ProductNameAr).FirstOrDefault() ?? $"#{g.Key}";
                    if (checkedCtn > 0 && producedCtn > 0 && checkedCtn > producedCtn + 1.001)   // §سحب: تسامح ±1 كرتون — تدوير اشتقاق الكراتين من الكيلو
                        throw new DomainException($"⛔ نتيجة الفحص للصنف «{pname}» ({checkedCtn:N0} كرتون) تتجاوز الكمية المنتجة ({producedCtn:N0} كرتون).");
                    if (checkedKg > producedKg + 0.01)
                        throw new DomainException($"⛔ نتيجة الفحص للصنف «{pname}» ({checkedKg:N1} كجم) تتجاوز الكمية المنتجة ({producedKg:N1} كجم).");
                    // §B95 — منع التغطية المزدوجة: مجموع فحوصات الأمر للصنف لا يتجاوز إنتاجه (الحالي مستثنى لأنه يُستبدل)
                    double otherCheckedKg = Db.QualityCheckItems.AsNoTracking()
                        .Where(i => i.CheckId != check.Id && i.ProductId == g.Key)
                        .Join(Db.QualityChecks.AsNoTracking(), i => i.CheckId, c => c.Id, (i, c) => new { i, c })
                        .Where(x => x.c.OrderId == order.Id)
                        .Sum(x => x.i.CheckedQtyKg);
                    if (otherCheckedKg > 0.01 && otherCheckedKg + checkedKg > producedKg + 0.01)
                        throw new DomainException($"⛔ الصنف «{pname}» مغطى بفحص سابق ({otherCheckedKg:N1} كجم) — مجموع الفحوصات ({otherCheckedKg + checkedKg:N1} كجم) يتجاوز الإنتاج ({producedKg:N1} كجم). أكمل الفحص السابق بدل إنشاء فحص مكرر.");
                }
                double totCheckedKg = check.Items.Sum(i => i.CheckedQtyKg);
                double totProducedKg = order.Items.Sum(i => i.ProducedQtyKg);
                double totCheckedCtn = check.Items.Sum(i => i.CheckedCartons);
                int totProducedCtn = order.Items.Sum(i => i.ProducedCartons);
                // §B95 — الاكتمال ذاتي (المحضر يغطي الإنتاج وحده) أو تراكمي (فحوصات الأمر مجتمعة تغطيه):
                // بلا التراكمي يستحيل اعتماد الفحص الجزئي الثاني أبداً فيجمُد الأمر — والتغطية المزدوجة ممنوعة أصلاً أعلاه
                var otherCov = Db.QualityCheckItems.AsNoTracking()
                    .Where(i => i.CheckId != check.Id)
                    .Join(Db.QualityChecks.AsNoTracking(), i => i.CheckId, c => c.Id, (i, c) => new { i, c })
                    .Where(x => x.c.OrderId == order.Id)
                    .GroupBy(x => 1)
                    .Select(g => new { Kg = g.Sum(x => x.i.CheckedQtyKg), Ctn = g.Sum(x => x.i.CheckedCartons) })
                    .FirstOrDefault();
                double otherKg = otherCov?.Kg ?? 0, otherCtn = otherCov?.Ctn ?? 0;
                double tolKg = Math.Max(0.01, totProducedKg * 0.0001);
                bool kgComplete = Math.Abs(totCheckedKg - totProducedKg) <= tolKg
                    || (otherKg > 0.01 && Math.Abs(otherKg + totCheckedKg - totProducedKg) <= tolKg);
                bool ctnComplete = totProducedCtn > 0 && totCheckedCtn > 0
                    && (Math.Abs(totCheckedCtn - totProducedCtn) <= 0.001
                        || (otherCtn > 0.001 && Math.Abs(otherCtn + totCheckedCtn - totProducedCtn) <= 0.001));
                check.Status = (kgComplete || ctnComplete) ? DocStatuses.Completed : DocStatuses.InProgress;
            }
            else
            {
                check.Status = DocStatuses.Completed; // يدوي بلا أمر: الاكتمال = وجود نتائج مسجلة
            }
            if (!string.IsNullOrWhiteSpace(Session?.UserName))
                check.InspectorName = Session.UserName;

            // §قرار الجودة ومعايير الفحص المخبري والحسي (المواصفة القياسية المعتمدة للتمور)
            if (lab != null)
            {
                if (lab.Decision is not (QualityGate.Passed or QualityGate.Quarantine or QualityGate.Rejected))
                    throw new DomainException("قرار الجودة غير صالح — لا يُفترض مطابق تلقائياً.", "QC_DECISION");
                check.Decision = lab.Decision;
                check.MoisturePct = lab.MoisturePct;
                check.BrixDeg = lab.BrixDeg;
                check.SkinSeparationPct = lab.SkinSeparationPct;
                check.ImpuritiesPct = lab.ImpuritiesPct;
                check.SampleCartons = lab.SampleCartons;
                check.InspectorNotes = lab.InspectorNotes;
            }
            check.TotalCheckedKg = check.Items.Sum(i => i.CheckedQtyKg);
            check.AcceptedKg = check.Items.Sum(i => i.AcceptedQtyKg);
            check.RejectedKg = check.Items.Sum(i => i.RejectedQtyKg);
            check.TotalCheckedCartons = check.Items.Sum(i => i.CheckedCartons);
            check.AcceptedCartons = check.Items.Sum(i => i.AcceptedCartons);
            check.RejectedCartons = check.Items.Sum(i => i.RejectedCartons);
            if (check.Decision == QualityGate.Passed && check.AcceptedKg <= 0.001)
                throw new DomainException("قرار «مطابق» يتطلب كمية مقبولة موجبة — 100% مرفوض لا يُفرَج عنه.", "QC_ZERO_ACCEPTED");
            Db.SaveChanges();

            if (byProducts != null)
                foreach (var (bpId, qty) in byProducts)
                {
                    // §نظام الوحدات: المخرجات الثانوية (003) أصناف ثانوية معرفة وبالكيوجرام فقط — لا كراتين
                    if (!Db.Products.Any(b => b.Id == bpId && b.ItemType == "ByProduct"))
                        throw new DomainException("الصنف الثانوي غير موجود في بطاقة الأصناف.");
                    if (qty < 0) throw new DomainException("كمية المخرج الثانوي لا يمكن أن تكون سالبة.");
                    Db.QualityByProductRecords.Add(new QualityByProductRecord { CheckId = check.Id, ByProductId = bpId, QtyKg = qty });
                }

            Db.SaveChanges();
            return OpResult.Success($"تم حفظ فحص الجودة {check.DocumentNumber} — مقبول {check.AcceptedKg:N1} كجم — الحالة: {QualityCheckStatuses.ToArabic(check.Status)}.", check.Id, check.DocumentNumber);
        });
    }

    /// <summary>§B95 — التغطية التراكمية لأمر: مجموع مفحوص كل فحوصاته يغطي إنتاجه (كيلو، أو كراتين إن سُجلت).</summary>
    private bool OrderFullyChecked(int orderId)
    {
        var o = Db.ProductionOrders.AsNoTracking().Include(x => x.Items).FirstOrDefault(x => x.Id == orderId);
        if (o == null) return false;
        double producedKg = o.Items.Sum(i => i.ProducedQtyKg);
        int producedCtn = o.Items.Sum(i => i.ProducedCartons);
        var cids = Db.QualityChecks.AsNoTracking().Where(c => c.OrderId == orderId).Select(c => c.Id).ToList();
        if (cids.Count == 0) return false;
        double checkedKg = Db.QualityCheckItems.AsNoTracking().Where(i => cids.Contains(i.CheckId)).Sum(i => i.CheckedQtyKg);
        double checkedCtn = Db.QualityCheckItems.AsNoTracking().Where(i => cids.Contains(i.CheckId)).Sum(i => i.CheckedCartons);
        bool kgOk = Math.Abs(checkedKg - producedKg) <= Math.Max(0.01, producedKg * 0.0001);
        bool ctnOk = producedCtn > 0 && checkedCtn > 0 && Math.Abs(checkedCtn - producedCtn) <= 0.001;
        return kgOk || ctnOk;
    }

    public OpResult ApproveCheck(int checkId)
    {
        Require("quality", "Approve");
        return RunOp(() =>
        {
            var check = Db.QualityChecks.Include(c => c.Items).FirstOrDefault(c => c.Id == checkId)
                ?? throw new DomainException("الفحص غير موجود.");
            if (check.IsApproved) throw new DomainException("الفحص معتمد مسبقاً.");
            if (check.Items.Count == 0 || check.Items.Sum(i => i.CheckedQtyKg) <= 0.001)
                throw new DomainException("لا يُعتمد محضر بلا بنود مفحوصة.", "QC_EMPTY");
            if (check.Decision is not (QualityGate.Passed or QualityGate.Rejected or QualityGate.Quarantine))
                throw new DomainException("قرار الفحص غير صالح.", "QC_DECISION");
            if (check.Decision == QualityGate.Passed && check.Items.Sum(i => i.AcceptedQtyKg) <= 0.001)
                throw new DomainException("لا يمكن اعتماد قرار «مطابق» بمقبول صفر — البضاعة مرفوضة/محجوزة.", "QC_ZERO_ACCEPTED");
            if (check.Status != DocStatuses.Completed)
            {
                if (check.OrderId == null || !OrderFullyChecked(check.OrderId.Value))
                    throw new DomainException(
                        $"⛔ لا يمكن اعتماد المحضر — حالته «{QualityCheckStatuses.ToArabic(check.Status)}» وليس «مكتملاً».\n" +
                        "الاعتماد يتطلب تغطية كامل الكمية المنتجة بنتائج الفحص (مطابق + غير مطابق + مرفوض = المنتَج).");
                check.Status = DocStatuses.Completed;
            }
            check.IsApproved = true;
            check.Status = DocStatuses.Approved;
            check.ApprovedBy = Session?.UserId;
            check.ApprovedDate = Db.BusinessNow;
            Db.SaveChanges();
            if (check.OrderId != null) PlanSync.SyncAccepted(Db, check.OrderId.Value);
            Db.SaveChanges();
            string decision = check.Decision switch
            {
                QualityGate.Quarantine => " (قرار: حجز وتحريز مؤقت)",
                QualityGate.Rejected => " (قرار: مرفوض/عوادم)", _ => ""
            };
            // Cooling and laboratory thresholds are ADVISORY by business policy.
            string cooling = check.ExpectedCheckDate?.Date > Db.BusinessNow.Date
                ? " ⚠ قبل اكتمال يومَي التبريد؛ الملاحظة استرشادية وليست مانعاً." : "";
            var labRows = Db.QualityStandardRecords.AsNoTracking()
                .Where(r => r.CheckId == check.Id)
                .Join(Db.QualityStandards.AsNoTracking(), r => r.StandardId, s => s.Id,
                    (r, s) => new { r.Value, s.MinValue, s.MaxValue })
                .ToList();
            string lab = labRows.Any(x => (x.MinValue != null && x.Value < x.MinValue)
                                            || (x.MaxValue != null && x.Value > x.MaxValue))
                ? " ⚠ بعض القياسات خارج الحدود الاسترشادية؛ راجع المحضر." : "";
            return OpResult.Success($"تم اعتماد فحص الجودة{decision}.{cooling}{lab}", check.Id, check.DocumentNumber);
        });
    }

    /// <summary>
    /// §B95 — تصحيح معتمد على فحص معتمد: يتطلب صلاحية (الجودة/تعديل بعد الاعتماد)
    /// وسبباً مكتوباً يُسجَّل في التدقيق مع المستخدم والوقت — ثم يُعاد الفحص «قيد الفحص» للتعديل.
    /// </summary>
    public OpResult RequestCorrection(int checkId, string reason)
    {
        Require("quality", "EditAfterApproval");
        if (string.IsNullOrWhiteSpace(reason))
            return OpResult.Fail("التصحيح المعتمد يتطلب سبباً مكتوباً يُسجَّل في التدقيق.");
        return RunOp(() =>
        {
            var check = Db.QualityChecks.Include(c => c.Items).FirstOrDefault(c => c.Id == checkId)
                ?? throw new DomainException("الفحص غير موجود.");
            if (!check.IsApproved)
                throw new DomainException("الفحص غير معتمد — عدّله بالحفظ العادي.");
            // Policy: a shipped lot cannot have its release retrospectively
            // withdrawn. First reverse/settle EVERY customer delivery covering
            // this check's precise identity (including package and customer).
            var released = check.Items.Where(q => q.AcceptedQtyKg > 0.001).ToList();
            var blocking = Db.CustomerDeliveries.AsNoTracking().Include(d => d.Items)
                .Where(d => d.IsApproved)
                .ToList()
                .FirstOrDefault(d => released.Any(q => d.Items.Any(i => i.ProductId == q.ProductId
                    && i.PackagingTypeId == q.PackagingTypeId && i.LotId == q.LotId
                    && (q.CustomerId == null || d.CustomerId == q.CustomerId)
                    && (q.LotId != null || d.OrderId == check.OrderId))));
            if (blocking != null)
                throw new DomainException($"لا يمكن تصحيح الفحص {check.DocumentNumber}: توجد شحنة عميل معتمدة {blocking.DocumentNumber} من نفس البند/الدفعة/العبوة. سوِّ الشحنة أو ألغِ اعتمادها بقيد المرتجع أولاً.",
                    "QC_SHIPPED_UNSETTLED");
            check.IsApproved = false;
            check.Status = DocStatuses.InProgress;
            Db.QualityCorrections.Add(new QualityCorrection
            {
                CheckId = check.Id, Reason = reason.Trim(), CorrectedBy = Session?.UserId,
                CorrectedByName = Session?.UserName, CorrectedDate = Db.BusinessNow
            });
            Db.SaveChanges();
            if (check.OrderId != null) PlanSync.SyncAccepted(Db, check.OrderId.Value);
            Db.SaveChanges();
            _audit.Log("الفحص والجودة", "تصحيح معتمد", "QualityCheck", check.DocumentNumber, check.Id,
                new { الحالة_السابقة = "معتمد" }, new { الحالة_الجديدة = "قيد الفحص", السبب = reason.Trim() });
            return OpResult.Success($"فُتح المحضر {check.DocumentNumber} للتصحيح، وأُزيل المقبول مؤقتاً من الخطة وبوابة الشحن حتى إعادة الاعتماد.");
        });
    }

}


/// <summary>مزامنة تقدم بنود الخطة من أوامر الإنتاج والتسليمات.</summary>
public static class PlanSync
{
    /// <summary>المنتَج: يوزع إنتاج بنود الأمر على بنود الخطة المرتبطة ويحدّث حالة التنفيذ.</summary>
    public static void SyncProduced(DatesErp.Infrastructure.Persistence.DatesErpDbContext db, int orderId)
    {
        var orderItems = db.ProductionOrderItems.Where(i => i.OrderId == orderId && i.PlanItemId != null).ToList();
        foreach (var oi in orderItems)
        {
            var pi = db.ProductionPlanItems.FirstOrDefault(x => x.Id == oi.PlanItemId);
            if (pi == null) continue;
            pi.ProducedQtyKg = db.ProductionOrderItems.Where(x => x.PlanItemId == pi.Id).Sum(x => x.ProducedQtyKg);
            UpdateStatus(pi);
        }
    }

    /// <summary>المقبول: من فحوصات الجودة المعتمدة عبر بنود الأمر.</summary>
    public static void SyncAccepted(DatesErp.Infrastructure.Persistence.DatesErpDbContext db, int orderId)
    {
        var impacted = db.ProductionOrderItems.AsNoTracking()
            .Where(i => i.OrderId == orderId && i.PlanItemId != null)
            .Select(i => i.PlanItemId!.Value).Distinct().ToList();
        foreach (var planItemId in impacted)
        {
            var pi = db.ProductionPlanItems.FirstOrDefault(x => x.Id == planItemId);
            if (pi == null) continue;
            var allOrderLines = db.ProductionOrderItems.AsNoTracking()
                .Where(i => i.PlanItemId == planItemId).ToList();
            var orderIds = allOrderLines.Select(i => i.OrderId).Distinct().ToList();
            var approved = db.QualityCheckItems.AsNoTracking()
                .Join(db.QualityChecks.AsNoTracking(), q => q.CheckId, c => c.Id,
                    (q, c) => new { Item = q, Check = c })
                .Where(x => x.Check.IsApproved && x.Check.OrderId != null
                    && orderIds.Contains(x.Check.OrderId.Value))
                .ToList();
            double accepted = 0;
            foreach (var line in allOrderLines)
            {
                bool uniqueLegacyLine = db.ProductionOrderItems.AsNoTracking().Count(i => i.OrderId == line.OrderId
                    && i.ProductId == line.ProductId && i.LotId == line.LotId
                    && i.PackagingTypeId == line.PackagingTypeId
                    && i.CustomerId == line.CustomerId) == 1;
                accepted += approved.Where(x => x.Check.OrderId == line.OrderId
                    && (x.Item.OrderItemId == line.Id
                        || (x.Item.OrderItemId == null && uniqueLegacyLine
                            && x.Item.ProductId == line.ProductId && x.Item.LotId == line.LotId
                            && x.Item.PackagingTypeId == line.PackagingTypeId
                            && (x.Item.CustomerId == null || x.Item.CustomerId == line.CustomerId))))
                    .Sum(x => x.Item.AcceptedQtyKg);
            }
            pi.AcceptedQtyKg = accepted;
            UpdateStatus(pi);
        }
    }

    /// <summary>§CD-FIX: المسلَّم يوزع فقط داخل الخطة والدفعة وهوية الصنف/العميل/العبوة.
    /// التوزيع الواسع كان يلوث خططاً أخرى ويُسقط الفائض عن السعة بصمت؛ الفائض يرفض الآن.
    /// إذا تكررت الهوية نفسها في عدة بنود داخل الخطة، توزيع البنود ما زال حسب ترتيب الأيام؛
    /// إلغاء خارج ترتيب الاعتمادات يحتاج سجلاً منفصلاً لتخصيص كل كمية إلى بندها الدقيق.</summary>
    public static void SyncDeliveredForPlan(DatesErp.Infrastructure.Persistence.DatesErpDbContext db, int planId, int customerId, int productId, int? lotId, int? packagingTypeId, double qtyKg, string docNo)
    {
        var remaining = qtyKg;
        var items = db.ProductionPlanItems
            .Where(i => i.PlanId == planId && i.CustomerId == customerId && i.ProductId == productId
                && i.LotId == lotId && i.PackagingTypeId == packagingTypeId && i.ScheduledDate != null)
            .OrderBy(i => i.ScheduledDate).ThenBy(i => i.PriorityNo)
            .ToList();
        foreach (var pi in items)
        {
            if (remaining <= 0) break;
            double slot = pi.PlannedQtyKg - pi.DeliveredQtyKg;
            if (slot <= 0) continue;
            double take = Math.Min(slot, remaining);
            pi.DeliveredQtyKg += take;
            remaining -= take;
            UpdateStatus(pi);
        }
        if (remaining > 0.001)
            throw new DomainException(
                $"⛔ الكمية المسلَّمة في السند {docNo} تتجاوز سعة الخطة بـ {remaining:N1} كجم — لم يُوزَّع الفائض على أي خطة.\n" +
                "راجع سعة الخطة (أو عدّلها رسمياً) قبل الاعتماد.",
                "PLAN_OVERFLOW");
    }

    /// <summary>§CD-FIX: عكس توزيع المسلَّم عند إلغاء الاعتماد (LIFO داخل الخطة/الدفعة).
    /// If the attributed counters cannot cover the reversal, stop atomically rather
    /// than returning stock while leaving a plan discrepancy for manual guesswork.</summary>
    public static void UnsyncDeliveredForPlan(DatesErp.Infrastructure.Persistence.DatesErpDbContext db, int planId, int customerId, int productId, int? lotId, int? packagingTypeId, double qtyKg)
    {
        var remaining = qtyKg;
        var items = db.ProductionPlanItems
            .Where(i => i.PlanId == planId && i.CustomerId == customerId && i.ProductId == productId
                && i.LotId == lotId && i.PackagingTypeId == packagingTypeId && i.ScheduledDate != null)
            .OrderByDescending(i => i.ScheduledDate).ThenByDescending(i => i.Id)
            .ToList();
        foreach (var pi in items)
        {
            if (remaining <= 0) break;
            double take = Math.Min(Math.Max(pi.DeliveredQtyKg, 0), remaining);
            if (take <= 0) continue;
            pi.DeliveredQtyKg -= take;
            remaining -= take;
            UpdateStatus(pi);
        }
        if (remaining > 0.001)
            throw new DomainException(
                $"لا يمكن عكس تسليم الخطة #{planId}: عداد الدفعة #{lotId} / الصنف #{productId} / العبوة #{packagingTypeId} "
                + $"ينقصه {remaining:N1} كجم. لم تُعد الكمية إلى المخزون؛ يلزم فحص وتسوية رسمية.",
                "PLAN_REVERSE_SHORTFALL");
    }

    public static void UpdateStatus(DatesErp.Core.Domain.Entities.ProductionPlanItem pi)
    {
        if (pi.ProducedQtyKg <= 0 && pi.DeliveredQtyKg <= 0) pi.ExecutionStatus = "NotStarted";
        else if (pi.ProducedQtyKg + 0.001 >= pi.PlannedQtyKg && pi.DeliveredQtyKg + 0.001 >= pi.PlannedQtyKg) pi.ExecutionStatus = "Completed";
        else if (pi.DeliveredQtyKg > 0) pi.ExecutionStatus = "Partial";
        else pi.ExecutionStatus = "InProgress";
    }
}

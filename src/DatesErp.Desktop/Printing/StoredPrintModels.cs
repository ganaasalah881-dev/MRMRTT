using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Desktop.Views;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Printing;

/// <summary>Read-only print projections. Saved documents never borrow edited controls or today's document date.</summary>
public static class StoredPrintModels
{
    private static PhaseDocModel Snapshot(DatesErpDbContext db,Func<PhaseDocModel> read)
    {
        using var tx=db.Database.CurrentTransaction==null?db.Database.BeginTransaction(System.Data.IsolationLevel.Serializable):null;
        var model=read();tx?.Commit();return model;
    }
    private static string N(object v)=>PrintLayout.Format(v);
    private static PhaseDocModel Model(DatesErpDbContext db,WorkflowDocument doc,string title)
    {
        var company=db.CompanyInfos.AsNoTracking().OrderBy(x=>x.Id).FirstOrDefault();
        var m=new PhaseDocModel {DocTitle=title,DocNo=doc.DocumentNumber,StatusAr=PrintStatus.Of(doc),Notes=doc.Notes??"",
            CompanyNameAr=company?.CompanyNameAr??"DateERP",CompanyAddress=company?.Address,CompanyPhone=company?.Phone,LogoBytes=company?.LogoBytes,
            FooterNote="نسخة من البيانات المحفوظة؛ تعديلات الشاشة غير المحفوظة لا تظهر هنا. الطباعة لا تعتمد المستند ولا تغيّر المخزون."};
        m.Info.Add(("أنشأه",db.Users.AsNoTracking().Where(u=>u.Id==doc.CreatedBy).Select(u=>u.FullName).FirstOrDefault()??"—"));
        if(doc.ApprovedDate!=null)m.Info.Add(("تاريخ الاعتماد",doc.ApprovedDate.Value.ToString("dd/MM/yyyy HH:mm")));
        return m;
    }
    private static string Product(DatesErpDbContext db,int? id)=>db.Products.AsNoTracking().Where(p=>p.Id==id).Select(p=>p.ProductNameAr).FirstOrDefault()??"—";
    private static string Customer(DatesErpDbContext db,int? id)=>db.Customers.AsNoTracking().Where(p=>p.Id==id).Select(p=>p.CustomerName).FirstOrDefault()??"—";
    private static string Lot(DatesErpDbContext db,int? id)=>db.Lots.AsNoTracking().Where(p=>p.Id==id).Select(p=>p.LotCode).FirstOrDefault()??"—";
    private static string Pack(DatesErpDbContext db,int? id)=>db.PackagingTypes.AsNoTracking().Where(p=>p.Id==id).Select(p=>p.PackageNameAr).FirstOrDefault()??"—";
    private static string ReceiptState(string s)=>s switch {"Full"=>"مستلم بالكامل","Partial"=>"مستلم جزئيًا",_=>"لم يُستلم"};
    public static PhaseDocModel Order(DatesErpDbContext db,int id)
    {
        return Snapshot(db, () =>
        {
        var d=db.ProductionOrders.AsNoTracking().Include(o=>o.Items).Include(o=>o.Materials).Single(o=>o.Id==id);
        var m=Model(db,d,"أمر وتشغيل إنتاج التمور (Work Order)");m.MainTitle="بنود التشغيل والمنتجات التامة المستهدفة";
        m.Info.AddRange(new[]{("خطة الإنتاج",db.ProductionPlans.AsNoTracking().Where(p=>p.Id==d.SourcePlanId).Select(p=>p.DocumentNumber).FirstOrDefault()??"أمر مستقل"),("تاريخ الإنتاج",UiFormat.D(d.ProductionDate)),
            ("الوردية",db.Shifts.AsNoTracking().Where(x=>x.Id==d.ShiftId).Select(x=>x.ShiftNameAr).FirstOrDefault()??"غير محددة"),("خط الإنتاج",db.ProductionLines.AsNoTracking().Where(x=>x.Id==d.LineId).Select(x=>x.LineNameAr).FirstOrDefault()??"غير محدد")});
        m.Columns=new[]{"م","الدفعة","العميل","الخام المستلم","المنتج النهائي","العبوة","المستهدف (كجم)","كرتون","المنفذ (كجم)"};
        int n=1;foreach(var i in d.Items.OrderBy(x=>x.Id))
        {
            int? raw=db.Lots.AsNoTracking().Where(l=>l.Id==i.LotId).Select(l=>(int?)l.ProductId).FirstOrDefault();
            m.Rows.Add(new object[]{n++,Lot(db,i.LotId),Customer(db,i.CustomerId??d.CustomerId),Product(db,raw),Product(db,i.ProductId),Pack(db,i.PackagingTypeId),i.PlannedQtyKg,i.PlannedCartons,i.ProducedQtyKg});
        }
        m.SecondTitle="جدول المواد المساعدة والتغليف المحتسبة آلياً (BOM)";m.SecondColumns=new[]{"المادة","الوحدة","المحتسب","المصروف","المستهلك"};
        foreach(var x in d.Materials.OrderBy(x=>x.Id))m.SecondRows.Add(new object[]{db.AuxiliaryMaterials.AsNoTracking().Where(a=>a.Id==x.MaterialId).Select(a=>a.MaterialNameAr).FirstOrDefault()??"—",x.UnitOfMeasure,x.CalculatedQty,x.ActualIssuedQty,x.ConsumedQty});
        m.Totals.AddRange(new[]{("المستهدف (كجم)",N(d.Items.Sum(x=>x.PlannedQtyKg))),("الكراتين المستهدفة",N(d.Items.Sum(x=>x.PlannedCartons))),("المنفذ (كجم)",N(d.Items.Sum(x=>x.ProducedQtyKg)))});
        m.Signatures.AddRange(new[]{"مشرف صالة الإنتاج","أمين مخزن المواد المساعدة","مدير إدارة الإنتاج / الاعتماد"});return m;

        });
    }

    public static PhaseDocModel CustomerDelivery(DatesErpDbContext db,int id)
    {
        return Snapshot(db, () =>
        {
        var d=db.CustomerDeliveries.AsNoTracking().Include(x=>x.Items).Single(x=>x.Id==id);
        var m=Model(db,d,"سند إخراج وتسليم بضاعة للعميل (Gate Pass)");
        m.MainTitle="بنود البضاعة المُخرَجة والمسلَّمة للعميل";
        m.Info.Add(("العميل",Customer(db,d.CustomerId)));m.Info.Add(("تاريخ التسليم",UiFormat.D(d.DeliveryDate)));
        m.Info.Add(("أمر الإنتاج",db.ProductionOrders.AsNoTracking().Where(o=>o.Id==d.OrderId).Select(o=>o.DocumentNumber).FirstOrDefault()??"—"));
        m.Columns=new[]{"م","الصنف","الدفعة","العبوة","عدد العبوات","وزن الكرتون المسجل (كجم)","الوزن المسلم (كجم)"};
        m.ColumnWeights=new[]{0.4,1.8,1.2,1.2,0.8,1.2,1.2};
        int n=1;foreach(var i in d.Items.OrderBy(i=>i.Id))m.Rows.Add(new object[]{n++,Product(db,i.ProductId),Lot(db,i.LotId),Pack(db,i.PackagingTypeId),i.PackageCount,i.CartonWeightKg>0?N(i.CartonWeightKg):"غير مسجل",i.QtyKg});
        m.Totals.Add(("عدد العبوات",N(d.Items.Sum(i=>i.PackageCount))));m.Totals.Add(("الوزن المسلم (كجم)",N(d.Items.Sum(i=>i.QtyKg))));
        m.Signatures.AddRange(new[]{"أمين مخزن الإنتاج التام WFG","السائق الناقل / استلام الشحنة","أمن بوابة المصنع / تصريح الخروج"});
        if(!d.IsApproved)m.FooterNote+=" هذا السند غير معتمد؛ لا يُعد تصريح إفراج معتمدًا.";
        return m;

        });
    }
    public static PhaseDocModel FinishedReceipt(DatesErpDbContext db,int id)
    {
        return Snapshot(db, () =>
        {
        var d=db.FinishedGoodsReceipts.AsNoTracking().Include(x=>x.Items).Single(x=>x.Id==id);
        var order=db.ProductionOrders.AsNoTracking().FirstOrDefault(o=>o.Id==d.OrderId);
        var m=Model(db,d,"سند تسليم واستلام إنتاج تام (WFG)");m.MainTitle="بنود الإنتاج التام والمخرجات الثانوية المسلمة للمستودعات";
        m.StatusAr=PrintStatus.Of(d)+" — "+ReceiptState(d.ReceiptStatus);
        m.Info.AddRange(new[]{("أمر الإنتاج",order?.DocumentNumber??"—"),("تاريخ السند",UiFormat.D(d.DeliveryDate)),("حالة الاستلام",ReceiptState(d.ReceiptStatus)),("رقم الاستلام",d.ReceiptNumber??"—"),
            ("المستودع",db.Warehouses.AsNoTracking().Where(w=>w.Id==d.WarehouseId).Select(w=>w.WarehouseNameAr).FirstOrDefault()??"—")});
        m.Columns=new[]{"م","العميل","الصنف","الدفعة","العبوة","عدد العبوات","المسلم (كجم)","المستلم فعليًا (كجم)","المتبقي (كجم)"};
        int n=1;foreach(var i in d.Items.OrderBy(i=>i.Id))m.Rows.Add(new object[]{n++,Customer(db,i.CustomerId??order?.CustomerId),Product(db,i.ProductId),Lot(db,i.LotId),Pack(db,i.PackagingTypeId),i.PackageCount,i.NetWeightKg,i.ReceivedQtyKg,Math.Max(0,i.NetWeightKg-i.ReceivedQtyKg)});
        m.Totals.AddRange(new[]{("عدد العبوات",N(d.Items.Sum(i=>i.PackageCount))),("المسلم (كجم)",N(d.Items.Sum(i=>i.NetWeightKg))),("المستلم فعليًا (كجم)",N(d.Items.Sum(i=>i.ReceivedQtyKg))),("المتبقي (كجم)",N(d.Items.Sum(i=>Math.Max(0,i.NetWeightKg-i.ReceivedQtyKg))))});
        m.Signatures.AddRange(new[]{"مسؤول التعبئة والتغليف","ضابط فحص الجودة","أمين مستودع الإنتاج التام WFG","مدير إدارة الإنتاج / الاعتماد"});return m;

        });
    }
    public static PhaseDocModel ProductionDelivery(DatesErpDbContext db,int id)
    {
        return Snapshot(db, () =>
        {
        var d=db.ProductionDeliveries.AsNoTracking().Include(x=>x.Items).Single(x=>x.Id==id);
        var m=Model(db,d,"أمر تسليم الإنتاج إلى المستودعات");m.MainTitle="الكميات المحررة والاستلام الفعلي";
        m.Info.AddRange(new[]{("تاريخ الأمر",UiFormat.D(d.DeliveryDate)),("مصدر الأمر",DeliverySources.ToArabic(d.SourceType)),("معرف المصدر",d.SourceId.ToString()),("حالة الاستلام",ReceiptState(d.ReceiptStatus))});
        m.Columns=new[]{"م","أمر الإنتاج","العميل","الصنف","الدفعة","عدد العبوات","المحرر (كجم)","المستلم (كجم)","المتبقي (كجم)"};
        int n=1;foreach(var i in d.Items.OrderBy(i=>i.Id))m.Rows.Add(new object[]{n++,db.ProductionOrders.AsNoTracking().Where(o=>o.Id==i.OrderId).Select(o=>o.DocumentNumber).FirstOrDefault()??"—",Customer(db,i.CustomerId),Product(db,i.ProductId),Lot(db,i.LotId),i.PackageCount,i.QtyKg,i.ReceivedQtyKg,Math.Max(0,i.QtyKg-i.ReceivedQtyKg)});
        m.Totals.Add(("المحرر (كجم)",N(d.Items.Sum(i=>i.QtyKg))));m.Totals.Add(("المستلم (كجم)",N(d.Items.Sum(i=>i.ReceivedQtyKg))));
        if(!string.IsNullOrWhiteSpace(d.BypassReason))m.Notes+="\nسبب تجاوز الفحص المسجل: "+d.BypassReason;
        m.Signatures.AddRange(new[]{"مدير إدارة الإنتاج / التحرير","مسؤول تسليم الإنتاج","أمين المستودع / الاستلام"});
        m.FooterNote+=" تحرير الأمر لا يثبت استلام المخزن؛ الاستلام الفعلي يظهر في عموده.";return m;

        });
    }
    public static PhaseDocModel Quality(DatesErpDbContext db,int id)
    {
        return Snapshot(db, () =>
        {
        var d=db.QualityChecks.AsNoTracking().Include(x=>x.Results).Include(x=>x.Items).Single(x=>x.Id==id);
        var order=db.ProductionOrders.AsNoTracking().FirstOrDefault(o=>o.Id==d.OrderId);
        int? planId=order?.SourcePlanId;
        var m=Model(db,d,"استمارة وشهادة فحص جودة التمور (QC Lab Sheet)");m.MainTitle="نتائج فحص ومطابقة العينات التامة — كل نتيجة بوحدتها";
        string decision=d.Decision switch {"Passed"=>"مطابق ومقبول","Quarantine"=>"حجز وتحريز","Rejected"=>"مرفوض",_=>d.Decision??"غير محدد"};
        m.StatusAr=PrintStatus.Of(d);m.Info.AddRange(new[]{("قرار الجودة المسجل",decision),("تاريخ الفحص",UiFormat.D(d.CheckDate)),("نوع الفحص",d.CheckType),("مسؤول الجودة",d.InspectorName??"—"),("العينة المسجلة (كرتون)",d.SampleCartons.ToString()),("أمر الإنتاج",order?.DocumentNumber??"فحص يدوي"),("خطة الإنتاج",db.ProductionPlans.AsNoTracking().Where(p=>p.Id==planId).Select(p=>p.DocumentNumber).FirstOrDefault()??"—")});
        m.Columns=new[]{"م","نتيجة الفحص","التصنيف","الكمية","الوحدة","الصنف","العميل","الدفعة","ملاحظات"};
        var types=db.InspectionResultTypes.AsNoTracking().ToDictionary(t=>t.Id);
        string Kind(InspectionResult r)=>!types.TryGetValue(r.ResultTypeId,out var t)?"غير معرف":t.ResultKind switch {"Accepted"=>"مقبول","Rejected"=>t.IsFinalScrap?"مرفوض نهائي":"غير مطابق","ByProduct"=>"مخرج ثانوي","Loss"=>"فاقد",_=>t.ResultKind};
        string Unit(InspectionResult r)=>!string.IsNullOrWhiteSpace(r.UnitLabel)?r.UnitLabel:db.UnitsOfMeasure.AsNoTracking().Where(u=>u.Id==r.UnitId).Select(u=>u.UnitNameAr).FirstOrDefault()??"غير محددة";
        int n=1;foreach(var r in d.Results.OrderBy(r=>r.Id))
        {
            int? customer=db.Lots.AsNoTracking().Where(l=>l.Id==r.LotId).Select(l=>l.CustomerId).FirstOrDefault();
            m.Rows.Add(new object[]{n++,types.TryGetValue(r.ResultTypeId,out var t)?t.NameAr:$"#{r.ResultTypeId}",Kind(r),r.Qty,Unit(r),Product(db,r.ProductId),Customer(db,customer),Lot(db,r.LotId),r.Notes??""});
        }
        foreach(var g in d.Results.GroupBy(r=>new {r.UnitId,Label=Unit(r),Kind=Kind(r)}))m.Totals.Add(($"{g.Key.Kind} ({g.Key.Label})",N(g.Sum(r=>r.Qty))));
        if(d.Results.Count==0)
        {
            m.MainTitle="بنود الفحص القديم — القيم المسجلة بالكيلو دون اختراع نتائج تفصيلية";
            m.Columns=new[]{"الصنف","الدفعة","المفحوص (كجم)","المقبول (كجم)","المرفوض (كجم)","ملاحظات"};
            foreach(var i in d.Items.OrderBy(i=>i.Id))m.Rows.Add(new object[]{Product(db,i.ProductId),Lot(db,i.LotId),i.CheckedQtyKg,i.AcceptedQtyKg,i.RejectedQtyKg,i.Notes??""});
            m.Totals.AddRange(new[]{("المفحوص المسجل (كجم)",N(d.TotalCheckedKg)),("المقبول المسجل (كجم)",N(d.AcceptedKg)),("المرفوض المسجل (كجم)",N(d.RejectedKg))});
        }
        m.SecondTitle="المعايير المخبرية والحسية — القياسات المحفوظة والحدود المعرفة حاليًا";
        m.SecondColumns=new[]{"المعيار","الوحدة","الحد الأدنى الحالي","الحد الأعلى الحالي","القياس المسجل","ملاحظات"};
        foreach(var r in db.QualityStandardRecords.AsNoTracking().Where(r=>r.CheckId==id).OrderBy(r=>r.Id).ToList())
        {
            var s=db.QualityStandards.AsNoTracking().FirstOrDefault(s=>s.Id==r.StandardId);
            m.SecondRows.Add(new object[]{s?.NameAr??$"#{r.StandardId}",s?.UnitLabel??"—",s?.MinValue is double min?N(min):"—",s?.MaxValue is double max?N(max):"—",r.Value,r.Notes??""});
        }
        m.Notes=string.Join("\n",new[]{d.Notes,d.InspectorNotes}.Where(x=>!string.IsNullOrWhiteSpace(x)));
        m.Signatures.AddRange(new[]{"أخصائي فحص الجودة والمختبر","رئيس قسم الجودة وسلامة الغذاء","مدير المصنع / اعتماد الإفراج المخزني"});
        m.FooterNote+=" حدود المعايير تُقرأ من التعريف الحالي ولا تمثل لقطة تاريخية. قرار الجودة لا يحل محل اعتماد المحضر.";return m;

        });
    }
    public static PhaseDocModel Execution(DatesErpDbContext db,int orderId)
    {
        return Snapshot(db, () =>
        {
        var order=db.ProductionOrders.AsNoTracking().Single(o=>o.Id==orderId);
        var m=Model(db,order,"تقرير تنفيذ وإقفال يوم الإنتاج");m.MainTitle="جلسات التنفيذ المحفوظة";
        m.Info.Add(("تاريخ أمر الإنتاج",UiFormat.D(order.ProductionDate)));
        var runs=db.ProductionExecutions.AsNoTracking().Include(e=>e.Downtimes).Include(e=>e.ByProducts).Where(e=>e.OrderId==orderId).OrderBy(e=>e.Id).ToList();
        m.Columns=new[]{"الجلسة","البداية","النهاية","كرتون فعلي","إنتاج فعلي (كجم)","خام مستهلك (كجم)","فاقد (كجم)","حالة الجلسة","ملاحظات الإقفال"};
        foreach(var r in runs)m.Rows.Add(new object[]{r.DocumentNumber,r.StartDateTime?.ToString("dd/MM/yyyy HH:mm")??"—",r.EndDateTime?.ToString("dd/MM/yyyy HH:mm")??"—",r.ActualCartons,r.ActualQtyKg,r.ConsumedRawKg,r.WastageQtyKg,r.IsDayClosed?"مقفل":"غير مقفل",r.ClosingNotes??""});
        m.SecondTitle="السجل الزمني للتوقفات";m.SecondColumns=new[]{"الجلسة","التوقف","الاستئناف","الساعات","السبب"};
        foreach(var r in runs)foreach(var s in r.Downtimes.OrderBy(s=>s.Id))m.SecondRows.Add(new object[]{r.DocumentNumber,s.StartTime??"—",s.EndTime??"—",s.Hours,s.ReasonAr});
        var by=new PrintSection {Title="المخرجات الثانوية المسجلة",Columns=new[]{"الجلسة","الصنف الثانوي","الكمية","الوحدة"}};
        foreach(var r in runs)
        {
            foreach(var b in r.ByProducts)
            { var p=db.ByProducts.AsNoTracking().FirstOrDefault(x=>x.Id==b.ByProductId);by.Rows.Add(new[]{r.DocumentNumber,p?.ByProductNameAr??$"#{b.ByProductId}",N(b.Qty),p?.UnitOfMeasure??"غير محددة"}); }
            if(r.ByProducts.Count==0) { if(r.HashfKg>0)by.Rows.Add(new[]{r.DocumentNumber,"حشف (سجل سابق)",N(r.HashfKg),"كجم"});if(r.NawaKg>0)by.Rows.Add(new[]{r.DocumentNumber,"نوى (سجل سابق)",N(r.NawaKg),"كجم"}); }
        }
        var comparison = new PrintSection { Title="الخطة مقابل الفعلي — المخطط محفوظ دون تعديل", Columns=new[]{"العميل","الصنف","المخطط (كرتون)","الفعلي (كرتون)","الفرق (كرتون)"} };
        foreach(var i in db.ProductionOrderItems.AsNoTracking().Where(i=>i.OrderId==orderId).OrderBy(i=>i.Id).ToList())
            comparison.Rows.Add(new[]{db.Customers.Where(c=>c.Id==(i.CustomerId??order.CustomerId)).Select(c=>c.CustomerName).FirstOrDefault()??"—",
                db.Products.Where(p=>p.Id==i.ProductId).Select(p=>p.ProductNameAr).FirstOrDefault()??"—",N(i.PlannedCartons),N(i.ProducedCartons),N(i.PlannedCartons-i.ProducedCartons)});
        m.ExtraSections.Add(by);
        if (comparison.Rows.Count > 0) m.ExtraSections.Add(comparison);m.Totals.Add(("الإنتاج الفعلي (كجم)",N(runs.Sum(r=>r.ActualQtyKg))));m.Totals.Add(("الكراتين الفعلية",N(runs.Sum(r=>r.ActualCartons))));
        m.Signatures.AddRange(new[]{"مشرف الوردية","مدير الإنتاج","مسؤول الجودة"});return m;

        });
    }

    public static PhaseDocModel Receiving(DatesErpDbContext db, int id)
    {
        return Snapshot(db, () =>
        {
            var d = db.Shipments.AsNoTracking().Include(x => x.Items).Single(x => x.Id == id);
            var m = Model(db, d, "سند استلام التمور (Receiving Voucher)");
            m.MainTitle = "بنود الشحنة الواردة والكميات المستلمة";
            m.Info.Add(("العميل المورد", Customer(db, d.CustomerId)));
            m.Info.Add(("تاريخ الوصول", UiFormat.D(d.ArrivalDate)));
            m.Info.Add(("تاريخ الاستلام", UiFormat.D(d.ReceivedDate)));
            m.Info.Add(("رقم الحاوية", d.ContainerNumber ?? "—"));
            m.Columns = new[] { "م", "الصنف", "العبوة", "عدد العبوات", "وزن العبوة (كجم)", "الوزن (كجم)", "الوحدة" };
            int n = 1;
            foreach (var i in d.Items.OrderBy(i => i.Id))
                m.Rows.Add(new object[] { n++, Product(db, i.ProductId), Pack(db, i.PackagingTypeId), i.PackageCount, i.UnitWeightKg, i.TotalWeightKg, i.ReceiptUnit ?? "كجم" });
            m.Totals.Add(("عدد العبوات", N(d.Items.Sum(i => i.PackageCount))));
            m.Totals.Add(("إجمالي الوزن (كجم)", N(d.Items.Sum(i => i.TotalWeightKg))));
            m.Signatures.AddRange(new[] { "موظف الاستلام", "السائق الناقل", "أمين مخزن الخام WRM" });
            return m;
        });
    }
}

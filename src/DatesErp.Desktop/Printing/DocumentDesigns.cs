using DatesErp.Core.Common;
using DatesErp.Desktop.Views;

namespace DatesErp.Desktop.Printing;
public static class PrintStatus
{
    public static string Of(WorkflowDocument doc) => doc.Status==DocStatuses.Cancelled?"ملغى — ليس صالحًا للتنفيذ"
        : doc.Status==DocStatuses.Closed || doc is DatesErp.Core.Domain.Entities.ProductionPlan { IsClosed: true } || doc is DatesErp.Core.Domain.Entities.ProductionOrder { IsClosed: true } ? "مقفل نهائيًا"
        : doc.Status==DocStatuses.Draft ? "مسودة — غير معتمد" : DocStatuses.ToArabic(doc.Status);
}
public static class ReceivingPrintDesign
{
    public static PhaseDocModel Create(ReceivingPrintModel m)
    {
        var p=new PhaseDocModel {CompanyNameAr=m.CompanyNameAr,CompanyAddress=m.Address,CompanyPhone=m.Phone,LogoBytes=m.LogoBytes,
            CapturedAt=m.CapturedAt,DocTitle="أمر وسند استلام شحنة تمور خام",DocNo=m.DocumentNumber,StatusAr=m.StatusAr??(m.IsApproved?"معتمد":"مسودة — غير معتمد"),
            Columns=new[]{"م","الصنف / الرمز","وحدة الاستلام","نوع العبوة","العدد","وزن العبوة (كجم)","الوزن (كجم)","المعالجة","حتى تاريخ","حالة المعالجة / إتمامها"},
            ColumnWeights=new[]{0.4,1.8,0.9,1d,0.6,0.9,1d,0.7,1d,1.6},MainTitle="بنود الشحنة — قرار المعالجة مستقل لكل سطر",Notes=m.Notes,
            Signatures={"موظف الاستلام","مسؤول فحص الجودة","أمين مخزن المواد الخام","مدير المصنع / الاعتماد"}};
        p.Info.AddRange(new[]{("العميل المورد",m.CustomerName),("الحاوية / الشاحنة",m.ContainerNumber),("تاريخ الوصول",UiFormat.D(m.ArrivalDate)),("تاريخ الاستلام",UiFormat.D(m.ReceivedDate)),("مخزن الاستلام",m.WarehouseName),("موظف الاستلام",m.EmployeeName)});
        if(!string.IsNullOrWhiteSpace(m.VesselName))p.Info.Add(("السفينة",m.VesselName));
        if(!string.IsNullOrWhiteSpace(m.CompanyNameEn))p.Info.Add(("اسم المنشأة بالإنجليزية",m.CompanyNameEn));
        foreach(var r in m.Items)p.Rows.Add(new object[]{r.RowNo,$"{r.ProductName}\n{r.ProductCode}",r.ReceiptUnit,r.PackName,r.PackageCount,r.UnitWeightKg,r.QtyKg,
            r.TreatmentRequired==null?"غير محدد":r.TreatmentRequired==true?"نعم":"لا",r.TreatmentRequired==true?UiFormat.D(r.TreatmentUntilDate):"—",m.IsApproved?r.TreatmentState(m.CapturedAt):"استلام غير معتمد"});
        p.Totals.AddRange(new[]{("وزن الشحنة (كجم)",PrintLayout.Format(m.Items.Sum(i=>i.QtyKg))),("عدد العبوات",m.Items.Sum(i=>i.PackageCount).ToString()),("عدد البنود",m.Items.Count.ToString())});
        p.FooterNote="المعالجة حسب القرار والتاريخ المسجلين لكل بند. حلول التاريخ وحده لا يثبت إتمام المعالجة؛ راجع حالة النظام. التوقيعات للاعتماد الورقي ولا تُغيّر حالة السند آليًا.";
        return p;
    }
}
public static class PlanningPrintDesign
{
    public static PhaseDocModel Create(PlanningPrintModel m)
    {
        var p=new PhaseDocModel {CompanyNameAr=m.CompanyNameAr,CompanyAddress=m.Address,CompanyPhone=m.Phone,LogoBytes=m.LogoBytes,
            // No claim that a draft is approved in the title itself.
            DocTitle="خطة وجدولة تشغيل وإنتاج التمور",DocNo=m.PlanNumber,StatusAr=m.StatusAr??(m.IsApproved?"معتمدة":"مسودة — غير معتمدة"),
            MainTitle="بنود التشغيل مرتبة بتاريخ الإنتاج",Columns=new[]{"م","التاريخ","العميل","الشحنة / الدفعة","الخام","المنتج التام","العبوة","كرتون","كجم","الوردية / الخط"},
            ColumnWeights=new[]{0.4,0.9,1.3,1.4,1d,1.5,1d,0.6,0.8,1.2},Notes=m.Notes,
            Signatures={"مسؤول التخطيط والجدولة","مدير الإنتاج","المدير العام / اعتماد الخطة"}};
        p.Info.AddRange(new[]{("عنوان الخطة",m.Title),("نوع الخطة",m.PlanTypeAr),("من",UiFormat.D(m.StartDate)),("إلى",UiFormat.D(m.EndDate)),("وردية الرأس",m.ShiftName),("خط الرأس",m.LineName),("أعدها",m.CreatedByName)});
        foreach(var r in m.Items)p.Rows.Add(new object[]{r.RowNo,r.Date,r.CustomerName,$"{r.ShipmentNo}\n{r.LotCode}",r.RawName,r.ProductName,r.PackName,r.Cartons,r.QtyKg,$"{r.ShiftName}\n{r.LineName}"});
        p.SecondTitle="ملخص العملاء";p.SecondColumns=new[]{"العميل","عدد البنود","الكراتين المستهدفة","الوزن المستهدف (كجم)"};
        foreach(var g in m.Items.GroupBy(i=>new {i.CustomerId,i.CustomerName}))p.SecondRows.Add(new object[]{g.Key.CustomerName,g.Count(),g.Sum(i=>i.Cartons),g.Sum(i=>i.QtyKg)});
        p.Totals.Add(("الكراتين المستهدفة",m.Items.Sum(i=>i.Cartons).ToString()));p.Totals.Add(("الوزن المستهدف (كجم)",PrintLayout.Format(m.Items.Sum(i=>i.QtyKg))));
        p.FooterNote="نسخة طباعة من الخطة المحفوظة. لا تثبت هذه الورقة جاهزية الخام ولا تُصدر أوامر تشغيل. لا تغيير لقواعد التخطيط في هذا التحديث.";
        return p;
    }
}

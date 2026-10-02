using DatesErp.Desktop.Printing;
using DatesErp.Desktop.Views;

namespace DatesErp.PrintSmoke;

// Fabricated layout fixtures, never connected to a database. Not transaction samples or real documents.
public static class PrintSamples
{
    public static List<(string Id,PhaseDocModel Model)> Create()
    {
        var list=new List<(string,PhaseDocModel)>();
        var receipt=new ReceivingPrintModel {DocumentNumber="DEMO-RCV",CompanyNameAr="منشأة توضيحية — بيانات اختبار فقط",IsApproved=true,StatusAr="معتمد — مثال غير تشغيلي",CustomerName="عميل تجريبي",ReceivedDate=new(2026,9,9),CapturedAt=new(2026,9,9,10,0,0)};
        for(int i=0;i<80;i++)receipt.Items.Add(new(){RowNo=i+1,ProductCode="001-DEMO",ProductName="تمر خام توضيحي لا يمثل شحنة فعلية",ReceiptUnit="سلة",PackName="سلة اختبار",QtyKg=100,PackageCount=5,UnitWeightKg=20,TreatmentRequired=i%3==0?null:i%3==1,TreatmentUntilDate=i%3==1?new DateTime(2026,10,9):null});
        list.Add(("01-receiving",ReceivingPrintDesign.Create(receipt)));
        var plan=new PlanningPrintModel {PlanNumber="DEMO-PLAN",Title="خطة نموذجية لاختبار الطباعة فقط",CompanyNameAr=receipt.CompanyNameAr,StatusAr="مسودة — غير معتمدة",StartDate=new(2026,9,9),EndDate=new(2026,10,9)};
        for(int i=0;i<80;i++)plan.Items.Add(new(){RowNo=i+1,CustomerId=i%2+1,CustomerName="عميل تجريبي "+(i%2+1),ProductName="صنف تام تجريبي",RawName="خام توضيحي",ShipmentNo="DEMO-SHIP",LotCode="DEMO-LOT",PackName="كرتون اختبار",Cartons=20,QtyKg=100,Date="17/09/2026",ShiftName="صباحية",LineName="خط تجريبي"});
        list.Add(("02-planning",PlanningPrintDesign.Create(plan)));
        PhaseDocModel Make(string title,string[] cols,string[]? second=null)
        {
            var m=new PhaseDocModel {DocTitle=title,DocNo="DEMO-ONLY",CompanyNameAr=receipt.CompanyNameAr,CompanyAddress="عنوان توضيحي — غير حقيقي",CompanyPhone="غير مسجل",StatusAr="مسودة — بيانات اختبار غير تشغيلية",Columns=cols,
                CapturedAt=receipt.CapturedAt,Notes=string.Join(" ",Enumerable.Repeat("ملاحظة طويلة للتحقق من عدم حذف النص عند نهاية الصفحة.",35)),Signatures=new(){"مُعد المستند","المستلم","المراجع"},FooterNote="بيانات مصطنعة لا تمثل مخزونًا أو موافقة فعلية. نهاية البيان: END-OF-DEMO"};
            m.Info.Add(("تاريخ السند","03/02/2025"));m.Info.Add(("المرجع","DEMO-REFERENCE"));
            for(int i=0;i<80;i++)m.Rows.Add(cols.Select((c,k)=>(object)(k==0?$"{i+1}":c+" — قيمة توضيحية")).ToArray());
            if(second!=null) {m.SecondTitle="الجدول الثاني — استمرار مستقل";m.SecondColumns=second;for(int i=0;i<80;i++)m.SecondRows.Add(second.Select(c=>(object)(c+" "+(i+1))).ToArray());}
            return m;
        }
        list.Add(("03-order",Make("أمر وتشغيل إنتاج التمور",new[]{"م","الدفعة","العميل","الخام","المنتج","العبوة","كجم","كرتون","منفذ"},new[]{"المادة","الوحدة","المحتسب","المصروف","المستهلك"})));
        list.Add(("04-execution",Make("تقرير تنفيذ وإقفال يوم الإنتاج",new[]{"الجلسة","البداية","النهاية","كرتون","الإنتاج (كجم)","الخام (كجم)","فاقد","الحالة","ملاحظات"},new[]{"الجلسة","التوقف","الاستئناف","ساعات","السبب"})));
        var qc=Make("استمارة وشهادة فحص الجودة",new[]{"م","النتيجة","التصنيف","الكمية","الوحدة","الصنف","العميل","الدفعة","ملاحظات"},new[]{"المعيار","الوحدة","أدنى حالي","أعلى حالي","القياس","ملاحظات"});
        qc.Totals.Add(("مقبول (كرتون)","10.00"));qc.Totals.Add(("مخرج ثانوي (كجم)","3.00"));list.Add(("05-quality",qc));
        list.Add(("06-production-delivery",Make("أمر تسليم الإنتاج إلى المستودعات",new[]{"م","الأمر","العميل","الصنف","الدفعة","عدد العبوات","محرر (كجم)","مستلم (كجم)","متبقي (كجم)"})));
        list.Add(("07-finished-receipt",Make("سند تسليم واستلام إنتاج تام",new[]{"م","العميل","الصنف","الدفعة","العبوة","عدد العبوات","المسلم (كجم)","المستلم (كجم)","المتبقي (كجم)"})));
        list.Add(("08-customer-delivery",Make("سند إخراج وتسليم بضاعة للعميل",new[]{"م","الصنف","الدفعة","العبوة","العدد","وزن الكرتون المسجل","المسلم (كجم)"})));
        var carton=Make("سند بيع كرتون فارغ",new[]{"الصنف","الكراتين","سعر الكرتون","الإجمالي"});carton.Totals.Add(("عدد الكراتين","100"));carton.Totals.Add(("القيمة","250.00"));list.Add(("09-carton-sale",carton));
        foreach(var (_,m) in list)m.CompanyNameAr=receipt.CompanyNameAr;
        return list;
    }
}

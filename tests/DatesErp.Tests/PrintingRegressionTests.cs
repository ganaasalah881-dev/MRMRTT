using DatesErp.Desktop.Printing;
using DatesErp.Desktop.Views;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DatesErp.Tests;

public class PrintingRegressionTests
{
    // Deterministic test measurer. Real glyph measurements are covered by the Windows STA smoke runner.
    private static int Lines(string text,double width,double size,bool bold)
        => Math.Max(1,(text??"").Split('\n').Sum(s=>Math.Max(1,(int)Math.Ceiling(s.Length/Math.Max(1,Math.Floor(width/(size*0.6)))))));
    public static IEnumerable<object[]> Sizes => new[]{0,1,10,30,70,250,2000}.SelectMany(n=>new[]{false,true}.Select(l=>new object[]{n,l}));
    [Theory,MemberData(nameof(Sizes))]
    public void All_Rows_And_Headers_Survive_Pagination_Within_A4(int count,bool landscape)
    {
        var section=new PrintSection {Title="بنود طويلة",Columns=new[]{"م","الصنف"},Rows=Enumerable.Range(0,count).Select(i=>new[]{i.ToString(),"تمر تجريبي "+new string('ت',i%50)}).ToList()};
        var spec=new PrintSpec {Landscape=landscape,Sections=new(){section}};
        var pages=PrintLayout.Paginate(spec,120,Lines);
        Assert.NotEmpty(pages);double bottom=(landscape?PrintLayout.A4Width:PrintLayout.A4Height)-48;
        foreach(var p in pages)
        {
            Assert.NotEmpty(p.Slices);Assert.Equal("title",p.Slices[0].Style);Assert.Contains(p.Slices,s=>s.Style=="header");
            Assert.True(p.Slices.Last().RowIndex>=0); // No orphan section/header on previous page.
            foreach(var s in p.Slices)
            {
                Assert.InRange(s.Y,120,bottom);Assert.True(s.Y+s.Height<=bottom+0.001);
                Assert.Equal((landscape?PrintLayout.A4Height:PrintLayout.A4Width)-2*PrintLayout.Margin,s.Widths.Sum(),8);
            }
        }
        var rows=pages.SelectMany(p=>p.Slices).Where(s=>s.RowIndex>=0).GroupBy(s=>s.RowIndex).OrderBy(g=>g.Key).ToList();
        Assert.Equal(Math.Max(1,count),rows.Count);
        foreach(var row in rows)
        {
            int offset=0;foreach(var part in row) {Assert.Equal(offset,part.FirstLine);offset+=part.LineCount;}
            if(count>0)Assert.Equal(section.Rows[row.Key],row.First().Cells);
        }
    }
    [Fact] public void Oversized_Row_And_Second_Table_Continue_With_Their_Own_Headers()
    {
        var spec=new PrintSpec {Landscape=true,Sections=new(){
            new(){Title="رئيسي",Columns=new[]{"صنف"},Rows=new(){new[]{"الأول"}}},
            new(){Title="تفاصيل",Columns=new[]{"ملاحظة"},Rows=new(){new[]{string.Join("\n",Enumerable.Range(0,250).Select(i=>$"تفصيل {i}"))}}}
        }};
        var pages=PrintLayout.Paginate(spec,130,Lines);Assert.True(pages.Count>3);
        foreach(var page in pages.Skip(1)) { Assert.Equal("تفاصيل",page.Slices[0].Section);Assert.Equal("ملاحظة",page.Slices.Single(s=>s.Style=="header").Cells.Single()); }
        var pieces=pages.SelectMany(p=>p.Slices).Where(s=>s.Section=="تفاصيل" && s.RowIndex==0).ToList();
        Assert.Equal(250,pieces.Sum(s=>s.LineCount));Assert.Equal(0,pieces[0].FirstLine);
    }
    [Fact] public void Signatures_Move_As_Whole_Block_And_Footer_Note_Is_Not_Lost()
    {
        var m=new PhaseDocModel {Columns=new[]{"صنف"},Rows=Enumerable.Range(0,22).Select(i=>new object[]{i}).ToList(),
            Signatures=new(){"المستلم","الجودة","المخزن","الاعتماد"},Notes=new string('س',300),FooterNote="آخر بيان محفوظ"};
        var spec=PrintSchema.FromPhase(m);var pages=PrintLayout.Paginate(spec,120,Lines);
        Assert.Contains(pages.SelectMany(p=>p.Slices),s=>s.Cells.Contains("آخر بيان محفوظ"));
        var signatures=pages.SelectMany(p=>p.Slices).Where(s=>s.Style=="signature").ToList();
        Assert.Single(signatures);Assert.Equal(4,signatures[0].Cells.Length);
        Assert.All(pages,p=>Assert.True(p.Slices.Last().RowIndex>=0));
    }
    [Theory]
    [InlineData(794,1122,12,15,750,1060)] [InlineData(1122,794,15,12,750,1060)]
    [InlineData(794,1122,22,30,1080,750)] [InlineData(1122,794,0,0,1122,794)]
    public void Printer_Fit_Honors_Imageable_Origin_And_Aspect_Ratio(double sw,double sh,double x,double y,double w,double h)
    {
        var fit=PrintLayout.Fit(sw,sh,x,y,w,h);
        Assert.True(fit.X>=x && fit.Y>=y);Assert.True(fit.X+sw*fit.Scale<=x+w+0.001);Assert.True(fit.Y+sh*fit.Scale<=y+h+0.001);
        Assert.Equal(sw/sh,(sw*fit.Scale)/(sh*fit.Scale),8);
    }
    [Fact] public void Invalid_Widths_Missing_Headers_And_Extra_Cells_Fail_Not_Silently_Truncate()
    {
        Assert.Throws<ArgumentException>(()=>PrintLayout.ColumnWidths(700,2,new[]{1d,0d}));
        Assert.Throws<ArgumentException>(()=>PrintLayout.ColumnWidths(700,2,new[]{double.NaN,1d}));
        Assert.Throws<ArgumentException>(()=>PrintLayout.Fit(794,1122,0,0,0,800));
        Assert.Throws<ArgumentException>(()=>PrintSchema.FromPhase(new(){Rows=new(){new object[]{1}}}));
        Assert.Throws<ArgumentException>(()=>PrintLayout.Paginate(new(){Sections=new(){new(){Columns=new[]{"a"},Rows=new(){new[]{"one","two"}}}}},120,Lines));
    }
    [Fact] public void Snapshot_Copies_Rows_And_Does_Not_Infer_Price_Or_Unit_Totals()
    {
        var m=new PhaseDocModel {Columns=new[]{"الصنف","السعر","الكمية","الوحدة"},Rows=new(){new object[]{"A",2.5,2,"كرتون"},new object[]{"B",3.5,7,"كجم"}},SecondColumns=new[]{"معيار"},SecondRows=new(){new object[]{"قياس"}},Notes="ملاحظة",FooterNote="بيان"};
        var spec=PrintSchema.FromPhase(m);m.Rows[0][0]="تم تغييره";m.SecondRows.Clear();
        Assert.Equal("A",spec.Sections.First(s=>s.Columns.Length==4).Rows[0][0]);
        Assert.Single(spec.Sections.First(s=>s.Columns.Length==1).Rows);
        Assert.DoesNotContain(spec.Sections,s=>s.Style=="total");
        Assert.Contains(spec.Sections,s=>s.Rows.Any(r=>r.Contains("ملاحظة")));Assert.Contains(spec.Sections,s=>s.Rows.Any(r=>r.Contains("بيان")));
    }
    [Fact] public void Receiving_Decisions_Dates_Completion_And_Four_Signatures_Are_Independent()
    {
        var m=new ReceivingPrintModel {IsApproved=true,CapturedAt=new(2026,9,20),Items=new(){
            new(){RowNo=1,ProductName="نفس الصنف",QtyKg=100,PackageCount=5,TreatmentRequired=true,TreatmentUntilDate=new(2026,9,16),TreatmentCompletedAt=new(2026,9,16,0,0,0)},
            new(){RowNo=2,ProductName="نفس الصنف",QtyKg=100,PackageCount=5,TreatmentRequired=false},
            new(){RowNo=3,ProductName="نفس الصنف",QtyKg=100,PackageCount=5,TreatmentRequired=true,TreatmentUntilDate=new(2026,10,9)},
            new(){RowNo=4,ProductName="قديم",TreatmentRequired=null}}};
        var p=ReceivingPrintDesign.Create(m);Assert.Equal(4,p.Rows.Count);Assert.Equal(4,p.Signatures.Count);
        Assert.Equal("نعم",p.Rows[0][7]);Assert.Contains("مكتملة",p.Rows[0][9].ToString());Assert.Equal("لا",p.Rows[1][7]);Assert.Equal("—",p.Rows[1][8]);
        Assert.Contains("قيد المعالجة",p.Rows[2][9].ToString());Assert.Equal("غير محدد",p.Rows[3][7]);Assert.Equal("300.00",p.Totals[0].Value);
        Assert.False(p.Landscape); // §v1.50.25: النموذج المعتمد عمودي
        Assert.Equal(p.Columns.Length,p.ColumnWeights.Length);
    }
    [Fact] public void Due_Date_Without_Completion_Is_Not_Printed_As_Ready_And_Draft_Is_Not_In_Treatment()
    {
        var i=new ReceivingPrintModel.ItemRow {TreatmentRequired=true,TreatmentUntilDate=new(2026,9,16)};
        Assert.Contains("راجع",i.TreatmentState(new(2026,9,17)));Assert.DoesNotContain("مكتملة",i.TreatmentState(new(2026,9,17)));
        var p=ReceivingPrintDesign.Create(new(){IsApproved=false,Items=new(){i}});Assert.Equal("استلام غير معتمد",p.Rows[0][9]);
    }
    [Fact] public void Print_Does_Not_Claim_Treatment_Complete_Before_The_Chosen_Date_Even_With_Bad_Completion_Data()
    {
        var row=new ReceivingPrintModel.ItemRow {TreatmentRequired=true,TreatmentUntilDate=new(2026,10,9),TreatmentCompletedAt=new(2026,9,9)};
        Assert.Contains("لم يحل الموعد",row.TreatmentState(new(2026,9,10)));
        Assert.Contains("تعارض",row.TreatmentState(new(2026,10,10)));
    }

    [Fact] public void Planning_Draft_Title_Does_Not_Claim_Approval_And_Per_Row_Line_Is_Preserved()
    {
        var p=PlanningPrintDesign.Create(new(){IsApproved=false,Items=new(){new(){CustomerName="A",ShipmentNo="S1",LotCode="L1",LineName="خط ثان",ShiftName="مساء",Cartons=10,QtyKg=50}}});
        Assert.DoesNotContain("المعتمدة",p.DocTitle);Assert.Contains("غير معتمدة",p.StatusAr);Assert.Contains("خط ثان",p.Rows[0][9].ToString());
        Assert.Single(p.SecondRows);Assert.Equal(10,p.SecondRows[0][2]);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Customer_Delivery_Uses_Persisted_Date_And_Frozen_Weight_Not_Todays_Editor(bool approved)
    {
        using var h=new TestHost();h.LoginAsAdmin();var db=h.Get<DatesErpDbContext>();
        var d=new CustomerDelivery {DocumentNumber="CD-PRINT",CustomerId=1,DeliveryDate=new(2024,2,3),IsApproved=approved,Status=approved?DocStatuses.Approved:DocStatuses.Draft,
            Items=new(){new(){ProductId=3,PackagingTypeId=1,QtyKg=70,PackageCount=10,CartonWeightKg=7}}};db.CustomerDeliveries.Add(d);db.SaveChanges();
        d.DeliveryDate=new(2030,1,1);d.Items[0].QtyKg=999; // Unsaved UI/tracker values must not leak into print.
        var m=StoredPrintModels.CustomerDelivery(db,d.Id);Assert.Contains(m.Info,x=>x.Label=="تاريخ التسليم" && x.Value=="03/02/2024");
        Assert.Equal("7.00",m.Rows[0][5]);Assert.Equal(70d,m.Rows[0][6]);
        Assert.Equal(approved?"معتمد":"مسودة — غير معتمد",m.StatusAr);Assert.NotEqual(999d,m.Rows[0][6]);
    }
    [Fact] public void Finished_Goods_Partial_Receipt_Separates_Sent_Received_And_Remaining()
    {
        using var h=new TestHost();h.LoginAsAdmin();var db=h.Get<DatesErpDbContext>();
        var o=new ProductionOrder {DocumentNumber="OP",CustomerId=1};db.ProductionOrders.Add(o);db.SaveChanges();
        var d=new FinishedGoodsReceipt {DocumentNumber="FGP",OrderId=o.Id,WarehouseId=1,DeliveryDate=new(2025,5,6),ReceiptStatus="Partial",
            Items=new(){new(){ProductId=3,CustomerId=1,NetWeightKg=100,ReceivedQtyKg=40,PackageCount=20}}};db.FinishedGoodsReceipts.Add(d);db.SaveChanges();
        var m=StoredPrintModels.FinishedReceipt(db,d.Id);Assert.Contains("جزئي",m.StatusAr,StringComparison.Ordinal);Assert.Equal(100d,m.Rows[0][6]);Assert.Equal(40d,m.Rows[0][7]);Assert.Equal(60d,m.Rows[0][8]);
        Assert.False(db.ChangeTracker.HasChanges());
    }
    [Fact] public void Manual_Quality_Preserves_Notes_Sample_Units_And_Does_Not_Print_Draft_As_Release()
    {
        using var h=new TestHost();h.LoginAsAdmin();var db=h.Get<DatesErpDbContext>();
        int t=db.InspectionResultTypes.First().Id;
        var q=new QualityCheck {DocumentNumber="QP",OrderId=null,CheckDate=new(2025,4,3),SampleCartons=0,InspectorName="فاحص",InspectorNotes="ملاحظة مطولة محفوظة",Decision="Passed",
            Results=new(){new(){ResultTypeId=t,ProductId=3,Qty=10,UnitLabel="كرتون",Notes="ملاحظة السطر"},new(){ResultTypeId=t,ProductId=3,Qty=3,UnitLabel="كجم"}}};
        db.QualityChecks.Add(q);db.SaveChanges();var m=StoredPrintModels.Quality(db,q.Id);
        Assert.Contains("غير معتمد",m.StatusAr);Assert.Contains(m.Info,x=>x.Label=="العينة المسجلة (كرتون)" && x.Value=="0");Assert.Equal("ملاحظة السطر",m.Rows[0][8]);Assert.Contains("ملاحظة مطولة",m.Notes);
        Assert.Equal(2,m.Totals.Count);Assert.Contains(m.Totals,x=>x.Label.Contains("كرتون") && x.Value=="10.00");Assert.Contains(m.Totals,x=>x.Label.Contains("كجم") && x.Value=="3.00");
        Assert.False(db.ChangeTracker.HasChanges());
    }
    [Fact] public void Production_Delivery_Prints_Saved_Bypass_Reason_And_Receipt_Progress()
    {
        using var h=new TestHost();h.LoginAsAdmin();var db=h.Get<DatesErpDbContext>();
        var d=new ProductionDelivery {DocumentNumber="PD-PRINT",SourceType=DeliverySources.FromPlan,SourceId=1,BypassReason="سبب مسجل",DeliveryDate=new(2025,1,2),ReceiptStatus="Partial",Status=DocStatuses.Issued,
            Items=new(){new(){ProductId=3,CustomerId=1,QtyKg=100,ReceivedQtyKg=30}}};db.ProductionDeliveries.Add(d);db.SaveChanges();
        var m=StoredPrintModels.ProductionDelivery(db,d.Id);Assert.Contains("سبب مسجل",m.Notes);Assert.Equal(70d,m.Rows[0][8]);Assert.False(db.ChangeTracker.HasChanges());
    }
    [Fact] public void Order_Projection_Uses_Saved_State_And_Preserves_Bom_Units()
    {
        using var h=new TestHost();h.LoginAsAdmin();var db=h.Get<DatesErpDbContext>();
        var a=db.AuxiliaryMaterials.First();
        var o=new ProductionOrder {DocumentNumber="BOM-PRINT",Status=DocStatuses.Cancelled,ProductionDate=new(2024,3,2),CustomerId=1,
            Items=new(){new(){ProductId=3,PackagingTypeId=1,PlannedCartons=20,PlannedQtyKg=100}},
            Materials=new(){new(){MaterialId=a.Id,UnitOfMeasure="قطعة",CalculatedQty=20,ActualIssuedQty=10,ConsumedQty=8}}};
        db.ProductionOrders.Add(o);db.SaveChanges();var m=StoredPrintModels.Order(db,o.Id);
        Assert.Contains("ملغى",m.StatusAr);Assert.Single(m.Rows);Assert.Single(m.SecondRows);Assert.Equal("قطعة",m.SecondRows[0][1]);Assert.Equal(8d,m.SecondRows[0][4]);Assert.False(db.ChangeTracker.HasChanges());
    }
    [Fact] public void Receiving_Print_Loads_Actual_Per_Line_Treatment_Without_Mutating_Stock()
    {
        using var h=new TestHost();h.LoginAsAdmin();var db=h.Get<DatesErpDbContext>();var r=h.Get<DatesErp.Core.Interfaces.Services.IReceivingService>();
        var saved=r.SaveShipment(1,"09/09/2026","09/09/2026",new(){
            new(){ProductId=1,PackagingTypeId=3,QtyKg=100,PackageCount=5,UnitWeightKg=20,TreatmentRequired=true,TreatmentUntilDate=new(2026,10,9)},
            new(){ProductId=1,PackagingTypeId=3,QtyKg=100,PackageCount=5,UnitWeightKg=20,TreatmentRequired=false}});
        Assert.True(saved.Ok,saved.Message);var approved=r.ApproveShipment(saved.Id);Assert.True(approved.Ok,approved.Message);
        double before=db.Lots.AsNoTracking().Sum(l=>l.UnderTreatmentQtyKg);var model=ReceivingPrintModel.Load(db,saved.Id);
        Assert.Equal(2,model.Items.Count);Assert.True(model.Items[0].TreatmentRequired);Assert.False(model.Items[1].TreatmentRequired);Assert.Equal(new DateTime(2026,10,9),model.Items[0].TreatmentUntilDate);
        Assert.Equal(before,db.Lots.AsNoTracking().Sum(l=>l.UnderTreatmentQtyKg));Assert.False(db.ChangeTracker.HasChanges());
    }
    [Fact] public void Planning_Print_Does_Not_Merge_Different_Customers_With_The_Same_Name()
    {
        var m=new PlanningPrintModel {Items=new(){new(){CustomerId=1,CustomerName="اسم متكرر",Cartons=10},new(){CustomerId=2,CustomerName="اسم متكرر",Cartons=20}}};
        var p=PlanningPrintDesign.Create(m);Assert.Equal(2,p.SecondRows.Count);Assert.Equal(10,p.SecondRows[0][2]);Assert.Equal(20,p.SecondRows[1][2]);
    }

    [Fact] public void Execution_Report_Contains_Sessions_Downtimes_And_Dynamic_Byproducts()
    {
        using var h=new TestHost();h.LoginAsAdmin();var db=h.Get<DatesErpDbContext>();
        var o=new ProductionOrder {DocumentNumber="EX-ORDER"};db.ProductionOrders.Add(o);db.SaveChanges();
        var by=db.ByProducts.First();
        var e=new ProductionExecution {DocumentNumber="EX-RUN",OrderId=o.Id,ActualCartons=10,ActualQtyKg=50,ConsumedRawKg=40,IsDayClosed=true,ClosingNotes="محضر الإقفال",
            Downtimes=new(){new(){Hours=0.5,StartTime="10:00",EndTime="10:30",ReasonAr="تنظيف"}},ByProducts=new(){new(){ByProductId=by.Id,Qty=2}}};db.ProductionExecutions.Add(e);db.SaveChanges();
        var m=StoredPrintModels.Execution(db,o.Id);Assert.Single(m.Rows);Assert.Single(m.SecondRows);Assert.Equal("10:30",m.SecondRows[0][2]);Assert.Equal("تنظيف",m.SecondRows[0][4]);
        Assert.Single(m.ExtraSections[0].Rows);Assert.Equal("2.00",m.ExtraSections[0].Rows[0][2]);Assert.False(db.ChangeTracker.HasChanges());
    }
}

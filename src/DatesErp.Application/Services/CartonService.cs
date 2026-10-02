using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §B10 — دورة الكرتون الفارغ: تولّد آلي من التفريغ (مستهلك ÷ وزن كرتون الخام)،
/// عدّ فعلي يقيّد الفروق، بيع موثق يخصم الرصيد ويمنع تجاوزه، وكشف تسوية:
/// أول مدة + متولّد − مبيع ± فروقات = آخر مدة.
/// </summary>
public class CartonService
{
    private readonly DatesErpDbContext Db;
    private readonly ICurrentSession Session;
    private readonly INumberingService Numbering;

    public CartonService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
    { Db = db; Session = session; Numbering = numbering; }

    // ═══ مصادر وزن كرتون الخام (الأدق أولاً): عبوة الدفعة ← وزن الاستلام ← بطاقة الصنف ═══
    public static double RawCartonWeight(DatesErpDbContext db, int? lotId, int productId)
    {
        if (lotId != null)
        {
            var lot = db.Lots.AsNoTracking().FirstOrDefault(l => l.Id == lotId);
            if (lot?.PackagingTypeId != null)
            {
                var w = db.PackagingTypes.AsNoTracking().Where(p => p.Id == lot.PackagingTypeId).Select(p => p.UnitWeightKg).FirstOrDefault();
                if (w > 0) return w;
            }
            if (lot?.ShipmentItemId != null)
            {
                var sw = db.ShipmentItems.AsNoTracking().Where(i => i.Id == lot.ShipmentItemId).Select(i => i.UnitWeightKg).FirstOrDefault();
                if (sw > 0) return sw;
            }
        }
        var cw = db.Products.AsNoTracking().Where(p => p.Id == productId).Select(p => p.CartonWeightKg).FirstOrDefault();
        // §لا وزن ثابت: كان يرجع 7.5 عند الغياب فيحسب النظام بصمت. صفر = غير معرَّف،
        // ومن يسجّل كراتين بلا وزن تعرّفه UnitsPolicy.RequireCartonWeight.
        return cw;
    }

    public int CartonProductId(int? packTypeId = null)
    {
        if (packTypeId != null)
        {
            var m = Db.Products.AsNoTracking().Where(p => p.GroupCode == "004" && p.IsActive && p.SourcePackagingTypeId == packTypeId)
                .OrderBy(p => p.Id).Select(p => p.Id).FirstOrDefault();
            if (m != 0) return m;
        }
        return Db.Products.AsNoTracking().Where(p => p.GroupCode == "004" && p.IsActive).OrderBy(p => p.Id).Select(p => p.Id).FirstOrDefault();
    }

    public int DefaultCartonWarehouseId()
        => Db.Warehouses.AsNoTracking().Where(w => w.WarehouseCode == "WPK").Select(w => w.Id).FirstOrDefault();

    // ═══ التولّد الآلي عند الإقفال ═══
    public void PostEmptyCartons(double cartons, string refDocNumber, int? warehouseId = null, int? packTypeId = null)
    {
        if (cartons <= 0) return;
        var pid = CartonProductId(packTypeId);
        var wid = warehouseId ?? DefaultCartonWarehouseId();
        if (pid == 0 || wid == 0) return; // لم يُهيأ الصنف/المخزن بعد — لا يعطل الإقفال
        AddStock(pid, wid, (int)Math.Round(cartons));
        Db.InventoryTransactions.Add(new InventoryTransaction
        {
            TxnNumber = Numbering.Next("CTX"),
            WarehouseId = wid,
            ProductId = pid,
            MovementType = MovementType.Inbound,
            QtyKg = 0,
            PackageCount = (int)Math.Round(cartons),
            ReferenceDocType = ReferenceDocType.CartonReturn,
            ReferenceDocNumber = refDocNumber,
            IsApproved = true
        });
        Db.SaveChanges();
    }

    private void AddStock(int productId, int warehouseId, int cartons, int? packagingTypeId = null, int? customerId = null)
    {
        // §1.50.66 — مفتاح الرصيد الكامل يشمل PackagingTypeId و CustomerId
        var b = Db.StockBalances.FirstOrDefault(x => x.WarehouseId == warehouseId && x.ProductId == productId && x.LotId == null && x.CustomerId == customerId && x.PackagingTypeId == packagingTypeId);
        if (b == null) { b = new StockBalance { WarehouseId = warehouseId, ProductId = productId, CustomerId = customerId, PackagingTypeId = packagingTypeId }; Db.StockBalances.Add(b); }
        // §1.50.66 — منع سالب إلا بتسوية
        if (b.PackageCount + cartons < 0)
            throw new DatesErp.Core.Exceptions.DomainException($"الرصيد السالب مرفوض للكرتون: الحالي {b.PackageCount} + المطلوب {cartons} = سالب — استخدم تسوية مصرحة.");
        b.PackageCount += cartons;
    }

    public int BookCartons(int productId, int warehouseId, int? packagingTypeId = null, int? customerId = null)
        => Db.StockBalances.AsNoTracking().Where(x => x.WarehouseId == warehouseId && x.ProductId == productId && x.LotId == null && x.CustomerId == customerId && x.PackagingTypeId == packagingTypeId)
            .Select(x => (int?)x.PackageCount).FirstOrDefault() ?? 0;

    public int BookCartonsByPack(int productId, int warehouseId, int? packagingTypeId)
        => BookCartons(productId, warehouseId, packagingTypeId);


    // ═══ سند العدّ الفعلي ═══
    public OpResult CreateCountDoc(int warehouseId, string date, string notes, List<(int productId, int counted)> lines)
    {
        Require("cartons", "Edit");
        if (lines == null || lines.Count == 0) return OpResult.Fail("أدخل صنفاً واحداً على الأقل للعدّ.");
        return RunOp(() =>
        {
            var doc = new CartonCountDoc
            {
                DocumentNumber = Numbering.Next("CCD"),
                CountDate = UiFormat.TryParseDate(date, out var d) ? d : DateTime.Now,
                WarehouseId = warehouseId,
                Notes = notes,
                Status = DocStatuses.Approved
            };
            foreach (var (pid, counted) in lines)
            {
                int book = BookCartons(pid, warehouseId);
                int diff = counted - book;
                doc.Items.Add(new CartonCountItem { ProductId = pid, BookCartons = book, CountedCartons = counted });
                if (diff != 0)
                {
                    AddStock(pid, warehouseId, diff);
                    Db.InventoryTransactions.Add(new InventoryTransaction
                    {
                        TxnNumber = Numbering.Next("CTX"),
                        WarehouseId = warehouseId,
                        ProductId = pid,
                        MovementType = MovementType.Adjustment,
                        PackageCount = diff,
                        ReferenceDocType = ReferenceDocType.CartonCount,
                        ReferenceDocNumber = doc.DocumentNumber,
                        IsApproved = true
                    });
                }
            }
            Db.CartonCountDocs.Add(doc);
            Db.SaveChanges();
            return OpResult.Success($"تم تسجيل سند العدّ {doc.DocumentNumber} وتقييد الفروق آلياً.", doc.Id, doc.DocumentNumber);
        });
    }

    // ═══ سند البيع ═══
    public OpResult CreateSaleDoc(int? customerId, int warehouseId, double price, string notes, List<(int productId, int cartons)> lines)
    {
        Require("cartons", "Create");
        if (lines == null || lines.Count == 0) return OpResult.Fail("أدخل كمية بيع واحدة على الأقل.");
        return RunOp(() =>
        {
            foreach (var (pid, cartons) in lines)
            {
                int book = BookCartons(pid, warehouseId);
                if (cartons > book)
                {
                    var name = Db.Products.AsNoTracking().Where(p => p.Id == pid).Select(p => p.ProductNameAr).FirstOrDefault();
                    throw new DomainException($"البيع فوق الرصيد مرفوض: المتاح من «{name}» {book} كرتون فقط.");
                }
            }
            var doc = new CartonSaleDoc
            {
                DocumentNumber = Numbering.Next("CSD"),
                SaleDate = DateTime.Now,
                CustomerId = customerId,
                WarehouseId = warehouseId,
                PricePerCarton = price,
                Notes = notes,
                Status = DocStatuses.Approved
            };
            double total = 0;
            foreach (var (pid, cartons) in lines)
            {
                AddStock(pid, warehouseId, -cartons);
                var amount = Math.Round(cartons * price, 2);
                total += amount;
                doc.Items.Add(new CartonSaleItem { ProductId = pid, Cartons = cartons, Amount = amount });
                Db.InventoryTransactions.Add(new InventoryTransaction
                {
                    TxnNumber = Numbering.Next("CTX"),
                    WarehouseId = warehouseId,
                    ProductId = pid,
                    CustomerId = customerId,
                    MovementType = MovementType.Outbound,
                    PackageCount = cartons,
                    ReferenceDocType = ReferenceDocType.CartonSale,
                    ReferenceDocNumber = doc.DocumentNumber,
                    IsApproved = true
                });
            }
            doc.TotalAmount = total;
            Db.CartonSaleDocs.Add(doc);
            Db.SaveChanges();
            return OpResult.Success($"تم بيع {lines.Sum(l => l.cartons)} كرتون بقيمة {total:N2} — السند {doc.DocumentNumber}.", doc.Id, doc.DocumentNumber);
        });
    }

    // ═══ كشف التسوية ═══
    public (int inbound, int sold, int adjusted) StatementTotals(int? warehouseId, DateTime? from, DateTime? to)
    {
        var q = Db.InventoryTransactions.AsNoTracking().Where(t =>
            t.ReferenceDocType == ReferenceDocType.CartonReturn || t.ReferenceDocType == ReferenceDocType.CartonSale || t.ReferenceDocType == ReferenceDocType.CartonCount);
        if (warehouseId != null) q = q.Where(t => t.WarehouseId == warehouseId);
        if (from != null) q = q.Where(t => t.TxnDate >= from);
        if (to != null) q = q.Where(t => t.TxnDate <= to.Value.AddDays(1));
        var list = q.ToList();
        return (list.Where(t => t.ReferenceDocType == ReferenceDocType.CartonReturn).Sum(t => t.PackageCount),
                list.Where(t => t.ReferenceDocType == ReferenceDocType.CartonSale).Sum(t => t.PackageCount),
                list.Where(t => t.ReferenceDocType == ReferenceDocType.CartonCount).Sum(t => t.PackageCount));
    }

    private void Require(string module, string action)
    {
        if (!Session.Can(module, action))
            throw new DomainException($"ليست لديك صلاحية {action} على {module}.");
    }

    private OpResult RunOp(Func<OpResult> f)
    {
        try { return f(); }
        catch (DomainException ex) { return OpResult.Fail(ex.Message); }
    }
}

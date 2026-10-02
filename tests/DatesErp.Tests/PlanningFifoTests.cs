using System;
using System.Collections.Generic;
using System.Linq;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// اختبارات تنبيه FIFO — دفعات أقدم لم تُخطط بعد (1.50.56).
/// </summary>
public class PlanningFifoTests
{
    [Fact]
    public void Fifo_Warns_When_Planning_Later_While_Earlier_Exists()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var planning = host.Get<IPlanningService>();

        // إنشاء عميلين ودفعتين بتاريخين مختلفين
        var cust1 = db.Customers.First();
        var cust2 = new DatesErp.Core.Domain.Entities.Customer { CustomerCode = "FIFO-C2",
            CustomerName = "عميل FIFO ثانٍ", IsActive = true };
        db.Customers.Add(cust2);
        db.SaveChanges();
        var prod = db.Products.First(p => p.ItemType == "Raw");

        // شحنتان بتاريخ وصول مختلف: 3 و 5 من الشهر
        var ship1 = new DatesErp.Core.Domain.Entities.Shipment
        {
            DocumentNumber = "SHIP-FIFO-1",
            CustomerId = cust1.Id,
            ArrivalDate = new DateTime(2026, 9, 3),
            ReceivedDate = new DateTime(2026, 9, 3),
            TotalWeightKg = 100,
            IsApproved = true
        };
        var ship2 = new DatesErp.Core.Domain.Entities.Shipment
        {
            DocumentNumber = "SHIP-FIFO-2",
            CustomerId = cust2.Id,
            ArrivalDate = new DateTime(2026, 9, 5),
            ReceivedDate = new DateTime(2026, 9, 5),
            TotalWeightKg = 100,
            IsApproved = true
        };
        db.Shipments.AddRange(ship1, ship2);
        db.SaveChanges();

        var lot1 = new DatesErp.Core.Domain.Entities.Lot
        {
            LotCode = "LOT-FIFO-1",
            ShipmentId = ship1.Id,
            ProductId = prod.Id,
            CustomerId = cust1.Id,
            InitialQtyKg = 100,
            InStockQtyKg = 100,
            Status = "Approved"
        };
        var lot2 = new DatesErp.Core.Domain.Entities.Lot
        {
            LotCode = "LOT-FIFO-2",
            ShipmentId = ship2.Id,
            ProductId = prod.Id,
            CustomerId = cust2.Id,
            InitialQtyKg = 100,
            InStockQtyKg = 100,
            Status = "Approved"
        };
        db.Lots.AddRange(lot1, lot2);
        db.SaveChanges();

        // المتاح يجب أن يكون مرتب FIFO: الأقدم أولاً
        var available = planning.GetAvailableLots(null, null);
        var fifo1 = available.FirstOrDefault(l => l.LotId == lot1.Id);
        var fifo2 = available.FirstOrDefault(l => l.LotId == lot2.Id);
        Assert.NotNull(fifo1);
        Assert.NotNull(fifo2);
        Assert.True(fifo1.ArrivalDate < fifo2.ArrivalDate, "يجب أن تكون دفعة 3 أقدم من 5");

        // عند محاولة التخطيط لدفعة يوم 5 بينما توجد دفعة يوم 3 لم تُخطط بعد → تنبيه
        var warning = planning.CheckFifoWarning(lot2.Id, new List<int>());
        Assert.True(warning.HasEarlier, "يجب أن ينبه لوجود دفعات أقدم");
        Assert.Contains(cust1.CustomerName, warning.Message);
        Assert.Contains("03/09/2026", warning.Message);
        Assert.Contains("05/09/2026", warning.Message);

        // عند التخطيط للأقدم (يوم 3) لا تنبيه
        var noWarn = planning.CheckFifoWarning(lot1.Id, new List<int>());
        Assert.False(noWarn.HasEarlier);

        // إذا كانت الدفعة الأقدم موجودة بالفعل في الخطة الحالية، لا تنبيه
        var withEarlierInPlan = planning.CheckFifoWarning(lot2.Id, new List<int> { lot1.Id });
        Assert.False(withEarlierInPlan.HasEarlier, "الأقدم موجود في الخطة الحالية — لا تنبيه");
    }

    [Fact]
    public void Fifo_No_Warning_When_Only_One_Lot()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var planning = host.Get<IPlanningService>();

        var cust = db.Customers.First();
        var prod = db.Products.First(p => p.ItemType == "Raw");
        var ship = new DatesErp.Core.Domain.Entities.Shipment
        {
            DocumentNumber = "SHIP-FIFO-SINGLE",
            CustomerId = cust.Id,
            ArrivalDate = new DateTime(2026, 9, 10),
            ReceivedDate = new DateTime(2026, 9, 10),
            TotalWeightKg = 50,
            IsApproved = true
        };
        db.Shipments.Add(ship);
        db.SaveChanges();

        var lot = new DatesErp.Core.Domain.Entities.Lot
        {
            LotCode = "LOT-FIFO-SINGLE",
            ShipmentId = ship.Id,
            ProductId = prod.Id,
            CustomerId = cust.Id,
            InitialQtyKg = 50,
            InStockQtyKg = 50,
            Status = "Approved"
        };
        db.Lots.Add(lot);
        db.SaveChanges();

        var warning = planning.CheckFifoWarning(lot.Id, new List<int>());
        Assert.False(warning.HasEarlier);
    }
}

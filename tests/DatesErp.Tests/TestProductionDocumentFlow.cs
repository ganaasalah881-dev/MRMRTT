using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Tests;

/// <summary>
/// Test-only adapter for the current document flow:
/// actual execution → issued production delivery → finished-goods receipt.
/// It keeps older domain-focused tests from bypassing the production-delivery document.
/// </summary>
internal static class TestProductionDocumentFlow
{
    internal static OpResult SaveReceiptFromActual(TestHost host, int orderId, int? qualityCheckId,
        string deliveryDate, List<FinishedGoodsItemDto> items, int? existingDeliveryId = null)
    {
        var db = host.Get<DatesErpDbContext>();
        var deliveryService = host.Get<IProductionDeliveryService>();
        int deliveryId = existingDeliveryId ?? 0;

        if (deliveryId == 0)
        {
            var execution = db.ProductionExecutions.AsNoTracking()
                .FirstOrDefault(e => e.OrderId == orderId && e.IsDayClosed && e.Status == DocStatuses.Completed);
            if (execution == null)
                return OpResult.Fail("اختبار التدفق يحتاج تنفيذ إنتاج فعلياً مكتملًا قبل إنشاء أمر التسليم.");

            var existing = db.ProductionDeliveries.AsNoTracking()
                .FirstOrDefault(d => d.SourceType == DeliverySources.FromActual
                    && d.SourceId == execution.Id && d.Status != DocStatuses.Cancelled);
            if (existing != null)
                deliveryId = existing.Id;
            else
            {
                host.LoginAs("production");
                var draft = deliveryService.CreateDeliveryFromActual(execution.Id, deliveryDate);
                if (!draft.Ok) return draft;
                var issue = deliveryService.IssueDelivery(draft.Id);
                if (!issue.Ok) return issue;
                deliveryId = draft.Id;
            }
        }

        host.LoginAs("warehouse");
        var card = deliveryService.GetDelivery(deliveryId);
        if (card == null) return OpResult.Fail("اختبار التدفق لم يجد أمر التسليم الإنتاجي.");

        var mapped = items?.Select(item =>
        {
            var line = item.DeliveryItemId is int explicitId
                ? card.Lines.FirstOrDefault(l => l.Id == explicitId)
                : card.Lines.FirstOrDefault(l => l.ProductId == item.ProductId
                    && (item.LotId == null || l.LotId == item.LotId)
                    && (item.CustomerId == null || l.CustomerId == item.CustomerId)
                    && l.RemainingQtyKg > 0.001);
            return new FinishedGoodsItemDto
            {
                ProductId = item.ProductId,
                LotId = line?.LotId ?? item.LotId,
                CustomerId = line?.CustomerId ?? item.CustomerId,
                PackagingTypeId = item.PackagingTypeId ?? line?.PackagingTypeId,
                PackageCount = item.PackageCount,
                NetWeightKg = item.NetWeightKg,
                DeliveryItemId = line?.Id ?? item.DeliveryItemId
            };
        }).ToList() ?? new List<FinishedGoodsItemDto>();

        return host.Get<IFinishedGoodsService>().SaveReceipt(orderId, qualityCheckId,
            deliveryDate, mapped, deliveryId);
    }
}

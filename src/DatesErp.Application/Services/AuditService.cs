using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using System.Text.Json;

namespace DatesErp.Application.Services;

/// <summary>§26 — التدقيق المركزي داخل نفس معاملات العمليات.</summary>
public class AuditService : IAuditService
{
    private readonly DatesErpDbContext _db;
    private readonly ICurrentSession _session;

    public AuditService(DatesErpDbContext db, ICurrentSession session)
    {
        _db = db;
        _session = session;
    }

    public void Log(string screen, string action, string docType, string docNumber, int? recordId = null, object oldValues = null, object newValues = null)
    {
        try
        {
            _db.AuditLogs.Add(new AuditLog
            {
                UserId = _session?.UserId,
                UserName = _session?.UserName ?? "system",
                ComputerName = Environment.MachineName,
                MachineName = Environment.MachineName,
                ActionDate = DateTime.Now,
                ScreenName = screen,
                ActionType = action,
                DocumentType = docType,
                DocumentNumber = docNumber,
                RecordId = recordId,
                OldValue = oldValues == null ? null : JsonSerializer.Serialize(oldValues),
                NewValue = newValues == null ? null : JsonSerializer.Serialize(newValues)
            });
            _db.SaveChanges();
        }
        catch
        {
            // لا يفشل العمل بسبب فشل التسجيل
        }
    }

    public List<AuditLog> Query(DateTime? from, DateTime? to, string user = null, string action = null)
    {
        var q = _db.AuditLogs.AsQueryable();
        if (from != null) q = q.Where(a => a.ActionDate >= from);
        if (to != null) q = q.Where(a => a.ActionDate <= to.Value.AddDays(1));
        if (!string.IsNullOrEmpty(user)) q = q.Where(a => a.UserName == user);
        if (!string.IsNullOrEmpty(action)) q = q.Where(a => a.ActionType == action);
        return q.OrderByDescending(a => a.ActionDate).Take(2000).ToList();
    }
}

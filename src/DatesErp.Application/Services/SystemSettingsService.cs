using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;

namespace DatesErp.Application.Services;

/// <summary>§44 — خدمة إعدادات النظام: إنشاء/تحديث مفتاح داخل معاملة موحّدة وتدقيق.</summary>
public class SystemSettingsService : ServiceBase, ISystemSettingsService
{
    public SystemSettingsService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    public OpResult Set(string key, string value)
        => RunOp(() =>
        {
            if (string.IsNullOrWhiteSpace(key)) throw new DomainException("مفتاح الإعداد مطلوب.");
            var s = Db.SystemSettings.FirstOrDefault(x => x.SettingKey == key);
            if (s == null)
                Db.SystemSettings.Add(new SystemSetting { SettingKey = key, SettingValue = value, Category = "System" });
            else
                s.SettingValue = value;
            Db.SaveChanges();
            return OpResult.Success($"تم حفظ الإعداد {key}.");
        });
}
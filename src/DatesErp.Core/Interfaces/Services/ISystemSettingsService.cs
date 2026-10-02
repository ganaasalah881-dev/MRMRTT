using DatesErp.Core.Common;

namespace DatesErp.Core.Interfaces.Services;

/// <summary>§44 — كتابة إعدادات النظام تمر بطبقة الخدمات وحدها (حارس الكتابة الطبقية).</summary>
public interface ISystemSettingsService
{
    OpResult Set(string key, string value);
}

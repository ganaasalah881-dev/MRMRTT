using System.Text.Json;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// مدير ثيمات النظام. تُخزّن الكتالوجات في SystemSettings كـ JSON حتى لا يتغير مخطط
/// قاعدة البيانات المستقرة، بينما تمر كل عمليات الكتابة من خلال الصلاحيات والمعاملة الموحدة.
/// </summary>
public sealed class ThemeSettingsService : ServiceBase, IThemeSettingsService
{
    private const string CatalogKey = "Theme.Catalog";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = false };

    public ThemeSettingsService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    public ThemeCatalogDto Load()
    {
        try
        {
            var raw = Db.SystemSettings.AsNoTracking()
                .Where(x => x.SettingKey == CatalogKey)
                .Select(x => x.SettingValue)
                .FirstOrDefault();
            var catalog = string.IsNullOrWhiteSpace(raw)
                ? ThemeProfileDefaults.CreateCatalog()
                : JsonSerializer.Deserialize<ThemeCatalogDto>(raw, JsonOptions) ?? ThemeProfileDefaults.CreateCatalog();
            return Normalize(catalog);
        }
        catch
        {
            return ThemeProfileDefaults.CreateCatalog();
        }
    }

    public OpResult Save(ThemeProfileDto profile, bool activate = true)
    {
        Require("settings", "Edit");
        if (profile == null) return OpResult.Fail("بيانات الثيم غير موجودة.");
        profile = profile.Clone();
        profile.Name = (profile.Name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(profile.Name)) return OpResult.Fail("اسم الثيم مطلوب.");
        if (profile.Name.Length > 80) return OpResult.Fail("اسم الثيم طويل جداً.");

        return RunOp(() =>
        {
            var catalog = Load();
            var existing = catalog.Profiles.FirstOrDefault(x => string.Equals(x.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
            if (existing != null) catalog.Profiles[catalog.Profiles.IndexOf(existing)] = profile;
            else catalog.Profiles.Add(profile);
            if (activate) catalog.ActiveName = profile.Name;
            Write(catalog);
            return OpResult.Success($"تم حفظ الثيم «{profile.Name}».");
        });
    }

    public OpResult Activate(string name)
    {
        Require("settings", "Edit");
        return RunOp(() =>
        {
            var catalog = Load();
            var profile = catalog.Profiles.FirstOrDefault(x => string.Equals(x.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (profile == null) return OpResult.Fail("الثيم المطلوب غير موجود.");
            catalog.ActiveName = profile.Name;
            Write(catalog);
            return OpResult.Success($"تم تفعيل الثيم «{profile.Name}».");
        });
    }

    public OpResult Delete(string name)
    {
        Require("settings", "Edit");
        if (string.Equals(name?.Trim(), "الافتراضي", StringComparison.OrdinalIgnoreCase))
            return OpResult.Fail("لا يمكن حذف الثيم الافتراضي.");
        return RunOp(() =>
        {
            var catalog = Load();
            var profile = catalog.Profiles.FirstOrDefault(x => string.Equals(x.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (profile == null) return OpResult.Fail("الثيم المطلوب غير موجود.");
            catalog.Profiles.Remove(profile);
            if (string.Equals(catalog.ActiveName, profile.Name, StringComparison.OrdinalIgnoreCase))
                catalog.ActiveName = catalog.Profiles.FirstOrDefault()?.Name ?? "الافتراضي";
            Write(catalog);
            return OpResult.Success("تم حذف الثيم.");
        });
    }

    private void Write(ThemeCatalogDto catalog)
    {
        var setting = Db.SystemSettings.FirstOrDefault(x => x.SettingKey == CatalogKey);
        var json = JsonSerializer.Serialize(Normalize(catalog), JsonOptions);
        if (setting == null)
            Db.SystemSettings.Add(new SystemSetting { SettingKey = CatalogKey, SettingValue = json, Category = "Theme", DataType = "Json", Description = "ثيمات واجهة النظام" });
        else
        {
            setting.SettingValue = json;
            setting.Category = "Theme";
            setting.DataType = "Json";
        }
        Db.SaveChanges();
    }

    private static ThemeCatalogDto Normalize(ThemeCatalogDto catalog)
    {
        catalog ??= ThemeProfileDefaults.CreateCatalog();
        catalog.Profiles ??= new List<ThemeProfileDto>();
        var builtIns = ThemeProfileDefaults.CreateCatalog().Profiles;
        foreach (var builtIn in builtIns)
        {
            if (!catalog.Profiles.Any(x => string.Equals(x?.Name, builtIn.Name, StringComparison.OrdinalIgnoreCase)))
                catalog.Profiles.Insert(0, builtIn);
        }
        catalog.Profiles = catalog.Profiles.Where(x => x != null).ToList();
        var active = catalog.Profiles.FirstOrDefault(x => string.Equals(x.Name, catalog.ActiveName, StringComparison.OrdinalIgnoreCase));
        catalog.ActiveName = active?.Name ?? catalog.Profiles.FirstOrDefault()?.Name ?? "الافتراضي";
        return catalog;
    }
}

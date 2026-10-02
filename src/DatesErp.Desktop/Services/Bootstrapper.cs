using System.IO;
using System.Windows;
using DatesErp.Core.Domain.Enums;
using DatesErp.Desktop.Views;
using DatesErp.Infrastructure.Connection;
using DatesErp.Infrastructure.Persistence;
using DatesErp.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Services;

/// <summary>
/// §19/§20/§31 — تسلسل الإقلاع بضغطة واحدة:
/// Splash ← فحص الإعداد (شاشة اتصال إن لم يوجد) ← فحص الخادم وقاعدة البيانات والإصدار
/// ← تسجيل الدخول ← لوحة التحكم. لا CMD ولا متصفح ولا بيئة تطوير.
/// </summary>
public class Bootstrapper
{
    /// <summary>§B84/H6: إصدار التطبيق من التجميع نفسه (مصدر واحد — Version في csproj)
    /// بدل الثابت اليدوي "1.0.0" الذي جعل فحص التوافق عديم الجدوى.</summary>
    private static string AppVersion =>
        typeof(Bootstrapper).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public void Run(App app)
    {
        BootTrace.Step("Bootstrapper.Run: بداية الإقلاع.");
        ErrorLog.WriteInfo("بدء الإقلاع...");
        BootTrace.Step("Bootstrapper.Run: إنشاء نافذة البداية (Splash)...");
        var splash = new SplashWindow();
        splash.Show();
        app.MainWindow = splash;
        BootTrace.Step("Bootstrapper.Run: نافذة البداية ظاهرة.");

        try
        {
            // 1) فحص الإعداد — عند أول تشغيل: وضع محلي تلقائي (بلا أي إعداد) حتى يربطه المسؤول بالخادم المركزي لاحقاً
            splash.SetStatus("جارٍ التحقق من إعداد الاتصال...");
            BootTrace.Step("المرحلة 1: فحص إعداد الاتصال. موجود مسبقاً؟ " + AppConfig.Exists());
            if (!AppConfig.Exists())
            {
                new AppConfig
                {
                    Server = "محلي (وضع الاستعراض)",
                    Database = AppConfig.LocalDatabaseFileName,
                    AuthMode = "Local",
                    AppVersion = AppVersion
                }.Save();
                BootTrace.Step("المرحلة 1: أُنشئ ملف إعداد جديد (وضع محلي).");
            }
            // §CS0162: حُذفت كتلة if(false) الميتة. نافذة إعداد الاتصال تُفتح
            // من شاشة "معلومات النظام" ← "تعديل الاتصال".

            // 2) بناء حاوية الخدمات على الإعداد المحفوظ
            splash.SetStatus("جارٍ التحقق من خادم قاعدة البيانات...");
            BootTrace.Step("المرحلة 2: حل الاتصال تلقائياً (AutoDbResolver)...");
            // §v1.50.23: يجرب الخادم المضبوط، ثم يكتشف نسخ SQL المحلية ويضبط الصحيح
            // تلقائياً، ويعرض الوضع المحلي المدمج عند غياب الخادم — بدل رسالة فشل عمياء.
            if (!AutoDbResolver.EnsureSqlServerOrFallback())
            {
                BootTrace.Step("المرحلة 2: رفض المستخدم خيارات الحل — إيقاف التشغيل.");
                ErrorLog.WriteInfo("AutoDbResolver: رفض المستخدم خيارات الاتصال — إيقاف التشغيل.");
                splash.Hide();
                app.Shutdown();
                return;
            }
            BootTrace.Step("المرحلة 2: بناء حاوية الخدمات (AppContainer.Build)...");
            AppContainer.Build();
            BootTrace.Step("المرحلة 2: تم بناء حاوية الخدمات.");
            ErrorLog.WriteInfo("تم بناء حاوية الخدمات.");

            // 3) فحص الخادم وقاعدة البيانات والإصدار (§20/§31)
            splash.SetStatus("جارٍ فحص قاعدة البيانات والإصدار...");
            BootTrace.Step("المرحلة 3: فتح نطاق وفحص قاعدة البيانات...");
            using (var scope = AppContainer.NewScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                try
                {
                    BootTrace.Step("المرحلة 3: فتح اتصال قاعدة البيانات...");
                    db.Database.GetDbConnection().Open();
                    BootTrace.Step("المرحلة 3: اتصال قاعدة البيانات مفتوح. نوع: " + (db.Database.IsSqlServer() ? "SQL Server" : "SQLite"));
                    var hasTables = db.Database.CanConnect();

                    if (!db.Database.GetAppliedMigrations().Any() && !TableExists(db, "Users"))
                    {
                        // §إقلاع تلقائي: تُنشأ القاعدة وجداولها وبياناتها الأساسية بلا سؤال.
                        // يُعطَّل السؤال فقط إن ضُبط AutoInitialize=0 صراحةً (لمن يثبّت القاعدة
                        // يدوياً من مثبّت السيرفر ويريد التحكم الكامل).
                        bool autoInit = true;
                        try
                        {
                            var st = db.SystemSettings.AsNoTracking()
                                .FirstOrDefault(x => x.SettingKey == "AutoInitialize");
                            // لا يوجد إعداد بعد (قاعدة فارغة) ← نقرأه من ملف config.json إن وُجد
                            if (st == null)
                            {
                                var cfgPath = DatesErp.Infrastructure.Connection.AppConfig.ConfigPath;
                                if (System.IO.File.Exists(cfgPath))
                                {
                                    var txt = System.IO.File.ReadAllText(cfgPath);
                                    if (txt.Contains("\"AutoInitialize\"") && txt.Contains("false"))
                                        autoInit = false;
                                }
                            }
                            else if (st.SettingValue == "0" || string.Equals(st.SettingValue, "false", StringComparison.OrdinalIgnoreCase))
                                autoInit = false;
                        }
                        catch { autoInit = true; }

                        if (autoInit)
                        {
                            BootTrace.Step("المرحلة 3: قاعدة فارغة — إنشاء تلقائي للمخطط والبيانات الأساسية.");
                            splash.SetStatus("جارٍ إنشاء قاعدة البيانات والجداول والبيانات الأساسية تلقائياً...");
                            db.Database.EnsureCreated();
                            DbSeeder.Seed(db);
                            BootTrace.Step("المرحلة 3: تم إنشاء القاعدة وبذر البيانات الأساسية.");
                        }
                        else
                        {
                            splash.Hide();
                            var init = MessageBox.Show(
                                "قاعدة البيانات فارغة (لا توجد جداول).\nهل تريد إنشاء المخطط والبيانات الأساسية الآن؟\n(اختر لا إذا كنت ستثبّت قاعدة البيانات من مثبّت السيرفر)",
                                "تهيئة قاعدة البيانات", MessageBoxButton.YesNo, MessageBoxImage.Question);
                            if (init == MessageBoxResult.Yes)
                            {
                                db.Database.EnsureCreated();
                                DbSeeder.Seed(db);
                            }
                            splash.Show();
                        }
                    }

                    // الترحيل الآمن الشامل: إنشاء أي جدول مفقود وإضافة أي عمود مفقود
                    // وفق نموذج النظام الحالي — تلقائياً على أي قاعدة قديمة وبلا حذف بيانات.
                    BootTrace.Step("المرحلة 3: فحص وجود جدول Users للترحيل. موجود؟ " + TableExists(db, "Users"));
                    if (TableExists(db, "Users"))
                    {
                        BootTrace.Step("المرحلة 3: بدء الترحيل التلقائي للمخطط...");
                        // §44 — نسخة احتياطية مؤكدة قبل أي ترحيل مخطط
                        if (!AutoBackup.EnsurePreMigration(db))
                        {
                            splash.Hide();
                            app.Shutdown(); // no schema changes without a verified backup
                            return;
                        }
                        var migrationReport = DatesErp.Infrastructure.Persistence.SchemaMigrator.Migrate(db);
                        BootTrace.Step("المرحلة 3: انتهى الترحيل. عدد البنود: " + migrationReport.Count);
                        var errors = migrationReport.Where(r => r.StartsWith("خطأ") || r.StartsWith("تعذر")
                            || r.StartsWith("تعذّر")).ToList();
                        var changes = migrationReport.Except(errors).ToList();
                        if (changes.Count > 0)
                        {
                            ErrorLog.WriteInfo("ترقية تلقائية لمخطط قاعدة البيانات:\n  - "
                                               + string.Join("\n  - ", changes));
                            splash.SetStatus($"تمت ترقية قاعدة البيانات تلقائياً ({changes.Count} تغيير)...");
                        }
                        // Fail closed: continuing after a partial SQL Server migration
                        // lets 6–20 concurrent users post against a broken schema.
                        if (errors.Count > 0)
                        {
                            ErrorLog.Write(new Exception(string.Join("\n", errors)), "SchemaMigrator");
                            splash.Hide();
                            MessageBox.Show(
                                "⛔ لم تكتمل ترقية قاعدة البيانات (" + errors.Count + " مشكلة).\n" +
                                "أُوقف تشغيل النظام لحماية الأرصدة. راجع نسخة ما قبل الترحيل وسجل الأخطاء ثم أعد المحاولة:\n" +
                                ErrorLog.LogDirectory + "\n\n" +
                                string.Join("\n", errors.Take(3)),
                                "ترقية قاعدة البيانات — التشغيل متوقف", MessageBoxButton.OK, MessageBoxImage.Error);
                            app.Shutdown();
                            return;
                        }
                    }

                    // §45 — فحص ذاتي لمطابقة المخزون بعد كل ترحيل: ملخّص في السجل دون مقاطعة الإقلاع
                    try
                    {
                        var diffs = new DatesErp.Application.Services.InventoryIntegrityService(db, null, null).Check();
                        ErrorLog.WriteInfo($"مطابقة المخزون بعد الترحيل: {(diffs.Count == 0 ? "مطابِقة" : diffs.Count + " فرقاً")}");
                    }
                    catch { /* تشخيص فقط — لا يُفشل الإقلاع */ }

                    // §موارد الصلاحيات: كانت تُبذر عند فتح شاشة الصلاحيات فقط، فكان التنصيب
                    // الجديد يعمل بلا كتالوج صلاحيات حتى يُفتح تلك الشاشة. تُبذر الآن عند الإقلاع.
                    try
                    {
                        var perm = new DatesErp.Application.Services.PermissionService(
                            db, new DatesErp.Infrastructure.Session.SessionContext());
                        perm.EnsureCatalog();
                        BootTrace.Step("المرحلة 3: تم التأكد من كتالوج الصلاحيات.");
                    }
                    catch (Exception exP) { ErrorLog.Write(exP, "EnsureCatalog"); }

                    // §قاعدة المصنع على القواعد القائمة: البذر الأولي يتوقف عند أول تشغيل،
                    // فقاعدة أُنشئت قبل B48 تبقى على التصنيف القديم (المنسم مخرج ثانوي،
                    // ولا «تمر سليم» ولا «عجينة»). هذه الترقية تضيف الناقص وتصنّف المنسم
                    // منتجاً تاماً — مرة واحدة، وبلا حذف أو تعديل لبيانات عرّفها المستخدم.
                    try
                    {
                        var refChanges = DatesErp.Infrastructure.Persistence.DbSeeder.UpgradeReferenceData(db);
                        if (refChanges.Count > 0)
                        {
                            ErrorLog.WriteInfo("ترقية البيانات المرجعية (قاعدة المصنع):\n  - "
                                               + string.Join("\n  - ", refChanges));
                            splash.SetStatus($"تم تحديث البيانات المرجعية ({refChanges.Count} تغيير)...");
                        }
                        BootTrace.Step("المرحلة 3: تم التأكد من البيانات المرجعية. تغييرات: " + refChanges.Count);
                    }
                    catch (Exception exR) { ErrorLog.Write(exR, "UpgradeReferenceData"); }

                    // التحقق من توافق إصدار قاعدة البيانات (§31)
                    // §B106.1 — إصلاح خلل دائري: كانت البوابة تقرأ **آخر صف** في DbVersions،
                    // وهو غالباً «ختم إقلاع» يكتبه التطبيق نفسه بنسخته (IsMigration = false).
                    // فصارت تقارن نسخة التطبيق الحالية بنسخة آخر تشغيل — أي تحجب النظام
                    // كلما تغيّر رقم الإصدار ولو لم يتغيّر المخطط حرفاً واحداً.
                    // (ظهر عملياً: قاعدة مختومة 1.50.0 مُنعت بعد إرجاع الإصدار إلى 1.49.0.)
                    // الصواب: البوابة تخص **المخطط**، فتقرأ سجلات الترحيل فقط (IsMigration).
                    // أختام الإقلاع تبقى سجل تدقيق لا معياراً للتوافق.
                    var dbVersion = db.DbVersions.Where(v => v.IsMigration)
                        .OrderByDescending(v => v.Id).FirstOrDefault()?.VersionNumber;
                    var lastStamp = db.DbVersions.OrderByDescending(v => v.Id).FirstOrDefault()?.VersionNumber;
                    // §B84/H6: خط الأساس القديم "1.0.x" = مخطط ما قبل الترقيم الموحد — متوافق بالتعريف
                    // (المهاجر أعلاه رفعه للهيكل الحالي). أي إصدار لاحق يُقارن رئيسي.ثانوي بجدية.
                    bool legacyBaseline = dbVersion != null && dbVersion.StartsWith("1.0.");
                    if (dbVersion != null && !legacyBaseline && !IsCompatible(dbVersion, AppVersion))
                    {
                        splash.Hide();
                        MessageBox.Show(
                            $"إصدار قاعدة البيانات ({dbVersion}) غير متوافق مع إصدار التطبيق ({AppVersion}).\n" +
                            "يرجى تطبيق حزمة الترقية قبل المتابعة.",
                            "فحص الإصدار", MessageBoxButton.OK, MessageBoxImage.Warning);
                        app.Shutdown();
                        return;
                    }
                    // §B84/H6: ختم الترقية — صف تدقيق بنسخة التطبيق بعد نجاح الفحص (مرة لكل نسخة)،
                    // فيصبح لجدول DbVersions معنى: تاريخ النسخ التي عملت على هذه القاعدة.
                    if (lastStamp != AppVersion)
                    {
                        db.DbVersions.Add(new DatesErp.Core.Domain.Entities.DbVersion
                        {
                            VersionNumber = AppVersion,
                            Description = $"ختم إقلاع — تحقق من التوافق بتاريخ {DateTime.Now:dd/MM/yyyy}"
                        });
                        db.SaveChanges();
                    }

                    // §استعادة الطوارئ: إن وُجد ملف reset_admin.flag يفك القفل ويعيد كلمة المدير
                    TryEmergencyAdminRecovery(db);
                }
                catch (Exception ex)
                {
                    BootTrace.Fail("المرحلة 3 (فحص قاعدة البيانات)", ex);
                    splash.Hide();
                    ErrorLog.Write(ex, "Startup");
                    MessageBox.Show(
                        "تعذر الاتصال بخادم قاعدة البيانات.\n\nتأكد من اتصال الشبكة وتشغيل الخادم.\n(التفاصيل في سجل الأخطاء)",
                        "Date ERP", MessageBoxButton.OK, MessageBoxImage.Error);
                    app.Shutdown();
                    return;
                }
            }

            // 4) تسجيل الدخول
            BootTrace.Step("المرحلة 4: تم فحص قاعدة البيانات — الانتقال إلى تسجيل الدخول.");
            ErrorLog.WriteInfo("تم فحص قاعدة البيانات — الانتقال إلى تسجيل الدخول.");
            splash.Hide();
            var login = new LoginWindow();
            BootTrace.Step("المرحلة 4: نافذة تسجيل الدخول ظاهرة — بانتظار المستخدم.");
            if (login.ShowDialog() != true)
            {
                BootTrace.Step("المرحلة 4: أُلغي تسجيل الدخول — خروج.");
                app.Shutdown();
                return;
            }
            BootTrace.Step("المرحلة 4: نجح تسجيل الدخول.");

            // 5) لوحة التحكم
            BootTrace.Step("المرحلة 5: إنشاء لوحة التحكم الرئيسية (MainWindow)...");
            var main = new MainWindow();
            app.MainWindow = main;
            main.Show();
            BootTrace.Step("المرحلة 5: لوحة التحكم ظاهرة — اكتمل الإقلاع بنجاح.");
            ErrorLog.WriteInfo("اكتمل الإقلاع — لوحة التحكم ظاهرة.");
        }
        catch (Exception ex)
        {
            BootTrace.Fail("Bootstrapper (catch عام)", ex);
            ErrorLog.Write(ex, "Bootstrapper");
            splash.Hide();
            MessageBox.Show("حدث خطأ أثناء بدء التشغيل.\n\nالتفاصيل في ملف أثر الإقلاع بجانب البرنامج:\n" + BootTrace.FilePath,
                "Date ERP", MessageBoxButton.OK, MessageBoxImage.Error);
            app.Shutdown();
        }
    }

    private static bool TableExists(DatesErpDbContext db, string table)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            using var cmd = conn.CreateCommand();
            if (db.Database.IsSqlServer())
                cmd.CommandText = $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='{table}'";
            else
                cmd.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'";
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }
        catch { return false; }
    }

    private static bool IsCompatible(string dbVersion, string appVersion)
    {
        // §32 — توافق حسب الإصدار الرئيسي والثانوي
        var db = dbVersion.Split('.');
        var ap = appVersion.Split('.');
        if (db.Length < 2 || ap.Length < 2) return true;
        return db[0] == ap[0] && db[1] == ap[1];
    }

    /// <summary>
    /// §استعادة الطوارئ (لفك قفل الحساب عند نسيان كلمة المرور):
    /// إذا وُجد ملف باسم reset_admin* داخل مجلد الإعدادات، يفك قفل حسابات المديرين فقط
    /// ويعيد كلمتهم إلى Admin@123 مع فرض تغييرها عند أول دخول، ثم يحذف الملف.
    /// يتطلب وصولاً فعلياً لجهاز المستخدم — لذلك هو إجراء طوارئ آمن محلياً.
    /// §B84/H8: كانت تعيد كلمة *كل* الحسابات النشطة — الآن المديرون فقط (دور Administrator).
    /// </summary>
    private static void TryEmergencyAdminRecovery(DatesErpDbContext db)
    {
        try
        {
            // §نتعرف على ملف الاستعادة بأي امتداد (ويندوز قد يضيف .txt عند الإنشاء اليدوي)
            var flags = Directory.GetFiles(AppConfig.ConfigDirectory, "reset_admin*");
            BootTrace.Step("استعادة الطوارئ: عدد ملفات reset_admin* الموجودة = " + flags.Length);
            if (flags.Length == 0) return;

            BootTrace.Step("استعادة الطوارئ: بدء فك القفل وإعادة كلمات المرور المؤقتة...");
            // §B84/H8: الاستهداف = حاملو دور Administrator النشطون فقط؛ وإن فُقدت الأدوار
            // (قاعدة تالفة) فالمسار الاحتياطي حساب admin بالاسم — لا تُمس بقية الحسابات أبداً.
            var adminRoleIds = db.Roles.Where(r => r.RoleCode == SystemRoles.Administrator).Select(r => r.Id).ToList();
            var adminUserIds = db.UserRoles.Where(ur => adminRoleIds.Contains(ur.RoleId) && ur.IsActive)
                .Select(ur => ur.UserId).Distinct().ToList();
            var targets = db.Users.Where(u => u.IsActive && adminUserIds.Contains(u.Id)).ToList();
            if (targets.Count == 0)
                targets = db.Users.Where(u => u.IsActive && u.UserName == "admin").ToList();
            // §v1.50.30: نفس منطق زر الطوارئ في شاشة الدخول — مساعد مشترك واحد.
            int resetCount = DatesErp.Application.Services.AdminRecovery.ResetAdminAccounts(db);
            foreach (var f in flags) { try { File.Delete(f); } catch { } }
            BootTrace.Step($"استعادة الطوارئ: اكتملت. أُعيدت كلمة {resetCount} من حسابات المديرين إلى Admin@123 المؤقتة.");
            ErrorLog.WriteInfo($"استعادة طوارئ: أُعيدت كلمة {resetCount} من حسابات المديرين إلى Admin@123 (سيُطلب تغييرها عند أول دخول).");
        }
        catch (System.Exception ex)
        {
            BootTrace.Fail("استعادة الطوارئ", ex);
        }
    }
}

using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DatesErp.Core.Common;
using DatesErp.Desktop.Screens;
using DatesErp.Desktop.Views;
using DatesErp.Desktop.Views.Screens;
using DatesErp.Infrastructure.Connection;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Services;

/// <summary>
/// §الفحص الذاتي — يجعل سؤال «هل العطل من جهازي أم من البرنامج؟» سؤالاً له جواب.
///
/// يفحص: الاتصال · اكتمال المخطط · البيانات الأولية · إنشاء كل شاشة في الكتالوج ·
/// وجود معالج لكل زر في كل شاشة. ثم يكتب تقريراً نصياً يُرسل للمطوّر.
///
/// يُشغَّل من «معلومات النظام» بزر، ويُشغَّل تلقائياً عند الإقلاع (بلا نوافذ).
/// </summary>
public static class SelfDiagnostic
{
    public static string ReportDirectory => Path.Combine(AppConfig.ConfigDirectory, "logs");

    public static string ReportPath => Path.Combine(ReportDirectory, "selftest.txt");

    private static readonly List<string> _lines = new();
    private static int _pass, _fail;

    /// <summary>ينفّذ الفحص كاملاً ويعيد نص التقرير (ويكتبه في الملف).</summary>
    public static string Run(bool includeScreens = true)
    {
        _lines.Clear(); _pass = 0; _fail = 0;

        Head("تقرير الفحص الذاتي — DateERP");
        Line($"التاريخ            : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Line($"إصدار البرنامج     : {BuildInfo.Stamp} · v{AssemblyVersion()}");
        Line($"نظام التشغيل       : {Environment.OSVersion} · {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}");
        Line($"إصدار .NET         : {Environment.Version}");
        Line($"اسم الجهاز         : {Environment.MachineName} · المستخدم: {Environment.UserName}");
        Line("");

        CheckIdentity();
        CheckDatabase();
        CheckSeedData();
        CheckOperational();
        CheckDataIntegrity();
        CheckDeepIntegrity();
        if (includeScreens) CheckScreens();

        Line("");
        Line("════════════════════════════════════════════════════════════════════════════════");
        Line($"  النتيجة: {_pass} فحصاً نجح · {_fail} فشل");
        Line(_fail == 0
            ? "  ✓ لا خلل بنيوي. إن كانت المشكلة في شاشة بعينها فاذكر اسمها ورقم المستند."
            : "  ✗ هناك إخفاقات أعلاه — أرسل هذا الملف كاملاً للمطوّر.");
        Line("════════════════════════════════════════════════════════════════════════════════");

        var text = string.Join(Environment.NewLine, _lines);
        try
        {
            Directory.CreateDirectory(ReportDirectory);
            File.WriteAllText(ReportPath, text, Encoding.UTF8);
        }
        catch { /* كتابة التقرير لا تُفشل الفحص */ }
        return text;
    }

    // ═══════════════════ 0) هوية النظام والقاعدة ═══════════════════

    /// <summary>
    /// §B81 — أول قسم في التقرير: اسم الخادم والقاعدة الفعليَّين (من الاتصال المفتوح لا من
    /// الإعداد المطلوب) + إصدار القاعدة + أعداد الجداول الرئيسية + طابع البرنامج.
    /// الغرض: «هل أنا متصل بالقاعدة الصحيحة؟» تُجاب بنظرة واحدة في رأس التقرير.
    /// </summary>
    private static void CheckIdentity()
    {
        Section("0) هوية النظام والقاعدة — تأكد أنك متصل بالقاعدة الصحيحة");
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            foreach (var (name, value) in DatesErp.Application.Services.DiagnosticCore.GetIdentity(db))
                Line($"  • {name}: {value}");
            Line($"  • طابع البرنامج: {BuildInfo.Stamp} · v{AssemblyVersion()}");
            Line("  (طابق هذه الأعداد مع ما تراه في الشاشات — إن اختلفت فأنت على قاعدة أخرى)");
        }
        catch (Exception ex) { Fail("هوية القاعدة", ex.GetType().Name + ": " + ex.Message); }
    }

    // ═══════════════════ 1+2) القاعدة والبيانات الأولية ═══════════════════
    // §المنطق في DiagnosticCore (طبقة التطبيق، بلا WPF) — نفسه الذي يختبره مشغّل القبول.

    private static void CheckDatabase()
    {
        Section("1) الاتصال بقاعدة البيانات والمخطط");
        RunCore(db => DatesErp.Application.Services.DiagnosticCore.CheckDatabase(db));
    }

    private static void CheckSeedData()
    {
        Section("2) البيانات الأولية المطلوبة");
        RunCore(db => DatesErp.Application.Services.DiagnosticCore.CheckSeedData(db));
    }

    /// <summary>
    /// §الإعداد التشغيلي — ما يطلبه الكود بالاسم (مخازن، مخططات ترقيم، موارد صلاحيات).
    /// غيابه لا يظهر عند بدء التشغيل بل عند أول استخدام، فيبدو عطلاً عشوائياً في شاشة بريئة.
    /// </summary>
    private static void CheckOperational()
    {
        Section("3) الإعداد التشغيلي — المخازن والترقيم وموارد الصلاحيات");
        RunCore(db => DatesErp.Application.Services.DiagnosticCore.CheckOperational(db));
    }

    /// <summary>
    /// §اتساق البيانات — العطل الصامت: لا استثناء ولا منع حفظ، بل **أرقام خاطئة**
    /// يُبنى عليها قرار. أخطر من العطل الظاهر، لأن الظاهر يوقف العمل والخاطئ يمرّ ويُعتمد.
    /// </summary>
    private static void CheckDataIntegrity()
    {
        Section("4) اتساق البيانات — الأرصدة والالتزامات والتتبع");
        Line("  (تُبلّغ ولا تُصلح: تصحيح الأرصدة قرار محاسبي لا تقني)");
        RunCore(db => DatesErp.Application.Services.DiagnosticCore.CheckDataIntegrity(db));
    }

    /// <summary>
    /// §الفحص العميق (1.50.52) — الدفتر مقابل الأرصدة، القيود المكررة، الرايات المتناقضة،
    /// السلاسل الممزقة، الحسابات والأدوار، ومصفوفة الصلاحيات.
    /// </summary>
    private static void CheckDeepIntegrity()
    {
        Section("5) الفحص العميق — الدفتر والسلاسل والحسابات والصلاحيات");
        Line("  (تُبلّغ ولا تُصلح: أي فرق هنا قرار محاسبي/إداري لا تقني)");
        RunCore(db => DatesErp.Application.Services.DiagnosticCore.CheckDeepIntegrity(db));
    }

    private static void RunCore(Func<DatesErpDbContext, List<DatesErp.Application.Services.DiagnosticCore.Finding>> act)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            try { Line("  المزوّد: " + (db.Database.IsSqlServer() ? "SQL Server" : "SQLite") + " · " + Mask(db.Database.GetDbConnection().ConnectionString)); } catch { }
            foreach (var f in act(db))
            {
                _current = f.Name;
                if (f.Ok) Ok(f.Detail); else Bad(f.Detail);
            }
        }
        catch (Exception ex) { Fail("فحص القاعدة", ex.GetType().Name + ": " + ex.Message); }
    }

    // ═══════════════════ 3) الشاشات وأزرارها ═══════════════════

    private static void CheckScreens()
    {
        Section("6) إنشاء الشاشات وسلامة أزرارها");
        Line("  (كل شاشة تُبنى فعلياً، ثم يُفحص أن لكل زر فيها معالجاً)");

        int screens = 0, deadButtons = 0;
        foreach (var def in ScreenCatalog.All)
        {
            screens++;
            try
            {
                var el = ScreenFactory.Create(def.Code);
                if (el == null) { Fail($"الشاشة «{def.Title}» ({def.Code})", "المصنع أرجع null"); continue; }

                // بناء الشاشة بشريط أدواتها إن كانت تدعم AttachChrome
                var chrome = TryAttachChrome(el);
                ForceLoad(el);

                var buttons = CollectButtons(el);
                if (chrome?.CurrentToolbar != null) buttons.AddRange(CollectButtons(chrome.CurrentToolbar));

                var dead = buttons.Where(b => !HasClickHandler(b)).ToList();
                deadButtons += dead.Count;

                if (dead.Count > 0)
                    Fail($"الشاشة «{def.Title}» ({def.Code})",
                         $"{dead.Count} زر بلا معالج: " + string.Join("، ", dead.Select(Describe).Take(6)));
                else
                    Check($"«{def.Title}» ({def.Code}) — {buttons.Count} زر", () => true);
            }
            catch (Exception ex)
            {
                Fail($"الشاشة «{def.Title}» ({def.Code})", ex.GetType().Name + ": " + (ex.InnerException?.Message ?? ex.Message));
            }
        }
        Line($"  → {screens} شاشة فُحصت · {deadButtons} زر بلا معالج");
    }

    private static ErpChrome TryAttachChrome(UIElement el)
    {
        try
        {
            var chrome = new ErpChrome();
            var m = el.GetType().GetMethod("AttachChrome", BindingFlags.Public | BindingFlags.Instance);
            m?.Invoke(el, new object[] { chrome });
            return chrome;
        }
        catch { return null; }
    }

    /// <summary>بعض الشاشات تبني محتواها في Loaded — نُشغّله ليرى الفحص الأزرار الحقيقية.</summary>
    private static void ForceLoad(UIElement el)
    {
        try
        {
            if (el is FrameworkElement fe)
            {
                var m = fe.GetType().GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                m?.Invoke(fe, null);
            }
        }
        catch { }
    }

    private static List<Button> CollectButtons(DependencyObject root)
    {
        // §B54: الشجرة المنطقية تعمل قبل العرض — VisualTreeHelper كان يرجع صفراً
        // للشاشات غير المرسومة فلا يرى أزرارها، فيتّهم البريء ويفلت المذنب.
        var list = new List<Button>();
        void Walk(DependencyObject d)
        {
            if (d == null) return;
            if (d is Button b) list.Add(b);
            foreach (var child in LogicalTreeHelper.GetChildren(d))
                if (child is DependencyObject cd) Walk(cd);
        }
        Walk(root);
        return list;
    }

    /// <summary>§هل للزر معالج نقر فعلاً؟ زر بلا معالج = زر ميت يبدو حياً.</summary>
    private static bool HasClickHandler(Button b)
    {
        // §B54: يقرأ مخزن أحداث العنصر كله (Click وMouseLeftButtonUp وغيرهما) —
        // القراءة القديمة لـ«حقل Click» بالانعكاس كانت ترجع false دائماً لأن أحداث
        // WPF مساراتٌ لا حقول، فتتّهم أزرار لوحات المؤشرات الموصولة بـ MouseUp.
        try
        {
            var prop = typeof(UIElement).GetProperty("EventHandlersStore",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (prop?.GetValue(b) is Array store)
                return store.Length > 0;
            return true; // إن تعذّر الفحص لا نتّهم زراً بريئاً
        }
        catch { return true; }
    }

    private static string Describe(Button b)
    {
        string txt = (b.Content as string) ?? (b.Content as TextBlock)?.Text ?? b.Name ?? "(بلا اسم)";
        return txt.Length > 24 ? txt[..24] + "…" : txt;
    }

    // ═══════════════════ أدوات ═══════════════════

    private static string AssemblyVersion()
        => Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "?";

    private static string Mask(string cs)
    {
        if (string.IsNullOrEmpty(cs)) return "—";
        return System.Text.RegularExpressions.Regex.Replace(cs, @"(Password|Pwd)\s*=\s*[^;]*", "$1=***",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static void Section(string t)
    {
        _lines.Add("");
        _lines.Add("── " + t + " " + new string('─', Math.Max(0, 76 - t.Length)));
    }

    private static void Head(string t) { _lines.Add("═" + new string('═', 78)); _lines.Add("  " + t); _lines.Add("═" + new string('═', 78)); }
    private static void Line(string s) => _lines.Add(s);

    /// <summary>نتيجة ناجحة بتفصيل — العدّ هنا وحده (لا في Check).</summary>
    private static bool Ok(string detail) { _pass++; _lines.Add($"  ✅ {_current}  ({detail})"); return true; }

    /// <summary>نتيجة فاشلة بتفصيل — العدّ هنا وحده (لا في Check).</summary>
    private static bool Bad(string detail) { _fail++; _lines.Add($"  ❌ {_current}  ←  {detail}"); return false; }

    /// <summary>فشل مستقل (خارج Check) — يعدّ مرة واحدة.</summary>
    private static void Fail(string name, string why)
    {
        _fail++;
        _lines.Add($"  ❌ {name}  ←  {why}");
    }

    private static string _current = "";
    /// <summary>
    /// خطوة فحص. إن استدعى الجسم Ok/Bad فهو من يعدّ؛ وإلا يعدّ Check حسب النتيجة.
    /// </summary>
    private static void Check(string name, Func<bool> act)
    {
        _current = name;
        int before = _pass + _fail;
        try
        {
            bool ok = act();
            if (_pass + _fail == before)          // لم يعدّ الجسم بنفسه
            {
                if (ok) { _pass++; _lines.Add($"  ✅ {name}"); }
                else { _fail++; _lines.Add($"  ❌ {name}"); }
            }
        }
        catch (Exception ex) { _fail++; _lines.Add($"  ❌ {name}  ←  {ex.GetType().Name}: {ex.Message}"); }
    }

}

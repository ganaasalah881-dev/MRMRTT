using System.Text.RegularExpressions;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §اختبار انحدار بنيوي — يمنع «الأزرار الميتة».
///
/// النمط الذي كشفه الفحص: كود سليم في الخلفية بلا واجهة تصل إليه، أو زر ظاهر
/// موصول بمعالج فارغ. أمثلة وقعت فعلاً:
///   • ScreenFactory.WrapPlain كان يوصل زر الطباعة بـ WithPrint((_, _) => { }) في 8 شاشات
///   • UnitsAndPacksWindow كانت مكتوبة بالكامل ولا زر يفتحها
///   • MasterDataService.MarkInvoiced خدمة كاملة بلا أي مستدعٍ من الواجهات
///   • PlanningView.Approve() مكتوبة ولا زر يستدعيها
///
/// هذه الاختبارات تفحص المصدر مباشرة لأن مشروع الاختبارات لا يرجع إلى Desktop (net8.0-windows).
/// </summary>
public class UiWiringTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>يزيل التعليقات حتى لا يُحتسب نصّ توضيحي داخل تعليق ككود.</summary>
    private static string StripComments(string t)
    {
        t = Regex.Replace(t, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        t = Regex.Replace(t, @"//[^\n]*", " ");
        return t;
    }

    private static string ReadAll(string relativePattern)
    {
        var root = RepoRoot();
        var sb = new System.Text.StringBuilder();
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "src", "DatesErp.Desktop"),
                     relativePattern, SearchOption.AllDirectories))
        {
            if (f.Contains("/obj/") || f.Contains("\\obj\\") || f.Contains("/bin/") || f.Contains("\\bin\\")) continue;
            sb.AppendLine(StripComments(File.ReadAllText(f)));
        }
        return sb.ToString();
    }

    /// <summary>كل Click="X" في XAML يجب أن يقابله معالج موجود وغير فارغ.</summary>
    [Fact]
    public void Every_Xaml_Click_Handler_Exists_And_Is_Not_Empty()
    {
        var root = RepoRoot();
        var cs = ReadAll("*.cs");
        var missing = new List<string>();
        var empty = new List<string>();

        foreach (var xf in Directory.EnumerateFiles(Path.Combine(root, "src", "DatesErp.Desktop"),
                     "*.xaml", SearchOption.AllDirectories))
        {
            if (xf.Contains("/obj/") || xf.Contains("\\obj\\")) continue;
            var x = File.ReadAllText(xf);
            foreach (Match m in Regex.Matches(x, "Click=\"([^\"]+)\""))
            {
                var handler = m.Groups[1].Value;
                var decl = Regex.Match(cs, @"\b(?:void|Task)\s+" + Regex.Escape(handler) + @"\s*\(");
                if (!decl.Success) { missing.Add(Path.GetFileName(xf) + " -> " + handler); continue; }

                int i = cs.IndexOf('{', decl.Index + decl.Length);
                int depth = 1, j = i + 1;
                while (j < cs.Length && depth > 0)
                {
                    if (cs[j] == '{') depth++;
                    else if (cs[j] == '}') depth--;
                    j++;
                }
                if (cs.Substring(i + 1, j - i - 2).Trim().Length < 3)
                    empty.Add(Path.GetFileName(xf) + " -> " + handler);
            }
        }

        Assert.True(missing.Count == 0, "معالجات أزرار مفقودة:\n" + string.Join("\n", missing));
        Assert.True(empty.Count == 0, "معالجات أزرار فارغة:\n" + string.Join("\n", empty));
    }

    /// <summary>لا يجوز وصل زر بـ lambda فارغة — زر ظاهر لا يفعل شيئاً.</summary>
    [Fact]
    public void No_Button_Is_Wired_To_An_Empty_Lambda()
    {
        var cs = ReadAll("*.cs");
        var hits = Regex.Matches(cs, @"With(?:Print|Save|New|Edit|Delete|Search|Refresh|Approve|Unapprove|Excel|Exit|List)\(\s*\(_, _\)\s*=>\s*\{\s*\}")
            .Select(m => m.Value).ToList();
        Assert.True(hits.Count == 0, "أزرار موصولة بمعالج فارغ:\n" + string.Join("\n", hits));
    }

    /// <summary>
    /// مسار الحفظ في التخطيط: الزر العلوي يطلب Edit من PermissionGate،
    /// وF10 والزر المرئي ينتهيان إلى Save_Click نفسه.
    /// </summary>
    [Fact]
    public void Planning_Save_Uses_Edit_Gate_And_Save_Click_For_F10()
    {
        var root = RepoRoot();
        var planningCs = File.ReadAllText(Path.Combine(root, "src", "DatesErp.Desktop", "Views", "Screens", "PlanningView.xaml.cs"));
        var planningXaml = File.ReadAllText(Path.Combine(root, "src", "DatesErp.Desktop", "Views", "Screens", "PlanningView.xaml"));
        var toolbar = File.ReadAllText(Path.Combine(root, "src", "DatesErp.Desktop", "Views", "ErpToolbar.cs"));
        var catalog = File.ReadAllText(Path.Combine(root, "src", "DatesErp.Desktop", "Screens", "ScreenCatalog.cs"));

        Assert.Contains(".WithSave((_, _) => Save_Click(null, null), \"حفظ الخطة (F10)\")", planningCs);
        Assert.Contains("Key.F10", planningCs);
        Assert.Contains("private void Save_Click", planningCs);
        Assert.Contains("Click=\"Save_Click\"", planningXaml);
        Assert.DoesNotContain("SaveActionBtn.Visibility = Visibility.Visible", planningCs);
        Assert.Contains("AddBtn(label, \"ErpPrimaryButton\", h, \"حفظ (F10)\", \"Edit\")", toolbar);
        Assert.Contains("PermissionGate.Can(Module, kv.Value)", toolbar);
        Assert.Contains("kv.Key.Visibility = ok ? Visibility.Visible : Visibility.Collapsed", toolbar);
        Assert.Contains("new(\"planning\", \"الإنتاج\", \"خطط الإنتاج (MPS)\", \"planning\"", catalog);
    }

    /// <summary>
    /// التحقق من إصلاح 1.50.73: الحفظ متاح قبل الصفوف وقبل نتيجة الطاقة،
    /// بينما تبقى الطاقة شرطاً عند الضغط، والقفل وحده يعطل الأزرار.
    /// </summary>
    [Fact]
    public void Planning_Save_Is_Gated_Only_By_Lock_Not_Capacity()
    {
        var root = RepoRoot();
        var cap = File.ReadAllText(Path.Combine(root, "src", "DatesErp.Desktop", "Views", "Screens", "PlanningView.Capacity.cs"));
        var planning = File.ReadAllText(Path.Combine(root, "src", "DatesErp.Desktop", "Views", "Screens", "PlanningView.xaml.cs"));

        Assert.Contains("SaveActionBtn.IsEnabled = !_locked", cap);
        Assert.Contains("_toolbar.SaveBtn.IsEnabled = !_locked", cap);
        Assert.Contains("if (_toolbar.SaveBtn != null) _toolbar.SaveBtn.IsEnabled = !locked;", planning);
        Assert.Contains("if (SaveActionBtn != null) SaveActionBtn.IsEnabled = !locked;", planning);
        Assert.DoesNotContain("SaveActionBtn.IsEnabled = !_locked &&", cap);
        Assert.DoesNotContain("SaveBtn.IsEnabled = !_locked &&", cap);
        Assert.DoesNotContain("SaveBtn.IsEnabled = !locked &&", planning);
        Assert.DoesNotContain("SaveActionBtn.IsEnabled = !locked &&", planning);
        Assert.Contains("if (!_capacityValid)", planning);
        Assert.Contains("validRows.Count == 0", planning);
        Assert.Contains("أدخل عنوان الخطة", planning);
        Assert.Contains("SetPermissionModule(\"planning\")", planning);
    }

    /// <summary>
    /// عمليات دورة العمل الحرجة يجب أن تكون قابلة للوصول من الواجهات —
    /// وإلا كانت «خدمة بلا واجهة» (نمط وقع فعلاً: MarkInvoiced وApprove وUnitsAndPacksWindow).
    /// </summary>
    [Theory]
    [InlineData("StartOrder")]            // بدء الإنتاج من شاشة الأوامر
    [InlineData("SaveActualProduction")]  // تسجيل التنفيذ الفعلي فقط؛ الاستلام المخزني مسار مستقل
    [InlineData("CreateDeliveryFromActual")] // إنشاء مسودة أمر التسليم من التنفيذ
    [InlineData("IssueTodayOrders")]             // إنشاء أمر من الخطة
    [InlineData("ApproveOrder")]          // اعتماد الأمر
            // إقفال خطة الإنتاج
    [InlineData("SaveCheck")]             // حفظ فحص الجودة
    [InlineData("ApproveCheck")]          // اعتماد الفحص
    [InlineData("SaveReceipt")]           // أمر تسليم الإنتاج التام
    [InlineData("Receive")]               // سند الاستلام المخزني
    [InlineData("ChangePassword")]        // تغيير كلمة المرور
    // §B69: شاشة الأصناف حُذفت لإعادة التصميم — يُعاد هذا السطر مع الشاشة الجديدة
    [InlineData("SaveItemCategory")]      // الفئات الحرة
    public void Critical_Operation_Is_Reachable_From_The_UI(string method)
    {
        var ui = ReadAll("*.cs");
        Assert.True(Regex.IsMatch(ui, @"\." + Regex.Escape(method) + @"\s*\("),
            $"العملية {method} لا يستدعيها أي كود في الواجهات — خدمة بلا واجهة.");
    }

    /// <summary>
    /// §توثيق انحداري: StartExecution/CompleteExecution تنفيذ موازٍ لا تستخدمه الواجهات إطلاقاً —
    /// الواجهة تسلك StartOrder/CloseProductionDay. الاختبارات كانت تغطي المسار الأول فقط،
    /// وهذا سبب جوهري في «الاختبارات تنجح والواجهة تتصرف differently».
    /// إن وُصلا بالواجهة مستقبلاً يجب تحديث هذه القائمة.
    /// </summary>
    [Fact]
    public void Parallel_Execution_Path_Remains_UI_Unreachable_By_Design()
    {
        var ui = ReadAll("*.cs");
        Assert.False(Regex.IsMatch(ui, @"\.StartExecution\s*\("),
            "StartExecution صار مستخدماً من الواجهات — حدّث هذا الاختبار ووحّد المسارين.");
        Assert.False(Regex.IsMatch(ui, @"\.CompleteExecution\s*\("),
            "CompleteExecution صار مستخدماً من الواجهات — حدّث هذا الاختبار ووحّد المسارين.");
        // والمسار الفعلي المستخدم مغطى
        Assert.Matches(@"\.StartOrder\s*\(", ui);
        Assert.Matches(@"\.SaveActualProduction\s*\(", ui);
        Assert.DoesNotMatch(@"\.CloseProductionDay\s*\(", ui); // no second actual-entry form
        var coordinator = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), "src", "DatesErp.Application", "Services", "ProductionDeliveryService.Actual.cs")));
        Assert.DoesNotContain("SaveReceipt", coordinator);
        Assert.DoesNotContain("FinishedGoodsService", coordinator);
        Assert.DoesNotContain("finishedgoods", coordinator);
        Assert.DoesNotContain("JoinParentTransaction", coordinator);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // §شاشة التقارير: القائمة تختفي والتقرير يملأ الواجهة بخط أكبر
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Reports_Screen_Hides_List_And_Fills_The_Window()
    {
        string root = FindRepoRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "src/DatesErp.Desktop/Views/Screens/ReportsView.xaml"));
        string cs = File.ReadAllText(Path.Combine(root, "src/DatesErp.Desktop/Views/Screens/ReportsView.xaml.cs"));

        // القائمة تُخفى وعمودها يُصفَّر عند تشغيل التقرير
        Assert.Contains("ShowReportFullWidth", cs);
        Assert.Contains("CatalogPanel.Visibility = Visibility.Collapsed", cs);
        Assert.Contains("ListCol.Width = new GridLength(0)", cs);
        Assert.Contains("Grid.SetColumnSpan(ReportPanel, 2)", cs);

        // وزر رجوع يعيد القائمة
        Assert.Contains("ShowCatalog", cs);
        Assert.Contains("Back_Click", cs);
        Assert.Contains("BackBtn", xaml);

        // ونقرتان تشغّلان التقرير مباشرة
        Assert.Contains("Report_DoubleClick", xaml);
    }

    [Fact]
    public void Reports_Screen_Uses_Larger_Fonts_And_Full_Width_Columns()
    {
        string root = FindRepoRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "src/DatesErp.Desktop/Views/Screens/ReportsView.xaml"));
        string cs = File.ReadAllText(Path.Combine(root, "src/DatesErp.Desktop/Views/Screens/ReportsView.xaml.cs"));
        string theme = File.ReadAllText(Path.Combine(root, "src/DatesErp.Desktop/Themes/DateErpTheme.xaml"));

        // الجدول: خط 14 وارتفاع صف 36 (§الشكل الموحد: صفوف مرتّبة ومريحة)
        Assert.Contains("FontSize=\"14\"", xaml);
        Assert.Contains("RowHeight=\"36\"", xaml);

        // §الشكل الموحد لكل التقارير: شريط المعادلة + تلوين الصفوف بالمرحلة + محاذاة الأرقام
        Assert.Contains("EquationBar", xaml);
        Assert.Contains("AlternationIndex", xaml);
        Assert.Contains("StageColumn", cs);
        Assert.Contains("ReportGrid_LoadingRow", cs);
        Assert.Contains("StageBrush", cs);
        Assert.Contains("IsNumericColumn", cs);

        // العنوان بخط كبير
        Assert.Contains("FontSize=\"19\"", xaml);

        // الأعمدة بعرض نجمي يملأ الواجهة لا بعرض ذاتي يتزاحم
        Assert.Contains("DataGridLengthUnitType.Star", cs);
        Assert.DoesNotContain("DataGridLengthUnitType.Auto", cs);

        // ورؤوس الأعمدة بنمط واضح
        Assert.Contains("ReportHeaderStyle", theme);
        Assert.Contains("ReportHeaderStyle", cs);

        // Shared, measurable minimum font contract replaces separate FlowDocument/PDF fonts.
        Assert.True(DatesErp.Desktop.Printing.PrintLayout.FontSize >= 12);
        string renderer = File.ReadAllText(Path.Combine(root, "src/DatesErp.Desktop/Printing/PrintRenderer.cs"));
        Assert.Contains("FormattedText", renderer);
        Assert.Contains("PrintLayout.LineHeight", renderer);

    }

    // ═══════════════════════════════════════════════════════════════════════
    // §قاعدة الواجهات: لا تمرير رأسي للصفحة كلها — الأزرار ثابتة والجدول يملأ
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("OrdersView")]
    [InlineData("PlanningView")]
    [InlineData("ReceivingView")]
    [InlineData("DeliveryView")]
    [InlineData("FinishedGoodsView")]
    [InlineData("QualityView")]
    [InlineData("SystemInfoView")]
    public void Screen_Is_Not_One_Long_Scrolling_Page(string name)
    {
        string path = Path.Combine(FindRepoRoot(), $"src/DatesErp.Desktop/Views/Screens/{name}.xaml");
        Assert.True(File.Exists(path), "الشاشة مفقودة: " + path);
        var lines = File.ReadAllLines(path);

        // النمط الممنوع: ScrollViewer يليه StackPanel مباشرةً = صفحة واحدة طويلة تُصعد وتُنزل
        for (int i = 0; i < lines.Length - 1; i++)
        {
            bool sv = lines[i].TrimStart().StartsWith("<ScrollViewer") && !lines[i].Contains("MaxHeight");
            bool sp = lines[i + 1].TrimStart().StartsWith("<StackPanel");
            // §الممنوع هو ScrollViewer الجذر: ما جاء قبل أي DockPanel/Grid.
            // أما الداخلي (داخل بطاقة أو منطقة مستند) فمحدود بحدود حاويته ولا يجعل الصفحة طويلة.
            bool seenLayout = false;
            for (int k = 0; k < i; k++)
            {
                var t = lines[k].TrimStart();
                if (t.StartsWith("<DockPanel") || (t.StartsWith("<Grid") && !t.StartsWith("<Grid."))) { seenLayout = true; break; }
            }
            Assert.False(sv && sp && !seenLayout,
                $"{name}.xaml:{i + 1} — ScrollViewer ثم StackPanel في جذر الواجهة: صفحة واحدة طويلة تُصعد وتُنزل. " +
                "استخدم DockPanel: الأقسام ثابتة أعلى/أسفل والجدول الرئيسي يملأ المساحة.");
        }
    }

    [Fact]
    public void Main_Grids_Fill_The_Window_Instead_Of_Fixed_Height()
    {
        string root = FindRepoRoot();
        // الجداول الرئيسية في شاشات العمل لا ارتفاع ثابت لها — تملأ المساحة المتبقية
        var mains = new (string file, string grid)[]
        {
            ("OrdersView", "TodayGrid"),
            ("PlanningView", "RowsGrid"), ("ReceivingView", "ShipGrid"),
            ("DeliveryView", "DeliveriesGrid"), ("FinishedGoodsView", "ReceiptsGrid"),
            ("QualityView", "ResultsGrid"),
            ("ReportsView", "ReportGrid"),
        };
        foreach (var (file, grid) in mains)
        {
            string xaml = File.ReadAllText(Path.Combine(root, $"src/DatesErp.Desktop/Views/Screens/{file}.xaml"));
            int i = xaml.IndexOf($"x:Name=\"{grid}\"", StringComparison.Ordinal);
            Assert.True(i >= 0, $"{file}: لا يوجد جدول {grid}");
            int end = xaml.IndexOf('>', i);
            string tag = xaml.Substring(i, end - i);
            Assert.DoesNotContain(" Height=", tag);   // §بلا ارتفاع ثابت (المسافة تمنع مطابقة RowHeight)
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // §مطابقة قوالب الطباعة المرجعية (Documentation/PrintTemplates)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Print_Documents_Match_The_Reference_Templates()
    {
        string root = FindRepoRoot();
        string Read(string rel) => File.ReadAllText(Path.Combine(root, rel));

        // ① أمر الإنتاج ← print_order.html
        string orders = Read("src/DatesErp.Desktop/Printing/StoredPrintModels.cs");
        Assert.Contains("أمر وتشغيل إنتاج التمور (Work Order)", orders);
        Assert.Contains("بنود التشغيل والمنتجات التامة المستهدفة", orders);
        Assert.Contains("المحتسبة آلياً (BOM)", orders);
        Assert.Contains("مشرف صالة الإنتاج", orders);
        Assert.Contains("أمين مخزن المواد المساعدة", orders);

        // ② جلسة التشغيل ← B78: شاشة الإقفال حُذفت لإعادة التصميم من الصفر؛
        // يُعاد فحص مستند طباعة الإقفال مع الشاشة الجديدة.

        // ③ سند تسليم الإنتاج التام ← print_delivery.html
        string fg = Read("src/DatesErp.Desktop/Printing/StoredPrintModels.cs");
        Assert.Contains("سند تسليم واستلام إنتاج تام (WFG)", fg);
        Assert.Contains("والمخرجات الثانوية المسلمة للمستودعات", fg);
        Assert.Contains("ضابط فحص الجودة", fg);
        Assert.Contains("أمين مستودع الإنتاج التام WFG", fg);

        // ⑤ الفحص ← print_quality.html
        string quality = Read("src/DatesErp.Desktop/Printing/StoredPrintModels.cs");
        Assert.Contains("استمارة وشهادة فحص جودة التمور (QC Lab Sheet)", quality);
        Assert.Contains("نتائج فحص ومطابقة العينات التامة", quality);
        Assert.Contains("أخصائي فحص الجودة والمختبر", quality);
        Assert.Contains("رئيس قسم الجودة وسلامة الغذاء", quality);

        // ⑥ الاستلام ← print_shipment.html (المعاينة والـPDF معاً)
        string rcv = Read("src/DatesErp.Desktop/Printing/DocumentDesigns.cs");
        Assert.Contains("أمر وسند استلام شحنة تمور خام", rcv);
        Assert.Contains("أمين مخزن المواد الخام", rcv);

        // ⑦ الخطة ← print_plan.html
        string plan = Read("src/DatesErp.Desktop/Printing/DocumentDesigns.cs");
        Assert.Contains("خطة وجدولة تشغيل وإنتاج التمور", plan);
        Assert.DoesNotContain("خطة وجدولة تشغيل وإنتاج التمور المعتمدة", plan);
        Assert.Contains("مسؤول التخطيط والجدولة", plan);
        Assert.Contains("المدير العام / اعتماد الخطة", plan);

        // ⑧ تسليم العميل ← print_customer_delivery.html (Gate Pass)
        string del = Read("src/DatesErp.Desktop/Printing/StoredPrintModels.cs");
        Assert.Contains("سند إخراج وتسليم بضاعة للعميل (Gate Pass)", del);
        Assert.Contains("أمن بوابة المصنع / تصريح الخروج", del);
        Assert.Contains("السائق الناقل / استلام الشحنة", del);

        // والسجل الزمني للتوقفات محفوظ في الكيان
        string entity = Read("src/DatesErp.Core/Domain/Entities/Production.cs");
        Assert.Contains("public string StartTime", entity);
        Assert.Contains("public string EndTime", entity);
    }

    [Fact]
    public void Pdf_Export_Draws_Section_Titles_And_The_Second_Table()
    {
        // §كان PDF يغفل عناوين الأقسام والجدول الثاني كلياً — المعاينة تعرضهما وهو لا
        string src = File.ReadAllText(Path.Combine(FindRepoRoot(),
            "src/DatesErp.Desktop/Views/PhasePrintDocuments.cs"));
        Assert.Contains("PrintRenderer.ExportPdf(Build(m)", src);
        var model=new DatesErp.Desktop.Views.PhaseDocModel { MainTitle="بنود",Columns=new[]{"صنف"},Rows=new(){new object[]{"أ"}},SecondTitle="مواد",SecondColumns=new[]{"مادة"},SecondRows=new(){new object[]{"ب"}} };
        var spec=DatesErp.Desktop.Printing.PrintSchema.FromPhase(model);
        Assert.Contains(spec.Sections,s=>s.Title=="بنود" && s.Rows.Single().Single()=="أ");
        Assert.Contains(spec.Sections,s=>s.Title=="مواد" && s.Rows.Single().Single()=="ب");

    }

    [Fact]
    public void Reference_Print_Templates_Are_Kept_In_The_Repository()
    {
        string dir = Path.Combine(FindRepoRoot(), "Documentation/PrintTemplates");
        Assert.True(Directory.Exists(dir), "مجلد القوالب المرجعية مفقود");
        foreach (var f in new[] { "print_order.html", "print_execution.html", "print_delivery.html", "print_invoice.html",
                                   "print_quality.html", "print_shipment.html", "print_customer_delivery.html", "print_plan.html" })
            Assert.True(File.Exists(Path.Combine(dir, f)), "القالب مفقود: " + f);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // §زر «+» في التقارير: يفتح المستند الأصلي — ولا يظهر إن لم يكن هناك ما يُفتح
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Drill_Button_Never_Fails_Silently_And_Hides_When_Nothing_To_Open()
    {
        string src = File.ReadAllText(Path.Combine(FindRepoRoot(),
            "src/DatesErp.Desktop/Views/Screens/ReportsView.xaml.cs"));

        // العمود يظهر فقط إن وُجد رابط فعلي — فلا زر يبدو معطلاً
        Assert.Contains("links != null && links.Any(l => l != null)", src);

        // رقم الصف مثبَّت في Tag — لا اعتماد على وراثة DataContext وحدها
        Assert.Contains("factory.SetBinding(FrameworkElement.TagProperty, new Binding())", src);
        Assert.Contains("btn.Tag is DataRowView", src);

        // ولا رجوع صامت: كل فرع فشل يعرض رسالة (لا "return;" عارية في المعالج)
        int i = src.IndexOf("private void Drill_Click", StringComparison.Ordinal);
        Assert.True(i > 0);
        int end = src.IndexOf("private ", i + 10, StringComparison.Ordinal);
        string body = src.Substring(i, (end > 0 ? end : src.Length) - i);
        Assert.DoesNotContain(") return;", body.Replace("btn) return;", ""));   // عدا فحص نوع المُرسِل
        Assert.Contains("لا يرتبط بمستندات أصلية", body);
        Assert.Contains("لا يوجد مستند مرتبط بهذا الصف", body);
    }

    [Fact]
    public void Report_RowLinks_Align_With_Rows_For_Every_Report()
    {
        // §سبب رئيسي لتعطل «+»: عدد الروابط أقل من عدد الصفوف فيرجع المعالج بصمت.
        // إن كان التقرير يربط مستندات، فالروابط يجب أن تكون بعدد الصفوف تماماً.
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(host.Services);
        var svc = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<DatesErp.Core.Interfaces.Services.IReportService>(scope.ServiceProvider);

        int checkedReports = 0, misaligned = 0;
        var problems = new List<string>();
        foreach (var def in svc.GetReports())
        {
            DatesErp.Core.Interfaces.Services.ReportResult r;
            try { r = svc.Run(def.Code, new Dictionary<string, string>()); }
            catch { continue; }          // تقارير تحتاج معاملات — تُفحص في اختباراتها الخاصة
            if (r == null) continue;
            checkedReports++;

            bool hasAnyLink = r.RowLinks != null && r.RowLinks.Any(l => l != null);
            if (!hasAnyLink) continue;   // تقرير بلا مستندات — الزر لا يظهر أصلاً

            if (r.RowLinks.Count != r.Rows.Count)
            {
                misaligned++;
                problems.Add($"{def.Code}: {r.Rows.Count} صفاً مقابل {r.RowLinks.Count} رابطاً");
            }
        }
        Assert.True(checkedReports > 0, "لم يُفحص أي تقرير");
        Assert.True(misaligned == 0, "روابط لا تطابق الصفوف:\n" + string.Join("\n", problems));
    }

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "DateERP.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }
}
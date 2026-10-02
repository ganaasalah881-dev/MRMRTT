#nullable enable
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using DatesErp.Application.Services;
using DatesErp.Core.Domain.Entities;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// §v1.50.33 — مركز الصلاحيات بالنمط الكلاسيكي مع تصويبات المستخدم:
/// رأس الصفحة قائمة منسدلة للهدف (دور/مستخدم) كالصورة المرجعية، والمصفوفة معطّلة
/// حتى يُختار هدف صريح (لا منح صامت)، وشجرة الوحدات أوسع، والتحميل دفاعي:
/// أي عطل يظهر بتفاصيله داخل الشاشة نفسها بدل رسالة غامضة.
/// النموذج الهرمي كما هو: منح الدور مباشرة، واستثناء المستخدم الثلاثي
/// (منح/منع/وراثة)، تأكيد الحساسة، الحفظ بملخص، النسخ والمقارنة والسجل والتعطيل الآمن.
/// </summary>
public partial class PermissionsView : UserControl
{
    /// <summary>خانة واحدة في المصفوفة — التغيير يمر عبر الصف ليُحدَّث النموذج الداخلي.</summary>
    public sealed class PermCell : INotifyPropertyChanged
    {
        private bool _on;
        public bool On
        {
            get => _on;
            set
            {
                var v = value;
                if (!Row.OnCellChanging(Op, ref v)) { return; } // مرفوض (حساسة لم تُؤكَّد) — لا تغيير
                if (_on == v) { Raise(); return; }
                _on = v; Raise();
            }
        }
        public string Op { get; init; } = "";
        public MatrixRow Row { get; init; } = null!;
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        /// <summary>تهيئة بلا مرور على تحقق الحساسة (قيمة النموذج لا تدخل المستخدم).</summary>
        public void Init(bool on) { _on = on; Raise(nameof(On)); }
        public void Refresh() { _on = Row.ComputeOn(Op); Raise(nameof(On)); }
    }

    /// <summary>صف الشاشة في المصفوفة — خانته مبثوقة في Cells لكل عملية.</summary>
    public sealed class MatrixRow
    {
        public int SortNo { get; set; }
        public string ResCode { get; set; } = "";
        public string NameAr { get; set; } = "";
        public string GroupAr { get; set; } = "";
        public bool RoleMode { get; set; }
        public HashSet<string> Grants = new();          // وضع الدور: المنح الحي
        public HashSet<string> Inherited = new();       // وضع المستخدم: ما يرثه من أدواره
        public Dictionary<string, bool> Exc = new();    // وضع المستخدم: الاستثناءات
        public HashSet<string> Sensitive = new();
        public Dictionary<string, PermCell> Cells { get; } = new();
        public bool ComputeOn(string op) => RoleMode ? Grants.Contains(op)
            : Exc.TryGetValue(op, out var e) ? e : Inherited.Contains(op);
        /// <summary>تُستدعى قبل أي تغيير — تؤكد الحساسة وتحدّث النموذج، وتُرجع القيمة النهائية.</summary>
        public bool OnCellChanging(string op, ref bool val)
        {
            if (val && Sensitive.Contains(op) && !AppContainer.Get<DialogService>()
                    .Confirm($"⚠ العملية «{op}» حساسة على الشاشة «{NameAr}» — منحها يمكّن تجاوز حالات الاعتماد. تأكيد المنح؟"))
                return false;
            if (RoleMode) { if (val) Grants.Add(op); else Grants.Remove(op); }
            else
            {
                if (val == Inherited.Contains(op)) Exc.Remove(op);
                else Exc[op] = val;
            }
            return true;
        }
    }

    private sealed class ModeOpt { public string Code { get; set; } = ""; public string Name { get; set; } = ""; }
    private sealed class TargetOpt { public int? Id { get; set; } public string Label { get; set; } = ""; }

    private List<PermissionResource> _res = new();
    private List<PermissionOperation> _ops = new();
    private readonly HashSet<(string res, string op)> _pending = new();
    private HashSet<(string res, string op)> _baseline = new();
    private readonly Dictionary<(string res, string op), bool> _exceptions = new();
    private Dictionary<(string res, string op), bool> _excBaseline = new();
    private string _mode = "role";
    private int _targetId;
    private string _targetName = "";
    private string _groupFilter = "";    // فارغ = كل الوحدات
    private bool _loading;
    private bool _syncing;
    private readonly List<MatrixRow> _rows = new();

    public PermissionsView()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadAll();
    }

    private PermissionService Svc(DatesErpDbContext db)
        => new(db, AppContainer.Get<DatesErp.Core.Interfaces.Services.ICurrentSession>());

    private void ShowError(string where, Exception ex)
    {
        // §v1.50.33: التفاصيل داخل الشاشة — المستخدم يقرأها/ينسخها بدل رسالة غامضة، ويُسجَّل العطل كالمعتاد.
        ErrorText.Text = $"⚠ تعذّر تنفيذ العملية ({where}): {ex.Message}"
            + (ex.InnerException != null ? "\nالتفاصيل: " + ex.InnerException.Message : "");
        ErrorBanner.Visibility = Visibility.Visible;
        AppContainer.Get<DialogService>().HandleException(ex, "Perm." + where);
    }

    private void LoadAll()
    {
        try
        {
            ErrorBanner.Visibility = Visibility.Collapsed;
            _loading = true;
            ModeBox.ItemsSource = new List<ModeOpt>
            {
                new() { Code = "role", Name = "🎭 دور مجموعة" },
                new() { Code = "user", Name = "👤 مستخدم" }
            };
            ModeBox.SelectedValue = "role";
            using (var scope = AppContainer.NewScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                Svc(db).EnsureCatalog();
                _res = db.PermissionResources.AsNoTracking().Where(r => r.IsActive).OrderBy(r => r.SortNo).ToList();
                _ops = db.PermissionOperations.AsNoTracking().OrderBy(o => o.SortNo).ToList();
            }
            FillTargets();
            FillUsers("");
            FillUsersTab();
            BuildTree(TreeSearchBox.Text);
            BuildMatrixColumns();
            SetNoTarget("اختر الهدف من القائمتين أعلاه — لا يمكن التأشير في المصفوفة قبل اختيار موظف أو دور.");
        }
        catch (Exception ex) { ShowError("تحميل الشاشة", ex); }
        finally { _loading = false; }
    }

    // ═══ قائمتا رأس الصفحة: النوع ثم الهدف ═══
    private void FillTargets()
    {
        _syncing = true;
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            if (_mode == "role")
            {
                var roles = db.Roles.AsNoTracking().OrderBy(r => r.Id)
                    .Select(r => new { r.Id, r.RoleNameAr, r.IsActive }).ToList();
                TargetBox.ItemsSource = roles.Select(r => new TargetOpt
                { Id = r.Id, Label = $"🎭 رقم {r.Id} — {r.RoleNameAr}{(r.IsActive ? "" : " (معطل)")}" }).ToList();
            }
            else
            {
                var users = db.Users.AsNoTracking().OrderBy(u => u.Id)
                    .Select(u => new { u.Id, u.FullName, u.UserName, u.IsActive }).ToList();
                TargetBox.ItemsSource = users.Select(u => new TargetOpt
                { Id = u.Id, Label = $"👤 رقم {u.Id} — {u.FullName} ({u.UserName}){(u.IsActive ? "" : " (معطل)")}" }).ToList();
            }
            TargetBox.SelectedItem = null;
        }
        finally { _syncing = false; }
    }

    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _loading || ModeBox == null || ModeBox.SelectedValue is not string m) return;
        _mode = m;
        FillTargets();
        SetNoTarget("غيّر الهدف من القائمة الثانية أعلاه — المصفوفة معطّلة حتى الاختيار.");
    }

    private void Target_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _loading) return;
        if (TargetBox.SelectedValue is not int id)
        {
            if (!_syncing) SetNoTarget("اختر الهدف من القائمة أعلاه — المصفوفة معطّلة حتى الاختيار.");
            return;
        }
        LoadTargetMatrix(id);
    }

    private void SetNoTarget(string hint)
    {
        _targetId = 0; _targetName = "";
        _pending.Clear(); _exceptions.Clear(); _baseline = new HashSet<(string, string)>(); _excBaseline = new Dictionary<(string, string), bool>();
        _rows.Clear(); MatrixGrid.ItemsSource = new List<MatrixRow>();
        MatrixGrid.IsEnabled = false;
        TargetLabel.Text = hint;
        MatrixTitle.Text = "مصفوفة الصلاحيات — معطّلة حتى اختيار الهدف من القائمة أعلاه";
        RefreshAuditStrip();
    }

    /// <summary>تحميل مصفوفة الهدف المختار — دفاعية: أي عطل يظهر بالتفاصيل ولا يترك الشاشة مكسورة.</summary>
    private void LoadTargetMatrix(int id)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var svc = Svc(db);
            if (_mode == "role")
            {
                var role = db.Roles.AsNoTracking().FirstOrDefault(r => r.Id == id);
                if (role == null) { SetNoTarget("هذا الدور غير موجود — اختر هدفاً آخر."); return; }
                _targetName = role.RoleNameAr;
                _baseline = svc.GetRoleSet(id);
                _pending.Clear(); foreach (var k in _baseline) _pending.Add(k);
                _exceptions.Clear(); _excBaseline = new Dictionary<(string, string), bool>();
                TargetLabel.Text = $"✏️ تحرير صلاحيات المجموعة: رقم {id} — {_targetName} — حرّر المربعات ثم اضغط «حفظ التغييرات».";
            }
            else
            {
                var u = db.Users.AsNoTracking().FirstOrDefault(x => x.Id == id);
                if (u == null) { SetNoTarget("هذا المستخدم غير موجود — اختر هدفاً آخر."); return; }
                _targetName = u.FullName;
                var roleIds = db.UserRoles.AsNoTracking().Where(ur => ur.UserId == id && ur.IsActive).Select(ur => ur.RoleId).ToList();
                _baseline = svc.GetInheritedSet(roleIds);
                _pending.Clear(); foreach (var k in _baseline) _pending.Add(k);
                _excBaseline = svc.GetUserExceptions(id);
                _exceptions.Clear(); foreach (var kv in _excBaseline) _exceptions[kv.Key] = kv.Value;
                var roleNames = db.Roles.AsNoTracking().Where(r => roleIds.Contains(r.Id)).Select(r => r.RoleNameAr).ToList();
                TargetLabel.Text = $"✏️ استثناءات المستخدم: رقم {id} — {u.FullName} — الأدوار: {(roleNames.Count > 0 ? string.Join(" + ", roleNames) : "بلا دور")}.";
            }
            _targetId = id;
            MatrixGrid.IsEnabled = true;
            SyncMatrix();
        }
        catch (Exception ex) { ShowError("تحميل المصفوفة", ex); SetNoTarget("تعذّر تحميل صلاحيات الهدف — التفاصيل أعلاه."); }
    }

    // ═══ تبويب مستخدمو القاعدة ═══
    private void FillUsersTab()
    {
        using var scope = AppContainer.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var roleNames = db.Roles.AsNoTracking().ToDictionary(r => r.Id, r => r.RoleNameAr);
        // §v1.50.33: أدوار المستخدم تُجلب داخل الاستعلام الواحد (لا خصائص تنقل بعد إغلاق النطاق)
        var users = db.Users.AsNoTracking().OrderBy(u => u.Id)
            .Select(u => new { u.Id, u.FullName, u.UserName, u.IsActive,
                RoleIds = u.UserRoles.Where(ur => ur.IsActive).Select(ur => ur.RoleId).ToList() }).ToList();
        UsersAllGrid.ItemsSource = users.Select(u => new { u.Id, u.FullName, u.UserName,
            RolesText = string.Join(" + ", u.RoleIds.Select(rid => roleNames.TryGetValue(rid, out var rn) ? rn : "?")),
            u.IsActive }).ToList();
    }

    private void FillUsers(string term)
    {
        using var scope = AppContainer.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var roleNames = db.Roles.AsNoTracking().ToDictionary(r => r.Id, r => r.RoleNameAr);
        var users = db.Users.AsNoTracking().OrderBy(u => u.Id)
            .Select(u => new { u.Id, u.FullName, u.UserName, u.IsActive,
                RoleIds = u.UserRoles.Where(ur => ur.IsActive).Select(ur => ur.RoleId).ToList() }).ToList();
        var list = users.Select(u => new { u.Id,
            Label = $"👤 رقم {u.Id} — {u.FullName} ({u.UserName}) — {string.Join("+", u.RoleIds.Select(rid => roleNames.TryGetValue(rid, out var rn) ? rn : "?"))}{(u.IsActive ? "" : " (معطل)")}" })
            .ToList();
        if (!string.IsNullOrWhiteSpace(term))
            list = list.Where(u => u.Label.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        UsersList.ItemsSource = list;
    }

    private void UserSearch_Changed(object sender, TextChangedEventArgs e) => FillUsers(UserSearchBox.Text?.Trim() ?? "");

    // ═══ شجرة الوحدات — تصفية المصفوفة ═══
    private void BuildTree(string filter)
    {
        ResTree.Items.Clear();
        var term = filter?.Trim().ToLowerInvariant() ?? "";
        var all = new TreeViewItem { Header = "📁 كل الوحدات", FontWeight = FontWeights.Bold, FontSize = 13 };
        all.Selected += (_, _) => { _groupFilter = ""; SyncMatrix(); };
        ResTree.Items.Add(all);
        foreach (var group in _res.Select(r => r.GroupAr).Distinct())
        {
            var nodes = _res.Where(r => r.GroupAr == group)
                .Where(r => term == "" || r.NameAr.ToLower().Contains(term) || r.Code.Contains(term)).ToList();
            if (term == "" && nodes.Count == 0) continue;
            var gItem = new TreeViewItem { Header = $"📁 {group} ({nodes.Count})", FontWeight = FontWeights.Bold, FontSize = 13 };
            gItem.Selected += (_, _) => { _groupFilter = group; SyncMatrix(); };
            foreach (var r in nodes)
            {
                var item = new TreeViewItem { Header = $"   {r.NameAr} ({r.Code})", FontSize = 12.5, Margin = new Thickness(12, 0, 0, 0) };
                var code = r.Code;
                item.Selected += (_, _) => { _groupFilter = group; SyncMatrix(); SelectRow(code); };
                gItem.Items.Add(item);
            }
            ResTree.Items.Add(gItem);
        }
    }

    private void SelectRow(string code)
    {
        var row = MatrixGrid.Items.OfType<MatrixRow>().FirstOrDefault(x => x.ResCode == code);
        if (row != null) { MatrixGrid.SelectedItem = row; MatrixGrid.ScrollIntoView(row); }
    }

    private void TreeSearch_Changed(object sender, TextChangedEventArgs e) => BuildTree(TreeSearchBox.Text);

    // ═══ بناء أعمدة المصفوفة ═══
    private void BuildMatrixColumns()
    {
        MatrixGrid.Columns.Clear();
        MatrixGrid.Columns.Add(new DataGridTextColumn { Header = "رقم", Binding = new System.Windows.Data.Binding("SortNo"), Width = 50, IsReadOnly = true });
        MatrixGrid.Columns.Add(new DataGridTextColumn { Header = "اسم الشاشة", Binding = new System.Windows.Data.Binding("NameAr"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true });
        foreach (var o in _ops)
        {
            var col = new DataGridTemplateColumn { Header = ShortOp(o.NameAr), Width = 78, CanUserReorder = false };
            var factory = new FrameworkElementFactory(typeof(CheckBox));
            factory.SetValue(CheckBox.HorizontalContentAlignmentProperty, HorizontalAlignment.Center);
            factory.SetBinding(CheckBox.IsCheckedProperty, new System.Windows.Data.Binding($"Cells[{o.Code}].On") { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged, Mode = System.Windows.Data.BindingMode.TwoWay });
            factory.SetValue(ToolTipProperty, o.NameAr);
            col.CellTemplate = new DataTemplate { VisualTree = factory };
            MatrixGrid.Columns.Add(col);
        }
    }

    private static string ShortOp(string name)
        => name.Length <= 10 ? name : name.Replace(" (إشراف)", "§").Replace("إلغاء/عكس", "إلغاء").Replace("إعادة فتح مستند معتمد", "إعادة فتح")
            .Replace("تعديل بعد الاعتماد", "تعديل معتمد").Replace("تجاوز الفحص", "تجاوز فحص").Replace("إدارة الصلاحيات", "إدارة صلاحيات");

    // ═══ مزامنة المصفوفة مع الهدف والتصفية ═══
    private void SyncMatrix()
    {
        if (_targetId == 0) { return; }
        _loading = true;
        try
        {
            _rows.Clear();
            bool roleMode = _mode == "role";
            var sensitive = _ops.Where(o => o.IsSensitive).Select(o => o.Code).ToHashSet();
            foreach (var r in _res.Where(r => _groupFilter == "" || r.GroupAr == _groupFilter))
            {
                var row = new MatrixRow { SortNo = r.SortNo, ResCode = r.Code, NameAr = r.NameAr, GroupAr = r.GroupAr, RoleMode = roleMode, Sensitive = sensitive };
                foreach (var o in _ops)
                {
                    var key = (r.Code, o.Code);
                    if (roleMode) { if (_pending.Contains(key)) row.Grants.Add(o.Code); }
                    else if (_exceptions.TryGetValue(key, out var ex)) row.Exc[o.Code] = ex;
                    var cell = new PermCell { Op = o.Code, Row = row };
                    cell.Init(row.ComputeOn(o.Code));
                    row.Cells[o.Code] = cell;
                }
                _rows.Add(row);
            }
            MatrixGrid.ItemsSource = _rows.ToList();
            MatrixTitle.Text = roleMode
                ? $"مصفوفة صلاحيات المجموعة: رقم {_targetId} — {_targetName} — مربع ✔ = منح الدور مباشرة، ⬜ = سحب."
                : $"مصفوفة صلاحيات المستخدم: رقم {_targetId} — {_targetName} — ⬜ = منع صريح، ✔ = منح صريح، والعودة للنقرة السابقة = وراثة.";
        }
        finally { _loading = false; }
    }

    private void RefreshCells()
    {
        foreach (var row in _rows)
            foreach (var c in row.Cells.Values) c.Refresh();
    }

    private void RefreshAuditStrip()
    {
        try { AuditUserText.Text = AppContainer.Get<DatesErp.Core.Interfaces.Services.ICurrentSession>().UserName ?? "—"; } catch { AuditUserText.Text = "—"; }
        AuditDateText.Text = DateTime.Now.ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        AuditTargetText.Text = _targetId == 0 ? "—" : (_mode == "role" ? "دور #" : "مستخدم #") + _targetId + (_targetName == "" ? "" : " — " + _targetName);
    }

    // ═══ اختيار الهدف من التبويبات — يعين القائمة العلوية ═══
    private void Role_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _syncing || RolesList.SelectedItem?.GetType().GetProperty("Id")?.GetValue(RolesList.SelectedItem) is not int id) return;
        _syncing = true;
        try { _mode = "role"; ModeBox.SelectedValue = "role"; FillTargets(); TargetBox.SelectedValue = id; }
        finally { _syncing = false; }
        LoadTargetMatrix(id);
    }

    private void User_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _syncing || UsersList.SelectedItem?.GetType().GetProperty("Id")?.GetValue(UsersList.SelectedItem) is not int id) return;
        _syncing = true;
        try { _mode = "user"; ModeBox.SelectedValue = "user"; FillTargets(); TargetBox.SelectedValue = id; }
        finally { _syncing = false; }
        LoadTargetMatrix(id);
    }

    // ═══ تحديد الكل / إلغاء الكل — على الظاهر بعد التصفية ═══
    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireTarget()) return;
        foreach (var row in _rows.ToList()) SetResourceAll(row.ResCode, true);
        RefreshCells();
    }
    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireTarget()) return;
        foreach (var row in _rows.ToList()) SetResourceAll(row.ResCode, false);
        RefreshCells();
    }

    private bool RequireTarget()
    {
        if (_targetId != 0) return true;
        AppContainer.Get<DialogService>().Info("اختر الموظف/الدور أولاً من القائمة أعلى الصفحة — المصفوفة معطّلة قبل الاختيار.");
        return false;
    }

    /// <summary>تحديد/إلغاء المورد كاملاً — في وضع المستخدم تُترك الاستثناءات المطابقة للوراثة مكانها لا تُراكم.</summary>
    private void SetResourceAll(string resCode, bool allowed)
    {
        foreach (var o in _ops)
        {
            var key = (resCode, o.Code);
            if (_mode == "role")
            {
                if (allowed && o.IsSensitive && !AppContainer.Get<DialogService>().Confirm($"⚠ منح العملية الحساسة «{o.NameAr}» على «{resCode}»؟")) continue;
                if (allowed) _pending.Add(key); else _pending.Remove(key);
            }
            else
            {
                if (_pending.Contains(key) == allowed) _exceptions.Remove(key);
                else _exceptions[key] = allowed;
            }
        }
    }

    /// <summary>إعادة كل استثناءات المستخدم إلى الوراثة الصافية من أدواره.</summary>
    private void ResetToInherited_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireTarget()) return;
        if (_mode != "user") { AppContainer.Get<DialogService>().Info("هذا الإجراء خاص بالمستخدمين — اختر «👤 مستخدم» من القائمة أعلى الصفحة."); return; }
        _exceptions.Clear();
        SyncMatrix();
        AppContainer.Get<DialogService>().Info("أُزيلت الاستثناءات مبدئياً — اضغط «حفظ التغييرات» لتثبيت العودة للوراثة.");
    }

    // ═══ الحفظ بملخص ═══
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireTarget()) return;
        if (_mode == "role") SaveRole(); else SaveUser();
    }

    private void SaveRole()
    {
        var added = _pending.Except(_baseline).ToList();
        var removed = _baseline.Except(_pending).ToList();
        if (added.Count == 0 && removed.Count == 0) { AppContainer.Get<DialogService>().Info("لا تغييرات للحفظ."); return; }
        var sensitiveAdds = added.Where(a => _ops.Any(o => o.Code == a.op && o.IsSensitive)).Select(a => $"{a.res}:{a.op}").ToList();
        string sum = $"سيُحفَظ {added.Count} منح و{removed.Count} سحب على الدور «{_targetName}».";
        if (sensitiveAdds.Count > 0) sum += $"\n⚠ منها {sensitiveAdds.Count} صلاحية حساسة: {string.Join("، ", sensitiveAdds.Take(6))}";
        if (!AppContainer.Get<DialogService>().Confirm(sum + "\n\nتأكيد الحفظ؟")) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var svc = Svc(db);
            foreach (var (res, op) in added) svc.SetRolePermission(_targetId, res, op, true);
            foreach (var (res, op) in removed) svc.SetRolePermission(_targetId, res, op, false);
            _baseline = new HashSet<(string, string)>(_pending);
            AppContainer.Get<DialogService>().Info($"تم حفظ {added.Count + removed.Count} تغييراً على «{_targetName}» وسُجلت في سجل التدقيق.");
            RefreshAuditStrip();
        }
        catch (Exception ex) { ShowError("حفظ الدور", ex); }
    }

    private void SaveUser()
    {
        var keys = _exceptions.Keys.Union(_excBaseline.Keys).ToList();
        var grants = new List<(string res, string op)>();
        var denies = new List<(string res, string op)>();
        var cleared = new List<(string res, string op)>();
        foreach (var k in keys)
        {
            bool nowHas = _exceptions.TryGetValue(k, out var nv);
            bool oldHas = _excBaseline.TryGetValue(k, out var ov);
            if (nowHas && (!oldHas || ov != nv)) { if (nv) grants.Add(k); else denies.Add(k); }
            else if (!nowHas && oldHas) cleared.Add(k);
        }
        if (grants.Count + denies.Count + cleared.Count == 0) { AppContainer.Get<DialogService>().Info("لا تغييرات للحفظ."); return; }
        var sensitiveAdds = grants.Where(a => _ops.Any(o => o.Code == a.op && o.IsSensitive)).Select(a => $"{a.res}:{a.op}").ToList();
        string sum = $"استثناءات المستخدم «{_targetName}»: {grants.Count} منح · {denies.Count} منع صريح · {cleared.Count} عودة للوراثة.";
        if (sensitiveAdds.Count > 0) sum += $"\n⚠ منها {sensitiveAdds.Count} صلاحية حساسة: {string.Join("، ", sensitiveAdds.Take(6))}";
        if (!AppContainer.Get<DialogService>().Confirm(sum + "\n\nتأكيد الحفظ؟")) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var svc = Svc(db);
            foreach (var (res, op) in grants) svc.SetUserPermission(_targetId, res, op, true);
            foreach (var (res, op) in denies) svc.SetUserPermission(_targetId, res, op, false);
            foreach (var (res, op) in cleared) svc.ClearUserPermission(_targetId, res, op);
            _excBaseline = new Dictionary<(string, string), bool>(_exceptions);
            AppContainer.Get<DialogService>().Info($"تم حفظ {grants.Count + denies.Count + cleared.Count} تغييراً على «{_targetName}» وسُجلت في سجل التدقيق.");
            RefreshAuditStrip();
        }
        catch (Exception ex) { ShowError("حفظ المستخدم", ex); }
    }

    // ═══ النسخ ═══
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var win = new Window { Title = "نسخ الصلاحيات", Width = 460, Height = 250, FlowDirection = FlowDirection.RightToLeft, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this) };
        var kind = new ComboBox(); kind.Items.Add("دور → دور"); kind.Items.Add("مستخدم → مستخدم"); kind.SelectedIndex = 0;
        var src = new ComboBox(); var dst = new ComboBox();
        void Fill()
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            if (kind.SelectedIndex == 0)
            {
                var roles = db.Roles.AsNoTracking().Select(r => new { r.Id, r.RoleNameAr }).ToList();
                src.ItemsSource = roles; src.DisplayMemberPath = "RoleNameAr"; src.SelectedValuePath = "Id";
                dst.ItemsSource = roles; dst.DisplayMemberPath = "RoleNameAr"; dst.SelectedValuePath = "Id";
            }
            else
            {
                var users = db.Users.AsNoTracking().Select(u => new { u.Id, Label = u.FullName }).ToList();
                src.ItemsSource = users; src.DisplayMemberPath = "Label"; src.SelectedValuePath = "Id";
                dst.ItemsSource = users; dst.DisplayMemberPath = "Label"; dst.SelectedValuePath = "Id";
            }
        }
        kind.SelectionChanged += (_, _) => Fill();
        Fill();
        var btn = new Button { Content = "تنفيذ النسخ", ToolTip = "يُسجل كتغيير جماعي في التدقيق", Style = (Style)System.Windows.Application.Current.FindResource("ErpPrimaryButton"), Margin = new Thickness(0, 10, 0, 0) };
        btn.Click += (_, _) =>
        {
            try
            {
                using var scope = AppContainer.NewScope();
                var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                var svc = Svc(db);
                int s = (int)src.SelectedValue, d = (int)dst.SelectedValue;
                if (kind.SelectedIndex == 0) svc.CopyRolePermissions(s, d); else svc.CopyUserPermissions(s, d);
                AppContainer.Get<DialogService>().Info("تم النسخ وتسجيله في السجل.");
                win.Close(); LoadAll();
            }
            catch (Exception ex) { AppContainer.Get<DialogService>().Error(ex.Message); }
        };
        var p = new StackPanel { Margin = new Thickness(14) };
        p.Children.Add(new TextBlock { Text = "المصدر:", FontWeight = FontWeights.Bold }); p.Children.Add(src);
        p.Children.Add(new TextBlock { Text = "الهدف:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 0) }); p.Children.Add(dst);
        p.Children.Add(kind); p.Children.Add(btn);
        win.Content = p; win.ShowDialog();
    }

    // ═══ المقارنة ═══
    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        using var scope = AppContainer.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var svc = Svc(db);
        var roles = db.Roles.AsNoTracking().ToList();
        var win = new Window { Title = "مقارنة دورين (الفروقات)", Width = 860, Height = 520, FlowDirection = FlowDirection.RightToLeft, Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var aBox = new ComboBox { ItemsSource = roles, DisplayMemberPath = "RoleNameAr", SelectedValuePath = "Id", Width = 200 };
        var bBox = new ComboBox { ItemsSource = roles, DisplayMemberPath = "RoleNameAr", SelectedValuePath = "Id", Width = 200 };
        var grid = new DataGrid { AutoGenerateColumns = false, Height = 380, IsReadOnly = true };
        grid.Columns.Add(new DataGridTextColumn { Header = "المورد", Binding = new System.Windows.Data.Binding("Res"), Width = 220 });
        grid.Columns.Add(new DataGridTextColumn { Header = "العملية", Binding = new System.Windows.Data.Binding("Op"), Width = 160 });
        grid.Columns.Add(new DataGridTextColumn { Header = "أ", Binding = new System.Windows.Data.Binding("A"), Width = 70 });
        grid.Columns.Add(new DataGridTextColumn { Header = "ب", Binding = new System.Windows.Data.Binding("B"), Width = 70 });
        void Run()
        {
            if (aBox.SelectedValue is not int ai || bBox.SelectedValue is not int bi) return;
            var sa = svc.GetRoleSet(ai); var sb = svc.GetRoleSet(bi);
            var rows = sa.Union(sb).OrderBy(x => x.res).ThenBy(x => x.op)
                .Where(x => sa.Contains(x) != sb.Contains(x))
                .Select(x => new { Res = x.res, Op = x.op, A = sa.Contains(x) ? "✔" : "—", B = sb.Contains(x) ? "✔" : "—" }).ToList();
            grid.ItemsSource = rows;
        }
        aBox.SelectionChanged += (_, _) => Run();
        bBox.SelectionChanged += (_, _) => Run();
        var sp = new StackPanel { Margin = new Thickness(12) };
        var hp = new StackPanel { Orientation = Orientation.Horizontal };
        hp.Children.Add(new TextBlock { Text = "الدور أ:", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold }); hp.Children.Add(aBox);
        hp.Children.Add(new TextBlock { Text = "الدور ب:", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold, Margin = new Thickness(12, 0, 0, 0) }); hp.Children.Add(bBox);
        sp.Children.Add(hp);
        sp.Children.Add(new TextBlock { Text = "تُعرض الفروقات فقط:", Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 6, 0, 4) });
        sp.Children.Add(grid);
        win.Content = sp; win.ShowDialog();
    }

    // ═══ السجل ═══
    private void Audit_Click(object sender, RoutedEventArgs e)
    {
        using var scope = AppContainer.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var svc = Svc(db);
        var win = new Window { Title = "سجل تغييرات الصلاحيات (غير قابل للتعديل)", Width = 900, Height = 520, FlowDirection = FlowDirection.RightToLeft, Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true };
        grid.Columns.Add(new DataGridTextColumn { Header = "التوقيت", Binding = new System.Windows.Data.Binding("ChangedAt") { StringFormat = "dd/MM/yyyy HH:mm" }, Width = 120 });
        grid.Columns.Add(new DataGridTextColumn { Header = "بواسطة", Binding = new System.Windows.Data.Binding("ChangedByName"), Width = 110 });
        grid.Columns.Add(new DataGridTextColumn { Header = "نوع", Binding = new System.Windows.Data.Binding("ActionType"), Width = 80 });
        grid.Columns.Add(new DataGridTextColumn { Header = "الهدف", Binding = new System.Windows.Data.Binding("Target"), Width = 120 });
        grid.Columns.Add(new DataGridTextColumn { Header = "المورد", Binding = new System.Windows.Data.Binding("ResourceCode"), Width = 120 });
        grid.Columns.Add(new DataGridTextColumn { Header = "العملية", Binding = new System.Windows.Data.Binding("OperationCode"), Width = 120 });
        grid.Columns.Add(new DataGridTextColumn { Header = "قبل", Binding = new System.Windows.Data.Binding("OldValue"), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "بعد", Binding = new System.Windows.Data.Binding("NewValue"), Width = 90 });
        grid.ItemsSource = svc.GetAudit().Select(a => new
        {
            a.ChangedAt, a.ChangedByName, a.ActionType,
            Target = a.TargetRoleId != null ? $"دور #{a.TargetRoleId}" : a.TargetUserId != null ? $"مستخدم #{a.TargetUserId}" : "-",
            a.ResourceCode, a.OperationCode, a.OldValue, a.NewValue
        }).ToList();
        win.Content = grid; win.ShowDialog();
    }

    // ═══ دور جديد / تفويض / تعطيل ═══
    private void NewRole_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new InputDialog("إنشاء دور جديد", "اسم الدور (مثال: مشرف جودة متقدم):") { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Value)) return;
        try
        {
            // §B110 — إنشاء الدور عبر الخدمة (كود فريد مضمون + منع تكرار + تدقيق) بدل الكتابة المباشرة
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            Svc(db).CreateCustomRole(dlg.Value.Trim());
            AppContainer.Get<DialogService>().Info("أُنشئ الدور — اختره من القائمة أعلى الصفحة وامنحه الصلاحيات من المصفوفة.");
            LoadAll();
        }
        catch (Exception ex) { ShowError("دور جديد", ex); }
    }

    private void Delegation_Click(object sender, RoutedEventArgs e)
        => new DelegationWindow { Owner = Window.GetWindow(this) }.ShowDialog();

    private void Deactivate_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireTarget()) return;
        if (!AppContainer.Get<DialogService>().Confirm(_mode == "role" ? $"تعطيل الدور «{_targetName}»؟ (لا يُحذف)" : $"تعطيل المستخدم «{_targetName}»؟ (لا يُحذف)")) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var svc = Svc(db);
            if (_mode == "role") svc.DeactivateRole(_targetId); else svc.DeactivateUser(_targetId);
            AppContainer.Get<DialogService>().Info("تم التعطيل بأمان وسُجل.");
            LoadAll();
        }
        catch (Exception ex) { ShowError("تعطيل", ex); }
    }
}

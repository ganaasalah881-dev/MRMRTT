#!/usr/bin/env python3
"""
§1.50.67 — فحص تطابق أسماء عناصر XAML مع مراجع C# قبل الإصدار.
يكشف مراجع مثل SaveActionBtn, HoldRadio, ApproveRadio غير موجودة في XAML.
يُستخدم في CI وفي بناء الحزمة: يجب أن يكون 0 أخطاء قبل Publish.
"""
import re
import pathlib
import sys

root = pathlib.Path(__file__).parent.parent.parent / "src" / "DatesErp.Desktop" / "Views"
errors = []
warnings = []

# Suffixes that indicate XAML element
ELEMENT_SUFFIXES = ('Btn','Radio','Box','Grid','Panel','Chip','Badge','Text','Progress','Banner','Column','Picker','List','View','ActionsPanel','Summary','MetaBox','Status','Remaining','Tot','Step','Period','Scope','Scheduled','Capacity','Insert','SingleCust','MultiRadio','SingleRadio','StartPanel','EndPanel','StartLabel','CodeBox','TitleBox','TypeBox','StartBox','EndBox','ShiftBox','LineBox','NotesBox','CustLotsBtn','ApproveActionBtn','SaveActionBtn','SubmitBtn','ReturnBtn','EditRowBtn','CustomerColumn','DateColumn','RowsGrid','GridHint','LockBanner','DocState','DuplicateWarn','GrandTotal','ItemsCount','PackagesTotal','ContainerSummary','ShipGrid','ShipsSearchBox','NotesBox')

# Known non-XAML types to ignore
IGNORE_TYPES = {'TextBox','DataGrid','ComboBox','Button','RadioButton','Border','ProgressBar','TextBlock','StackPanel','WrapPanel','DockPanel','ScrollViewer','Grid','ListView','ListBox','CheckBox','DatePicker','MessageBox','ToList','ReadAllText','WriteAllText','GetPickList','OpenBatchPicker','RefreshList','WithList','CurrentColumn','Columns','CurrentCell','SelectedItem','SelectedItems','SelectedIndex','OpenProductPicker','LoadReceiptGrid','AutoGeneratingColumn','DefaultView','ExportGrid','PopulateValueBox','SelectedDisplayColumn','ScrollIntoView','DataRowView','FillGrid','AddChip','ApprovedText','ClosedText','CreatedText','ProductionText','QualityText','ReceiptText','WarehouseText','BuildDeptScreenGrid','SetColumn','InProgress','OrderDocumentPanel','ReturnToList','RolesText','GetDefaultView','ICollectionView','IsLongTextColumn','IsNumericColumn','StageColumn','ProtectText','TitleText','DashboardView','DeleteBtn','EditBtn','NewBtn','PrintBtn','SaveBtn','SearchBtn','ApproveBtn','UnapproveBtn','OpenProductPicker','Text','Status','ReceiptStatus','Summary','Column','ProductionDeliveryView','FinishedBatchPicker','LotPicker','ProductPicker','AuditFilterView','BackupView','AuxiliarySetupView','CartonView','CustomerCartonMappingView','DashboardView','DeliveryView','FGReceiveView','FinishedGoodsView','GenericListView','ItemsCapacitiesView','ItemsView','MaterialsView','MyTasksView','OrdersView','PermissionsView','PlanClosureView','PlanningView','ProductBOMView','ProductionAuxiliaryView','ProductionDeliveryView','QualityView','ReceivingView','ReportsView','ShiftsView','ShipmentReportView','SupplierMovementReportView','SystemInfoView','UsersView','WarehouseVariablesView','ConnectionSetupWindow','LoginWindow','MainWindow','OrderDetailWindow','PlanDetailWindow','PrintPreviewWindow','ReceiptWindow','ShipmentPickerWindow','TemplatePickerWindow','SplashWindow','InputDialog','ErpChrome','CustomerAvailabilityWindow','DeliveryReviewWindow','FinishedBatchPickerWindow','LotPickerWindow','ProductPickerWindow','QCWindow','Bd','BdTool','BdGreen','BdGhost'}

for xaml_path in root.rglob("*.xaml"):
    cs_path = xaml_path.with_suffix(".xaml.cs")
    if not cs_path.exists():
        continue
    try:
        xaml_text = xaml_path.read_text(encoding='utf-8')
        cs_text = cs_path.read_text(encoding='utf-8')
    except Exception as e:
        warnings.append(f"⚠️ لا يمكن قراءة {xaml_path}: {e}")
        continue

    xnames = set(re.findall(r'x:Name="([^"]+)"', xaml_text))

    # Find all potential XAML element usages in CS: look for <Name>.IsEnabled etc
    # Pattern: \bName\b followed by . or ? or == 
    for m in re.finditer(r'\b([A-Z][a-zA-Z0-9_]+)\b\s*(?:\.|\?\s*\.|==|!=)', cs_text):
        name = m.group(1)
        if name in IGNORE_TYPES:
            continue
        if len(name) < 3:
            continue
        # Must look like element (ends with known suffix or is in xnames)
        is_element_like = name in xnames or any(name.endswith(suf) for suf in ELEMENT_SUFFIXES)
        if not is_element_like:
            continue
        if name in xnames:
            continue
        # Check if defined in CS as field/property
        if re.search(rf'\b(?:private|public|protected|internal)\b.*\b{name}\b', cs_text):
            continue
        # Check if preceded by _toolbar or other object (not direct XAML)
        start = m.start()
        before = cs_text[max(0, start-30):start]
        if '_toolbar.' in before or 'Toolbar.' in before:
            continue
        # Check if it's a local variable
        if re.search(rf'\bvar\s+{name}\b|\bint\??\s+{name}\b|\bstring\??\s+{name}\b|\bbool\??\s+{name}\b|\bdouble\??\s+{name}\b', cs_text):
            continue
        # Check if it's a type name (e.g., DeliveryView)
        if name.endswith('View') and name not in xnames:
            # Might be class name, not element
            if re.search(rf'\bclass\s+{name}\b|\bnew\s+{name}\b', cs_text):
                continue
        errors.append(f"❌ {xaml_path.name}: المرجع '{name}' مستخدم في {cs_path.name} لكنه غير موجود في XAML (x:Name). السطر ~{cs_text[:m.start()].count(chr(10))+1}")

# Also check for duplicate x:Name — ignore those inside ControlTemplate (different namescope)
for xaml_path in root.rglob("*.xaml"):
    try:
        xaml_text = xaml_path.read_text(encoding='utf-8')
    except:
        continue
    # Remove ControlTemplate content to avoid false duplicate across templates
    # Simple: find <ControlTemplate>...</ControlTemplate> and replace inner x:Name with placeholder
    def strip_templates(text):
        # Replace ControlTemplate inner content
        return re.sub(r'<ControlTemplate[^>]*>.*?</ControlTemplate>', lambda m: re.sub(r'x:Name="[^"]+"', '', m.group(0)), text, flags=re.DOTALL)
    stripped = strip_templates(xaml_text)
    # Also check outside templates for duplicates
    names = re.findall(r'x:Name="([^"]+)"', stripped)
    seen = {}
    for n in names:
        seen[n] = seen.get(n, 0) + 1
    dups = [k for k,v in seen.items() if v>1]
    if dups:
        errors.append(f"❌ {xaml_path.name}: أسماء مكررة x:Name: {dups}")

if errors:
    print("=== فحص تطابق XAML/C# — أخطاء ===")
    for e in errors:
        print(e)
    print(f"\nالإجمالي: {len(errors)} خطأ — يجب إصلاحها قبل Build")
    sys.exit(1)
else:
    print("✅ فحص XAML/C# — لا يوجد مراجع لأسماء غير موجودة")
    if warnings:
        print("\nتحذيرات:")
        for w in warnings:
            print(w)
    sys.exit(0)

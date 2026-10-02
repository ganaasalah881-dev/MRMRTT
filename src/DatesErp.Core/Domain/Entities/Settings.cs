using DatesErp.Core.Common;

namespace DatesErp.Core.Domain.Entities;

public class SystemSetting : BaseEntity
{
    public string SettingKey { get; set; }
    public string SettingValue { get; set; }
    public string DataType { get; set; } = "String";
    public string Category { get; set; }
    public string Description { get; set; }
}

/// <summary>§31/§32 — إصدار قاعدة البيانات (منفصل عن إصدار التطبيق).</summary>
public class DbVersion : BaseEntity
{
    public string VersionNumber { get; set; } // مثال: 1.0.0
    public DateTime AppliedDate { get; set; } = DateTime.Now;
    public string Description { get; set; }
    public bool IsMigration { get; set; }
}

public class NumberingScheme : BaseEntity
{
    public string SchemeCode { get; set; } // SHIP | PLAN | ORD | DLV | TXN ...
    public string SchemeName { get; set; }
    public string Prefix { get; set; }
    public int LastSequence { get; set; }
    public int SequenceDigits { get; set; } = 4;
    public bool IsActive { get; set; } = true;
}

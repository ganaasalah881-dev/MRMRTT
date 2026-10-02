# إعداد السيرفر — Date ERP

## البنية
```
SERVER01
 ├── SQL Server (Default أو Named Instance)
 │     └── DateFactory (قاعدة مركزية وحيدة)
 └── C:\MfgSystem_1.50.74_Backups (مجلد النسخ الاحتياطية لهذا الإصدار)
```

يجب إنشاء مجلد النسخ ومنح حساب خدمة SQL Server صلاحية الكتابة إليه. هذا المجلد مستقل عن إعدادات وقاعدة SQLite المحلية على أجهزة العملاء.

## خطوات الإعداد الكاملة

### 1. تثبيت SQL Server
- أي إصدار 2016+ (Express يكفي لعدد أجهزة صغير).
- أثناء التثبيت فعّل **Windows Authentication Mode**.
- للـ Named Instance سمّها بوضوح، مثال: `SERVER01\SQLEXPRESS`.

### 2. الشبكة والجدار الناري (§14/§15)
| الإعداد | القيمة |
|---|---|
| Default Instance | TCP **1433** |
| Named Instance | منفذ ثابت (مثال 1433) أو SQL Browser (UDP 1444) |
| اسم الخادم المدعوم | `SERVER01` أو `SERVER01\SQLEXPRESS` أو `192.168.1.10` |

> لا تستخدم `localhost` على الأجهزة العميلة — فقط على السيرفر نفسه.

```powershell
# أوامر الجدار الناري
New-NetFirewallRule -DisplayName "SQL TCP" -Direction Inbound -Protocol TCP -LocalPort 1433 -Action Allow
New-NetFirewallRule -DisplayName "SQL Browser UDP" -Direction Inbound -Protocol UDP -LocalPort 1444 -Action Allow
# تفعيل البروتوكول TCP للـ Named Instance من SQL Server Configuration Manager ثم إعادة تشغيل الخدمة
```

### 3. إنشاء القاعدة والمخطط
```
مجلد المثبّت → Setup-Database.bat
```
أو يدوياً:
```
sqlcmd -S SERVER01 -E -Q "IF DB_ID(N'DateFactory') IS NULL CREATE DATABASE [DateFactory]"
sqlcmd -S SERVER01 -E -d DateFactory -i Install.sql
sqlcmd -S SERVER01 -E -d DateFactory -i Seed.sql
```
- `Install.sql`: 42 جدولاً بالمفاتيح والفهارس و`rowversion` للتزامن التفاؤلي (§5).
- `Seed.sql`: الأدوار السبعة + مصفوفة الصلاحيات + المستخدمون (كلمات مرور مجزأة).

### 4. النسخ الاحتياطي (§29/§30)
`Setup-BackupPlan.sql` ينشئ مهمة يومية كاملة في SQL Server Agent:
- نسخة كاملة كل يوم الساعة 23:00 مع `WITH CHECKSUM`.
- `RESTORE VERIFYONLY` تلقائي بعد كل نسخة (لا تُعتبر النسخة ناجحة بدون تحقق).
- لتفعيل الاستعادة الاختبارية دورياً:
```
RESTORE DATABASE [DateFactory_RestoreTest] FROM DISK = N'آخر نسخة' WITH REPLACE;
```

### 5. الترقية (§32)
1. نسخة احتياطية كاملة + تحقق.
2. `SELECT TOP 1 VersionNumber FROM DbVersions ORDER BY AppliedDate DESC;`
3. نفّذ `Upgrade.sql` بعد وضع أوامر الترحيل المطلوبة فيه.
4. تحقق من `DbVersions` ثم شغّل التطبيق الجديد.

## الأمان (§13)
- Windows Authentication هو الافتراضي — لا كلمات مرور لقاعدة البيانات في أي ملف.
- كلمات مرور المستخدمين مجزأة **PBKDF2-SHA256 (100,000 دورة)** ولا تُخزن صريحة.
- قفل الحساب بعد 5 محاولات دخول فاشلة.
- سجل تدقيق كامل لكل عملية مع اسم المستخدم والجهاز (§26).

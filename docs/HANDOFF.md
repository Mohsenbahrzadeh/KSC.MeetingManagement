# Handoff — وضعیت پروژه

آخرین به‌روزرسانی: 2026-10-20. شاخهٔ `main` همیشه شامل کارهای ادغام‌شده است؛ هر Task در یک PR جدا (`feature/task-NNN-…`) ادغام شده است.

## انجام‌شده و ادغام‌شده
| حوزه | Taskها | توضیح کوتاه |
|---|---|---|
| پایه | 001–016 | ADRها، Solution، CI، لاگ و Correlation ID، Tenant، عضویت/نقش، مجوزها، Meeting، ذخیره‌سازی PostgreSQL |
| مدیریت جلسه | 017–021 | ویرایش/لغو/تعویق/جابه‌جایی با کنترل هم‌زمانی، شرکت‌کننده و دعوت مهمان (توکن هش‌شده)، شماره‌گذاری، کمیته‌ها، پیوست‌ها |
| تقویم شمسی | 022–034 | `KscJalaliCalendarService`، `KscJalaliDatePicker`، نماهای ماه/هفته/روز/Agenda، بارگیری بازه‌ای، Drag & Drop، کنترل تداخل، ماتریس RTL/موبایل |
| رابط | 035–037, 031 | MudBlazor با RTL، منو، فرم جلسه، `StatePanel`، ایجاد/ویرایش از تقویم |
| دستور جلسه تا اقدام‌ها | 038–042 | دستور جلسه، صورت‌جلسه با نسخه‌بندی و گردش تأیید، مصوبات، اقدامات و گزارش معوق |
| رسانه (بخش کد) | 045–046 | صدور توکن LiveKit و API ورود به اتاق (سازمانی و مهمان) |

آزمون: `dotnet test` — تست‌های واحد و یکپارچه (EF InMemory) سبز.

## باقی‌مانده و محدودیت‌ها
- **نیازمند زیرساخت واقعی** (در این محیط ممکن نبود): TASK-043 (LiveKit)، 044 (coturn)، 047 (اتصال SDK مرورگر و آزمون دو مرورگر)، 067–069 (ظرفیت، TURN، مرورگرها)، 072 (گزارش PoC).
- توکن و API اتاق (045/046) با مشخصات LiveKit نوشته شده ولی **روی سرور LiveKit واقعی آزمایش نشده**.
- هویت (ADR-0003): ورود کاربر واقعی هنوز پیاده نشده؛ API با هدر آزمایشی در تست و پیکربندی توسعه کار می‌کند. صفحات وب با `Api:TenantId` پیکربندی می‌شوند.
- رفتار هم‌زمانی پایگاه‌داده (تراکنش و Unique Index) فقط روی PostgreSQL واقعی کامل بررسی می‌شود؛ تست‌ها با EF InMemory غیرتراکنشی‌اند.
- Drag & Drop صفحه‌کلید و آزمون صفحه‌خوان انجام نشده (ماتریس در `docs/testing/rtl-mobile-matrix.md`).

## راه‌اندازی روی ماشین دیگر
```
git clone https://github.com/Mohsenbahrzadeh/KSC.MeetingManagement.git
dotnet build && dotnet test
```
- `global.json` نسخهٔ SDK ‏10.0.402 را می‌خواهد؛ اگر آن نسخه نصب نیست آن را محلی (بدون commit) پایین بیاورید.
- اجرای API به PostgreSQL محلی نیاز دارد (`ConnectionStrings:Default`). رمزها فقط از متغیر محیطی یا User Secrets.
- تنظیمات رسانه: `Media:ServerUrl`, `Media:ApiKey`, `Media:ApiSecret` (حداقل ۳۲ نویسه). بدون آن ورود به اتاق 503 می‌دهد.
- پیوست‌ها: `Storage:Root`, `Attachments:MaxBytes`.

## قواعد کاری
- هر Task: شاخهٔ مستقل، Build و تست سبز، سپس PR و ادغام در `main`.
- مایگریشن: `dotnet ef migrations add <Name> -p src/KSC.MeetingManagement.Infrastructure -s src/KSC.MeetingManagement.Api -o Persistence/Migrations` و بررسی با `dotnet ef migrations has-pending-model-changes`.
- موجودیت‌های EF با کلید Guid که برنامه می‌سازد و بعداً به یک Aggregate بارگذاری‌شده اضافه می‌شوند باید `ValueGeneratedNever()` داشته باشند (در غیر این‌صورت EF آن‌ها را موجود فرض می‌کند).

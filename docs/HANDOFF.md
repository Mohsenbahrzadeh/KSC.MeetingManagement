# Handoff — وضعیت پروژه

آخرین به‌روزرسانی: 2026-10-20. شاخهٔ `main` همیشه شامل کارهای ادغام‌شده است؛ هر Task در یک PR جدا (`feature/task-NNN-…`) ادغام شده است.

## انجام‌شده و ادغام‌شده
| حوزه | Taskها | توضیح کوتاه |
|---|---|---|
| پایه | 001–016 | ADRها، Solution، CI، لاگ و Correlation ID، Tenant، عضویت/نقش، مجوزها، Meeting، PostgreSQL |
| مدیریت جلسه | 017–021 | ویرایش/لغو/تعویق با کنترل هم‌زمانی، شرکت‌کننده و دعوت مهمان، شماره‌گذاری، کمیته‌ها، پیوست‌ها |
| تقویم شمسی و رابط | 022–037 | سرویس و DatePicker و نماهای تقویم، Drag & Drop، تداخل، ماتریس RTL/موبایل، MudBlazor، فرم جلسه |
| دستور جلسه تا اقدام | 038–042 | دستور جلسه، صورت‌جلسه و تأیید، مصوبات، اقدامات و گزارش معوق |
| رسانه (بخش API) | 045, 046, 051–053, 055 | توکن LiveKit، ورود به اتاق، چت، اتاق انتظار و کنترل میزبان، پایان جلسه، حضور از Webhook امضاشده |
| اعلان | 056, 057, 058 (جزئی) | Outbox تراکنشی، Worker با Retry، رویدادها و یادآورها |
| ممیزی و گزارش | 059–061 | ممیزی append-only، گزارش جلسات/حضور/اقدام/مصوبات |
| امنیت | 062–065 | Threat model، Rate limit/CORS/هدرها، آزمون انقضا، بررسی وابستگی در CI |
| عملیات | 066 (جزئی), 071 | Health و Metrics و Alert، Runbook |
| اسناد/اسکریپت (اجرانشده) | 043, 044, 070 | `deploy/media`، `deploy/backup`، آزمون پذیرش رسانه |

آزمون: `dotnet test` (تست واحد و یکپارچه با EF InMemory) و CI (قالب‌بندی، بیلد، تست، ممیزی آسیب‌پذیری) سبز. قبل از Push: `dotnet format KSC.MeetingManagement.sln --verify-no-changes`.

## باز است و دلیل
- **نیازمند زیرساخت/مرورگر واقعی (اجرا نشد):** 043 (LiveKit)، 044 (coturn)، 047 (SDK مرورگر)، 048–050 (پیش‌نمایش، کنترل رسانه، اشتراک صفحه)، 054 (اتصال مجدد سمت کلاینت)، 067–069 (ظرفیت، TURN، مرورگرها)، 072 (گزارش PoC)، 070 (بازیابی روی PostgreSQL واقعی).
- **تصمیم/ورودی لازم:** 002/003/004/005 (اجرای PostgreSQL، نمونهٔ ورود، نسخهٔ PoC، تأیید ماتریس مرورگر)، 058 (کانال اعلان مصوب؛ SMTP نوشته شده ولی آزموده نشده)، 066 (سلامت LiveKit/TURN و داشبورد واردشده).
- ورود کاربر واقعی (ADR-0003) پیاده نشده؛ API در محیط واقعی تا آن زمان 401 می‌دهد (بسته در حالت پیش‌فرض). آدرس ایمیل اعضای داخلی هم تا ساخت پروفایل نیست (اعلان‌ها Skipped).
- تمام کد رسانه فقط با Stub/رویداد ساختگی آزموده شده، نه LiveKit واقعی.
- رفتار هم‌زمانی پایگاه‌داده (تراکنش، Unique Index) فقط روی PostgreSQL واقعی کامل بررسی می‌شود؛ تست‌ها با EF InMemory غیرتراکنشی‌اند.
- Drag & Drop صفحه‌کلید و آزمون صفحه‌خوان انجام نشده.

## راه‌اندازی روی ماشین دیگر
```
git clone https://github.com/Mohsenbahrzadeh/KSC.MeetingManagement.git
dotnet build && dotnet test
```
- `global.json` نسخهٔ SDK ‏10.0.402 را می‌خواهد؛ اگر آن نسخه نصب نیست آن را محلی (بدون commit) پایین بیاورید.
- اجرای API به PostgreSQL محلی نیاز دارد (`ConnectionStrings:Default`). رمزها فقط از متغیر محیطی یا User Secrets.
- تنظیمات رسانه: `Media:ServerUrl`, `Media:ApiKey`, `Media:ApiSecret` (حداقل ۳۲ نویسه). بدون آن ورود به اتاق 503 می‌دهد.
- پیوست‌ها: `Storage:Root`, `Attachments:MaxBytes`.
- اعلان‌ها: `Notifications:*` (docs/operations/notifications.md)؛ مانیتورینگ: `Metrics:Token`؛ رله: `Turn:*`؛ امنیت: docs/security/hardening.md.

## قواعد کاری
- هر Task: شاخهٔ مستقل، Build و تست سبز، سپس PR و ادغام در `main`.
- مایگریشن: `dotnet ef migrations add <Name> -p src/KSC.MeetingManagement.Infrastructure -s src/KSC.MeetingManagement.Api -o Persistence/Migrations` و بررسی با `dotnet ef migrations has-pending-model-changes`.
- موجودیت‌های EF با کلید Guid که برنامه می‌سازد و بعداً به یک Aggregate بارگذاری‌شده اضافه می‌شوند باید `ValueGeneratedNever()` داشته باشند (در غیر این‌صورت EF آن‌ها را موجود فرض می‌کند).

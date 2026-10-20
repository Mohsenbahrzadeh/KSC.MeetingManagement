# سخت‌سازی و مدیریت Secretها (TASK-063)

## Secretها
- **هیچ Secret در مخزن نیست.** `appsettings*.json` فقط مقدار غیرحساس دارد؛ آزمون `SecurityTests.Committed_configuration_files_contain_no_secrets` هر کلید حاوی `password/secret/apikey/token/privatekey` با مقدار غیرخالی و هر رشتهٔ `Password=…` را رد می‌کند.
- در محیط توسعه: `dotnet user-secrets` برای پروژهٔ Api (`ConnectionStrings:Default` با رمز، `Media:ApiSecret`).
- در محیط استقرار: متغیر محیطی یا Secret store سکو. نام‌ها با `__`: `ConnectionStrings__Default`, `Media__ServerUrl`, `Media__ApiKey`, `Media__ApiSecret`.
- `Media:ApiSecret` حداقل ۳۲ نویسه؛ اگر `Media:*` نیمه‌کاره باشد برنامه **راه نمی‌افتد** (خطای شروع، نه خطای بعدی).
- چرخش (rotation): رمز رسانه را در LiveKit و در متغیر محیطی API هم‌زمان عوض کنید؛ توکن‌های قبلی حداکثر ۶۰ دقیقه (پیش‌فرض ۱۰) معتبرند.

## Rate limiting (پیکربندی)
| کلید | پیش‌فرض | محدوده |
|---|---|---|
| `RateLimit:GuestPerMinute` | 20 | endpointهای مهمان (`redeem`, `join-guest`) به‌ازای نشانی کلاینت |
| `RateLimit:SensitivePerMinute` | 60 | ورود اتاق، آپلود/دانلود پیوست، گزارش‌ها، ممیزی — به‌ازای کاربر |
| `RateLimit:GlobalPerMinute` | 600 | همهٔ درخواست‌ها به‌ازای نشانی کلاینت |

پاسخ: 429 با `Retry-After`. شمارنده‌ها درون‌پردازه‌ای‌اند. پشت Reverse proxy باید `ForwardedHeaders` با فهرست پراکسی‌های مورد اعتماد تنظیم شود، وگرنه همهٔ کاربران یک نشانی دیده می‌شوند (به Runbook، TASK-071 اضافه شود).

## CORS
بدون پیکربندی هیچ مبدأ دیگری مجاز نیست (API فقط توسط سرور Web و کلاینت‌های هم‌مبدأ استفاده می‌شود). برای فعال‌سازی: `Cors__AllowedOrigins=https://app.example.org,https://other.example.org` (نشانی کامل بدون مسیر؛ `*` نادیده گرفته می‌شود). متدها: GET/POST/PUT/DELETE.

## سرصفحه‌ها (API)
`X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'`, `Permissions-Policy` (دوربین/میکروفون/موقعیت/پرداخت غیرفعال)، `Cross-Origin-Resource-Policy: same-site`, `Cache-Control: no-store` (مگر اینکه Endpoint خودش تعیین کند). HSTS در غیر Development. برنامهٔ Web هم سرصفحه‌های امنیتی دارد (R-22): CSP با nonce برای import map (اسکریپت فقط از همین سایت)، `X-Frame-Options: DENY`، `nosniff`، `Referrer-Policy`، و `Permissions-Policy` که دوربین/میکروفون را فقط برای همین سایت مجاز می‌کند. `style-src` به‌خاطر MudBlazor `unsafe-inline` دارد (در مرورگر Chromium آزموده شد؛ سایر مرورگرها نه).

## آزمون‌ها
`tests/KSC.MeetingManagement.IntegrationTests/SecurityTests.cs`: سرصفحه‌ها در پاسخ‌های 200/401/403/404، محدودیت مهمان و حساس (و تفکیک کاربران)، CORS، شروع‌نشدن با پیکربندی نیمه‌کاره، اسکن Secret در فایل‌ها.

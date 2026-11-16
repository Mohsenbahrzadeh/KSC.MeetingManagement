# اجرای محلی پروژه

این راهنما برای اجرای development روی Windows/macOS/Linux است. تست‌های خودکار از EF InMemory استفاده می‌کنند، اما **اجرای خود برنامه به PostgreSQL نیاز دارد**. تماس صوتی/تصویری هم علاوه بر PostgreSQL به LiveKit/coturn قابل‌دسترسی نیاز دارد.

## پیش‌نیازها

- .NET SDK `10.0.402` (نسخهٔ pin شده در `global.json`؛ نصب نسخهٔ جدید .NET 8/9 کافی نیست).
- PostgreSQL سازگار با migrationهای پروژه (برای محیط محلی PostgreSQL 17 توصیه شده) و یک database قابل‌نوشتن.
- Git برای clone؛ مرورگر جدید برای وب. دوربین/میکروفون واقعی برای تماس، LiveKit و coturn عمومی برای media.
- اختیاری: Docker/Compose برای راه‌اندازی سرویس‌های وابسته. فایل‌های `deploy/media/docker-compose.yml` به imageهای نسخه‌دار و secret/TLS نیاز دارند و به‌تنهایی تنظیم آمادهٔ قابل اجرا نیستند.

## ۱. دریافت کد و ساخت

```powershell
git clone https://github.com/Mohsenbahrzadeh/KSC.MeetingManagement.git
cd KSC.MeetingManagement
dotnet --list-sdks
dotnet restore KSC.MeetingManagement.sln
dotnet build KSC.MeetingManagement.sln
dotnet test KSC.MeetingManagement.sln
```

## ۲. راه‌اندازی PostgreSQL

یک PostgreSQL محلی اجرا کنید، سپس یک database و role بسازید؛ مثال زیر برای `psql` با کاربر مجاز است:

```sql
CREATE ROLE ksc_dev LOGIN PASSWORD 'replace-with-a-local-password';
CREATE DATABASE ksc_meeting OWNER ksc_dev;
```

Connection string را در User Secrets پروژهٔ API ذخیره کنید، نه در فایل commit‌شده:

```powershell
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=5432;Database=ksc_meeting;Username=ksc_dev;Password=replace-with-a-local-password" --project src/KSC.MeetingManagement.Api
$key = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
dotnet user-secrets set "Auth:SigningKey" $key --project src/KSC.MeetingManagement.Api
```

> این role/password فقط نمونهٔ محیط توسعه است. در shared/dev یا production از Secret Manager امن استفاده کنید.

### اعمال migrationها

ابزار `dotnet-ef` در repository نصب/manifest نشده است. یک‌بار آن را نصب کنید (نسخهٔ EF پروژه 10.0.13 است):

```powershell
dotnet tool install --global dotnet-ef --version 10.0.13
```

بعد migrationها را اعمال کنید:

```powershell
dotnet ef database update `
  --project src/KSC.MeetingManagement.Infrastructure `
  --startup-project src/KSC.MeetingManagement.Api
```

## ۳. اجرای API و وب

پس از اعمال migrationها، در دو پنجرهٔ ترمینال از ریشهٔ repository اجرا کنید. Launch profileهای Development آدرس‌ها و seed محلی را تنظیم می‌کنند:

پنجرهٔ اول:

```powershell
dotnet run --project src/KSC.MeetingManagement.Api --launch-profile http
```

API روی `http://localhost:5227` در دسترس خواهد بود. در database خالی، حساب development فقط در محیط Development ساخته می‌شود:

- ایمیل: `admin@example.com`
- گذرواژه: `Admin123456@`
- tenant slug: `local`

این حساب ثابت و ناامن برای استفادهٔ محلی/دورریختنی است؛ آن را روی سرور مشترک یا production در دسترس قرار ندهید.

پنجرهٔ دوم:

```powershell
dotnet run --project src/KSC.MeetingManagement.Web --launch-profile http
```

وب در `http://localhost:5268` باز می‌شود. launch profile مقدار `Api__BaseUrl` را به `http://localhost:5227/` تنظیم می‌کند تا با API هم‌خوان باشد. برای پورت/میزبان دیگر، همین متغیر را تغییر دهید.

### ورود HTTPS و دوربین

برای profile HTTPS، ابتدا گواهی توسعه را اعتماد کنید:

```powershell
dotnet dev-certs https --trust
```

در دو ترمینال دیگر همین دستورها را با profile HTTPS اجرا کنید:

```powershell
dotnet run --project src/KSC.MeetingManagement.Api --launch-profile https
```

پنجرهٔ وب:

```powershell
dotnet run --project src/KSC.MeetingManagement.Web --launch-profile https
```

وب در `https://localhost:7231` و API در `https://localhost:7154` است؛ launch profile وب API را هم از HTTPS صدا می‌زند. HTTPS برای permissionهای مرورگر مهم است. پیش از تماس، API باید به LiveKit متصل باشد؛ در غیر این صورت API بقیهٔ قابلیت‌ها را اجرا می‌کند ولی join اتاق با 503 در دسترس نیست.

## تنظیم تماس صوتی/تصویری (اختیاری)

برای تماس کامل، یک LiveKit server و coturn با DNS/TLS، پورت‌ها و تنظیمات مطابق `deploy/media/README.md` راه‌اندازی کنید. سپس secretها را برای API به‌صورت محیطی یا User Secrets تنظیم کنید:

```text
Media__ServerUrl=wss://media.example.org
Media__ApiKey=<same key as LiveKit>
Media__ApiSecret=<secret of at least 32 characters>
Turn__Urls=turn:turn.example.org:3478?transport=udp,turns:turn.example.org:5349?transport=tcp
Turn__Secret=<shared TURN REST secret>
```

مقدار `Turn:Urls` یک رشتهٔ comma-separated است و `Turn:Secret` حداقل ۳۲ نویسه است. secret واقعی را در git یا این سند قرار ندهید. برای آزمون واقعی دو مرورگر و مسیر TURN از `docs/operations/media-acceptance.md` استفاده کنید.

## توسعه و اشکال‌زدایی

- OpenAPI فقط در محیط Development روی API فعال است: `http://localhost:5227/openapi/v1.json`.
- آزمون‌ها: `dotnet test KSC.MeetingManagement.sln`؛ آزمون یکپارچهٔ معمول از EF InMemory استفاده می‌کند و جای اجرای PostgreSQL واقعی را نمی‌گیرد.
- قالب‌بندی: `dotnet format KSC.MeetingManagement.sln --verify-no-changes`.
- storage و ایندکس جست‌وجو به‌صورت پیش‌فرض زیر `App_Data` کنار برنامه ساخته می‌شوند؛ برای پاک‌سازی دادهٔ development، برنامه را متوقف و فقط دادهٔ محلی خود را بررسی/حذف کنید.
- تنظیمات production، JWT، Setup، اعلان‌ها، backup و secretها را از [HANDOFF](HANDOFF.md)، [identity](operations/identity.md)، [backup/restore](operations/backup-restore.md) و [hardening](security/hardening.md) پیروی کنید؛ credential توسعه را استفاده نکنید.

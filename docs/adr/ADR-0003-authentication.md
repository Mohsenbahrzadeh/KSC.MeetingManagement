# ADR-0003 — احراز هویت (TASK-003)

**وضعیت:** Accepted (تأیید مالک محصول، 2026-10-20)  
**تاریخ:** 2026-10-20

## پیشنهاد
- شروع با **ASP.NET Core Identity** (Cookie برای UI، JWT کوتاه‌عمر برای API/کلاینت‌های دیگر) پشت یک قرارداد `ICurrentUser` / `ITenantContext` تا اتصال بعدی به IdP سازمانی (OIDC) بدون تغییر Application ممکن باشد.
- Cookie: HttpOnly، Secure، SameSite=Lax. Token: عمر کوتاه + refresh.
- مهمان خارجی: هویت جدا با توکن دعوت محدود به یک جلسه؛ بدون نشست سازمانی.
- Tenant از claim و/یا میزبان/مسیر resolve می‌شود (TASK-011)؛ درخواست بدون Tenant معتبر رد می‌شود.

## سؤال باز
آیا سازمان‌ها به IdP خود (Keycloak، Azure AD، AD FS) متصل می‌شوند؟ اگر بله OIDC از ابتدا فعال شود.

# Runbook اپراتور (TASK-071)

هر بخش: علامت (هشدار/نشانه) ← بررسی ← اقدام ← پیگیری. دستورها را روی سرور مربوط اجرا کنید. **این Runbook از روی طراحی نوشته شده و هنوز در حادثهٔ واقعی یا تمرین آزموده نشده است؛ پس از اولین تمرین اصلاح شود.** مسیرهای مانیتورینگ: `docs/operations/monitoring.md`.

شناسهٔ همبستگی: هر پاسخ خطا `correlationId` دارد و هدر `X-Correlation-Id` در پاسخ هست؛ با آن لاگ و ممیزی (`GET /api/v1/audit`) را دنبال کنید.

## ۱. خرابی API
**علامت:** `KscApiDown`، `KscHighServerErrorRate`، کاربران خطای ۵۰۰/۵۰۳ می‌بینند.
1. `curl -s https://api/health/live` — اگر پاسخ نیامد، پردازه/کانتینر/پراکسی پایین است: وضعیت سرویس (`docker ps`، `systemctl status`) و آخرین لاگ.
2. اگر live سبز و `ready` ۵۰۳ بود: بدنهٔ JSON کدام بررسی خراب است (`database`، `storage`، `media`).
   - `database`: پایگاه داده در دسترس؟ `pg_isready`؛ رشتهٔ اتصال/رمز تغییر نکرده؟ پر بودن دیسک؟ تعداد اتصال‌ها.
   - `storage`: مسیر `Storage:Root` وجود دارد و قابل نوشتن است؟ دیسک پر؟
   - `media` Degraded = فقط تنظیمات رسانه ناقص است؛ بقیهٔ سیستم کار می‌کند.
3. لاگ JSON را با `correlationId` نمونه جست‌وجو کنید؛ خطای شروع (`Media settings are incomplete`) یعنی پیکربندی `Media__*` ناقص است.
4. اقدام: راه‌اندازی مجدد سرویس؛ اگر بعد از استقرار شروع شد، بازگشت به نسخهٔ قبلی (Artifact قبلی CI). مایگریشن‌ها برگشت‌پذیر فرض نشوند؛ پیش از ارتقا بکاپ بگیرید (`backup-restore.md`).
5. پس از رفع: `/health/ready` سبز، نرخ ۵xx پایین، اعلان‌های ماندهٔ Outbox (بخش ۷) خودکار تخلیه می‌شوند.

## ۲. خرابی SFU (LiveKit)
**علامت:** ورود به اتاق ممکن است ولی اتصال برقرار نمی‌شود؛ `ksc_room_joins_total{outcome="Granted"}` هست ولی شکایت اتصال؛ health-check پورت 7880/7881 خطا؛ لاگ API `ksc_room_control_failures`.
1. `deploy/media/scripts/health-check.sh <livekit-host> <turn-host>`.
2. `docker compose -f deploy/media/docker-compose.yml ps` و `logs livekit --tail 200` (پورت اشغال، کلید/Secret نامعتبر، IP خارجی نادرست).
3. بررسی فایروال: 7880/7881 TCP و UDP 50000–60000 باز؟ ساعت سرور با NTP هماهنگ؟ (توکن‌ها به زمان حساس‌اند: انحراف ساعت بین API و LiveKit باعث رد توکن می‌شود.)
4. توکن API: `Media__ApiKey/Secret` با `livekit.yaml` (بخش keys) یکی باشد؛ تغییر یکی بدون دیگری همهٔ ورودها را می‌شکند.
5. اقدام: `docker compose restart livekit`. جلسه‌های جاری قطع می‌شوند؛ کلاینت با درخواست دوبارهٔ `room/join` توکن تازه می‌گیرد.
6. اگر Webhook نمی‌رسد (حضور ثبت نمی‌شود): از LiveKit به `API_PUBLIC_URL/api/v1/media/webhook` دسترسی دارد؟ API ۴۰۱ می‌دهد؟ (امضا/ساعت). رویدادهای ازدست‌رفته بازتولید نمی‌شوند؛ حضور آن بازه ناقص می‌ماند — در گزارش یادداشت شود.

## ۳. خرابی TURN (coturn)
**علامت:** کاربران پشت فایروال/NAT سخت وصل نمی‌شوند ولی بقیه بله؛ در آزمون Trickle ICE کاندید `relay` نیست.
1. `bash -c 'exec 3<>/dev/tcp/<turn-host>/3478'` و `openssl s_client -connect <turn-host>:5349`.
2. `docker compose logs coturn --tail 200`: خطای `401` پشت‌سرهم = `Turn__Secret` در API با `TURN_SECRET` coturn یکی نیست یا ساعت‌ها ناهماهنگ‌اند (انقضای credential بر پایهٔ زمان است).
3. گواهی TLS (بخش ۴)، پورت‌های relay 49152–49999/UDP، `external-ip` درست؟
4. اقدام: رندر دوبارهٔ تنظیمات (`render-config.sh`) و `restart coturn`. credentialها موقت‌اند؛ کلاینت با join دوباره credential نو می‌گیرد.

## ۴. انقضای گواهی TLS
**علامت:** هشدار انقضا (≤ ۱۴ روز)، خطای گواهی مرورگر، TURN/TLS خراب. (هشدار خودکار انقضا هنوز تعریف نشده؛ بررسی دستی هفتگی: `echo | openssl s_client -connect host:443 2>/dev/null | openssl x509 -noout -enddate`.)
1. تمدید (مثلاً `certbot renew`) و جایگزینی در `TLS_CERT_DIR`.
2. coturn گواهی را فقط هنگام شروع می‌خواند: `docker compose restart coturn`. Proxy/وب سرور را reload کنید.
3. بررسی: `openssl s_client` تاریخ جدید، `health-check.sh` سبز.
4. پیشگیری: تمدید خودکار + یادآور تقویمی ۳۰ روز مانده.

## ۵. افزایش مصرف منابع
**علامت:** `KscHeapGrowth`، تأخیر بالا (`KscSlowRequests`)، CPU/حافظهٔ سرور بالا.
1. `GET /metrics`: نرخ درخواست، `ksc_requests_rate_limited_total` (حمله یا کلاینت معیوب)، حافظهٔ پردازه (`dotnet_process_memory_working_set`)، شمارندهٔ GC.
2. تفکیک: افزایش ناگهانی ترافیک یا یک مسیر؟ بر اساس برچسب `http_route`.
3. سقف‌ها: Rate limit (`RateLimit__*`)، اندازهٔ صفحه/بازه و سقف فایل از قبل اعمال‌اند. اگر یک نشانی مزاحم است: مسدودسازی در پراکسی/فایروال.
4. Blazor Server: هر کاربر یک مدار حافظه‌بر دارد؛ با رشد کاربران هم‌زمان حافظه بالا می‌رود (ADR-0001) — افزایش منابع یا گسترش افقی.
5. اقدام کوتاه‌مدت: restart کنترل‌شده در ساعت کم‌بار، افزایش منابع. بلندمدت: نتایج آزمون ظرفیت (TASK-067، هنوز انجام نشده).

## ۶. پایگاه داده
- پر شدن دیسک: حجم جدول‌ها (`audit_events`، `outbox_messages`، `chat_messages`، `presence_records`) را بسنجید؛ پاک‌سازی/آرشیو سیاست‌گذاری نشده (باز).
- از دست رفتن داده: بخش بازیابی در `backup-restore.md`.

## ۶الف. جست‌وجوی محتوایی
- نتیجه‌ها قدیمی یا خالی‌اند: فضای آزاد و مجوز نوشتن `Search__IndexPath` را بررسی کنید، صف `search_index_changes` را برای tenant مربوط بشمارید، سپس با حساب `OrganizationManage`، `POST /api/v1/search/rebuild` را اجرا کنید. راهنمای backup، مسیر و محدودیت تک‌نمونه‌ای در `search-index.md` است.
- در استقرار فعلی بیش از یک نمونهٔ فعال با مسیر مشترک Lucene اجرا نکنید؛ چندنمونه‌ای نیازمند توزیع صف/نمایه است.

## ۷. صف اعلان (Outbox)
**علامت:** اعلان‌ها دیر می‌رسند یا نمی‌رسند.
1. `select "Status", count(*) from outbox_messages group by 1;` — انبوه `Pending` = کانال کند/خراب؛ `Dead` = پس از چند تلاش رها شده (`LastError` علت)؛ `Skipped` = آدرسی برای عضو نبود.
2. لاگ Worker: «Outbox batch: claimed…» و «Outbox pass failed».
3. کانال: SMTP در دسترس است؟ `Notifications__Smtp__*` درست؟ بدون `PayloadKey` اعلان‌ها غیرفعال‌اند (هشدار شروع).
4. پیام `Dead` پس از رفع علت: فعلاً فقط با SQL به `Pending` برمی‌گردد (`Attempts=0`, `NextAttemptAtUtc=now`); ابزار رسمی باقی است.

## ۸. موج «نامجاز» یا رد مکرر
`KscRoomJoinRefusalsSpike` / `ksc_authorization_denied_total`: ممیزی را بر اساس `authorization.denied` و `room.join-guest` Failure فیلتر کنید (`GET /api/v1/audit?action=…&outcome=Denied`)؛ حدس توکن مهمان یا کاربر با نقش اشتباه. دعوت مشکوک را ابطال کنید (`participants/{id}/revoke`).

## تماس و ارتقا
نام مسئول، شماره تماس و زمان پاسخ‌گویی هنوز تعریف نشده است — پیش از بهره‌برداری در این جدول ثبت شود.

| نقش | نام | تماس |
|---|---|---|
| اپراتور اصلی | — | — |
| پشتیبان | — | — |

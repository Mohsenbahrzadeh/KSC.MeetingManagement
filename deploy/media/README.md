# استقرار LiveKit و coturn (TASK-043، 044)

> **وضعیت: آماده برای اجرا، آزمایش‌نشده.** این فایل‌ها در محیطی نوشته شدند که Docker/سرور عمومی نداشت؛ هیچ‌کدام اجرا نشده‌اند. معیار پذیرش (اتصال آزمایشی مرورگر، Relay از شبکهٔ محدود) را فقط روی یک VM واقعی با روش `docs/operations/media-acceptance.md` می‌توان تأیید کرد. نام تصویرها عمداً خالی است: نسخهٔ دقیق (نه `latest`) و مجوز طبق ADR-0004 هنگام استقرار ثبت شود.

## پیش‌نیاز
- VM لینوکس با IP عمومی، Docker + Compose، دامنه‌های `media.example.org` (LiveKit، پشت TLS Proxy) و `turn.example.org` (coturn) و گواهی TLS برای TURN.
- فایروال:

| پورت | پروتکل | برای |
|---|---|---|
| 443 (یا 7880 پشت Proxy) | TCP | سیگنالینگ WebSocket LiveKit (TLS در Proxy) |
| 7881 | TCP | WebRTC روی TCP (راه جایگزین) |
| 50000–60000 | UDP | رسانهٔ LiveKit |
| 3478 | UDP/TCP | TURN |
| 5349 | TCP (TLS) | TURN روی TLS (عبور از فایروال‌های سخت‌گیر) |
| 49152–49999 | UDP | Relay coturn |

## مراحل
1. `cp .env.example .env` و پر کردن مقادیر؛ رمزها: `scripts/gen-secrets.sh`.
2. `scripts/render-config.sh` (فایل‌های `*.rendered.*` را می‌سازد؛ حاوی Secret‌اند و در git نیستند).
3. `docker compose up -d`
4. `scripts/health-check.sh <livekit-host> <turn-host>`
5. در API (متغیر محیطی): `Media__ServerUrl=wss://media.example.org`، `Media__ApiKey`، `Media__ApiSecret` (همان `LIVEKIT_API_KEY/SECRET`)، `Media__RecordingPath=/recordings` (همان mount مسیر Egress)، `Turn__Urls=turn:turn.example.org:3478?transport=udp,turns:turn.example.org:5349?transport=tcp`، `Turn__Secret` (همان `TURN_SECRET`).
6. LiveKit به آدرس `${API_PUBLIC_URL}/api/v1/media/webhook` رویداد می‌فرستد (حضور و غیاب)؛ API امضا را با همان کلید بررسی می‌کند.

## امنیت
- credential رله موقت است: API برای هر ورود به اتاق نام‌کاربری `انقضا:هویت` و رمز `HMAC-SHA1` می‌سازد؛ حساب دائمی در coturn نیست.
- coturn دسترسی به شبکه‌های خصوصی/Loopback را (denied-peer-ip) می‌بندد و سهمیه دارد؛ TLS قدیمی غیرفعال است.
- `LIVEKIT_API_SECRET` و `TURN_SECRET` فقط در `.env` سرور و متغیر محیطی API.
- برای ضبط، Egress و Redis روی شبکهٔ میزبان اجرا می‌شوند؛ Redis فقط روی loopback گوش می‌دهد. مقدار `RECORDING_ROOT` باید یک پوشهٔ پایدار، با فضای کافی و دسترسی نوشتن برای فرایند Egress باشد. در compose، آن پوشه داخل Egress با مسیر `/recordings` mount می‌شود.
- RoomComposite Egress برای هر ضبط می‌تواند ۲ تا ۶ هستهٔ CPU مصرف کند؛ مستندات LiveKit حداقل ۴ CPU و ۴ GB RAM برای worker را پیشنهاد می‌کنند. سیاست نگهداری/پاک‌سازی ضبط‌ها و اطلاع‌رسانی/رضایت شرکت‌کنندگان را پیش از استفادهٔ عملیاتی تعیین کنید.

## موارد ناتمام
نسخهٔ دقیق imageهای LiveKit/Egress/Redis/Coturn، مجوز، تنظیم Proxy/TLS برای LiveKit، چرخش گواهی TURN و آزمون واقعی ظرفیت/TURN (TASK-067/068) باید روی میزبان استقرار انجام شود؛ روش ظرفیت در `docs/operations/capacity-test.md` است. ضبط تا وقتی Egress، Redis و پوشهٔ پایدار تنظیم نشده باشند با پیام «پیکربندی نشده» در دسترس نخواهد بود.

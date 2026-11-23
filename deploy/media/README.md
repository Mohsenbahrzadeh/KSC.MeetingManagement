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
7. برای رونویسی داخلی، مدل چندزبانهٔ Whisper را جداگانه و از منبع مورداعتماد دریافت کنید؛ دانلود مدل در زمان اجرای برنامه انجام نمی‌شود. مدل را read-only به API mount کنید و تنظیم کنید: `Media__TranscriptionModelPath=/models/ggml-large-v3-turbo.bin`، `Media__TranscriptionLanguage=fa`، `Media__FfmpegPath=ffmpeg` و `Media__TranscriptionRecordingPath=/recordings`.
8. در compose استقرار API (این فایل فقط پشتهٔ رسانه را تعریف می‌کند) همان `${RECORDING_ROOT}` را با مجوز فقط‌خواندنی در API به `/recordings` و پوشهٔ مدل را به `/models` متصل کنید. همچنین مسیری پایدار و writable برای `Media__TranscriptionOutputPath` تعریف کنید (مثلاً `/app/App_Data/transcripts`). Image API باید FFmpeg و پیش‌نیازهای Native Whisper را داشته باشد. فایل خروجی Egress به شکل `ksc-{tenantId}-{meetingId}-{time}.mp4` است. پس از پایان ضبط، برگزارکننده دکمهٔ «رونویسی محلی» را می‌زند؛ متن به شکل `{recording-name}.transcript.json` در مسیر خروجی ذخیره می‌شود و هیچ صوتی به سرویس ابری فرستاده نمی‌شود.

## امنیت
- credential رله موقت است: API برای هر ورود به اتاق نام‌کاربری `انقضا:هویت` و رمز `HMAC-SHA1` می‌سازد؛ حساب دائمی در coturn نیست.
- coturn دسترسی به شبکه‌های خصوصی/Loopback را (denied-peer-ip) می‌بندد و سهمیه دارد؛ TLS قدیمی غیرفعال است.
- `LIVEKIT_API_SECRET` و `TURN_SECRET` فقط در `.env` سرور و متغیر محیطی API.
- برای ضبط، Egress و Redis روی شبکهٔ میزبان اجرا می‌شوند؛ Redis فقط روی loopback گوش می‌دهد. مقدار `RECORDING_ROOT` باید یک پوشهٔ پایدار، با فضای کافی و دسترسی نوشتن برای فرایند Egress باشد. در compose، آن پوشه داخل Egress با مسیر `/recordings` mount می‌شود.
- برای رونویسی، API به فایل ضبط، مدل محلی و مسیر writable خروجی transcript نیاز دارد؛ mount ضبط و مدل در API باید read-only باشد. پکیج `Whisper.net.Runtime` به کتابخانهٔ بومی نیاز دارد؛ مدل `large-v3-turbo` بزرگ است و CPU/RAM کافی لازم دارد. مسیر جایگزین مدل کوچک‌تر را می‌توان در `Media__TranscriptionModelPath` تنظیم کرد. FFmpeg باید در image/API نصب شده باشد.
- فایل MP4 و transcript اطلاعات جلسه‌اند؛ دایرکتوری ضبط باید با دسترسی محدود، backup و سیاست نگهداری مناسب اداره شود. endpoint رونویسی و دریافت متن فقط برای برگزارکننده مجاز است.
- RoomComposite Egress برای هر ضبط می‌تواند ۲ تا ۶ هستهٔ CPU مصرف کند؛ مستندات LiveKit حداقل ۴ CPU و ۴ GB RAM برای worker را پیشنهاد می‌کنند. سیاست نگهداری/پاک‌سازی ضبط‌ها و اطلاع‌رسانی/رضایت شرکت‌کنندگان را پیش از استفادهٔ عملیاتی تعیین کنید.

## موارد ناتمام
نسخهٔ دقیق imageهای LiveKit/Egress/Redis/Coturn، مجوز، تنظیم Proxy/TLS برای LiveKit، چرخش گواهی TURN و آزمون واقعی ظرفیت/TURN (TASK-067/068) باید روی میزبان استقرار انجام شود؛ روش ظرفیت در `docs/operations/capacity-test.md` است. ضبط تا وقتی Egress، Redis و پوشهٔ پایدار تنظیم نشده باشند با پیام «پیکربندی نشده» در دسترس نخواهد بود.

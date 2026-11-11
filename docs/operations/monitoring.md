# مانیتورینگ (TASK-066)

## آنچه پیاده شده (و در آزمون‌ها ثابت شده)
| مورد | نشانی/محل | توضیح |
|---|---|---|
| زنده بودن | `GET /health/live` | بدون وابستگی؛ ۲۰۰ تا وقتی پردازه کار می‌کند |
| آمادگی | API: `GET /health/ready` | بررسی پایگاه داده، نوشتنی‌بودن ذخیره‌سازی، پیکربندی و HTTP health سرور LiveKit، پیکربندی و دسترس‌پذیری listenerهای TURN. وابستگی اصلی DB/storage خراب: ۵۰۳؛ رسانه یا TURN اختیاری غایب/قطع: ۲۰۰ با `Degraded`. جزئیات حساس در پاسخ نیست |
| متریک | API و Web: `GET /metrics` (قالب Prometheus) | در هر دو پردازه فقط اگر `Metrics:Token` با حداقل ۱۶ نویسه تنظیم باشد وجود دارد و Bearer token می‌خواهد؛ بدون توکن endpoint نگاشت نمی‌شود (۴۰۴) |
| لاگ | stdout به‌صورت JSON | شناسهٔ همبستگی `X-Correlation-Id` در هر لاگ و پاسخ و ممیزی (TASK-010) |

### متریک‌های برنامه (`KSC.MeetingManagement`)
- `ksc_room_joins_total{outcome}` — درخواست ورود به اتاق با نتیجه (`Granted`, `NotAllowed`, `NotOpen`, `NotOnline`, `NotConfigured`, `MeetingNotFound`)
- `ksc_requests_rate_limited_total` — درخواست‌های ردشده با Rate limit
- `ksc_authorization_denied_total` — درخواست‌های ردشده به‌دلیل مجوز (۴۰۳)
- `ksc_audit_failures_total` — رخدادهای ممیزی که نوشته نشدند (باید صفر باشد)
- به‌علاوه متریک‌های `System.Runtime` (`dotnet_*`: GC، حافظه، رشته‌ها)، ASP.NET Core Hosting (`http_server_request_duration_count/_sum` با برچسب‌های `http_response_status_code`, `http_route`, `http_request_method`)، Kestrel و RateLimiting. هیستوگرام‌ها به‌صورت `_count` و `_sum` صادر می‌شوند (بدون Bucket).
- برچسب‌ها فقط مجموعه‌های کوچک ثابت‌اند؛ شناسهٔ کاربر/Tenant/جلسه هرگز برچسب نمی‌شود.
- در پردازهٔ Web، `ksc_blazor_active_circuits` شمارندهٔ مدارهای فعال همان instance است؛ Prometheus را جداگانه به Web scrape کنید. پشت چند replica، این gauge جمع‌پذیر است اما شمارندهٔ هر instance به‌تنهایی دید محلی است.
- API متریک `ksc_dependency_health{component}` را هنگام درخواست `/health/ready` به‌روز می‌کند: `1` سالم، `0.5` تنزل‌یافته، `0` ناسالم. این gauge آخرین مشاهده در هر instance است؛ Prometheus باید readiness را scrape کند تا تازه بماند.

## اتصال به Prometheus
```yaml
scrape_configs:
  - job_name: ksc-api
    metrics_path: /metrics
    authorization:
      type: Bearer
      credentials_file: /etc/prometheus/ksc-metrics-token   # همان Metrics__Token
    static_configs:
      - targets: ["api.internal:8080"]
  - job_name: ksc-web
    metrics_path: /metrics
    authorization:
      type: Bearer
      credentials_file: /etc/prometheus/ksc-metrics-token
    static_configs:
      - targets: ["web.internal:8080"]
  # Use the normal authenticated metrics scrape to update ksc_dependency_health.
  # A separate blackbox HTTP probe can monitor endpoint reachability if desired.
```
بررسی `/health/ready` را برای بالانسر/Orchestrator (Readiness probe) و `/health/live` را برای Liveness probe استفاده کنید.

## هشدارها (پیشنهادی، آزمایش‌نشده روی Prometheus واقعی)
```yaml
groups:
  - name: ksc
    rules:
      - alert: KscApiDown
        expr: up{job="ksc-api"} == 0
        for: 2m
        labels: {severity: critical}
        annotations: {summary: "API در دسترس نیست (Runbook: خرابی API)"}
      - alert: KscDependencyDegraded
        expr: ksc_dependency_health < 1
        for: 5m
        labels: {severity: warning}
        annotations: {summary: "یکی از وابستگی‌های API ناسالم یا تنزل‌یافته است؛ برچسب component و /health/ready را بررسی کنید"}
      - alert: KscHighServerErrorRate
        expr: |
          sum(rate(http_server_request_duration_count{http_response_status_code=~"5.."}[5m]))
            / sum(rate(http_server_request_duration_count[5m])) > 0.05
        for: 5m
        labels: {severity: critical}
        annotations: {summary: "بیش از ۵٪ پاسخ‌ها ۵xx است"}
      - alert: KscSlowRequests
        expr: |
          sum(rate(http_server_request_duration_sum[5m])) / sum(rate(http_server_request_duration_count[5m])) > 1
        for: 10m
        labels: {severity: warning}
        annotations: {summary: "میانگین زمان پاسخ بیش از ۱ ثانیه"}
      - alert: KscAuditWriteFailures
        expr: increase(ksc_audit_failures_total[15m]) > 0
        labels: {severity: critical}
        annotations: {summary: "ثبت ممیزی ناموفق است (ردپای عملیات حساس ناقص می‌شود)"}
      - alert: KscRoomJoinRefusalsSpike
        expr: sum(increase(ksc_room_joins_total{outcome=~"NotAllowed|NotConfigured"}[10m])) > 50
        labels: {severity: warning}
        annotations: {summary: "ورود به اتاق مکرراً رد می‌شود (حدس توکن یا خرابی پیکربندی رسانه)"}
      - alert: KscRateLimitingActive
        expr: increase(ksc_requests_rate_limited_total[10m]) > 100
        labels: {severity: warning}
        annotations: {summary: "Rate limit زیاد فعال می‌شود (حمله یا کلاینت معیوب)"}
      - alert: KscHeapGrowth
        expr: dotnet_process_memory_working_set > 1.5e9
        for: 15m
        labels: {severity: warning}
        annotations: {summary: "حافظهٔ پردازه بالا است"}
```

وضعیت جزئی هر dependency در gauge `ksc_dependency_health{component=...}` صادر می‌شود؛ برای تازه‌ماندن آن، scrape معمول `/metrics` کافی است.

## داشبورد
`docs/operations/grafana-dashboard.json` — Grafana با منبع داده Prometheus؛ پنل‌ها: نرخ درخواست، نرخ ۵xx، میانگین زمان پاسخ، ورود به اتاق به تفکیک نتیجه، Rate limit و ردشدن مجوز، شکست ممیزی، حافظه، وضعیت هر dependency و مدارهای فعال Web. gauge اجزا در هر بار درخواست `/health/ready` به‌روز می‌شود؛ endpoint readiness را مرتب probe کنید. JSON با parser محلی اعتبارسنجی شود؛ **وارد کردن (Import) آن در Grafana واقعی آزمایش نشده است.**

## محدودیت‌ها
- LiveKit probe به endpoint HTTP `/` با مهلت ۳ ثانیه می‌زند؛ TURN probe از هر URL تنظیم‌شده TCP connect یا STUN Binding روی UDP انجام می‌دهد. این‌ها فقط دسترس‌پذیری سرویس/listener را می‌سنجند، نه credential واقعی TURN، تخصیص relay candidate یا رسانهٔ مرورگر را؛ آزمون واقعی طبق `media-acceptance.md` لازم است.
- `ksc_blazor_active_circuits` در سطح پردازه است و disconnectهای ناگهانی تا callback بسته‌شدن مدار ممکن است دیر منعکس شوند؛ نرخ کاربر تجاری یا ظرفیت تأییدشده نیست.
- `/metrics` را در شبکهٔ داخلی محدود کنید؛ توکن جایگزین محدودیت شبکه نیست. Token یکسان را به API، Web و Prometheus secret file از Secret Store تزریق کنید.
- Dashboard فقط به‌صورت JSON قابل parse بررسی شده؛ import/اجرای queryها و alertها در Prometheus/Grafana واقعی هنوز تأیید نشده است.

# مانیتورینگ (TASK-066)

## آنچه پیاده شده (و در آزمون‌ها ثابت شده)
| مورد | نشانی/محل | توضیح |
|---|---|---|
| زنده بودن | `GET /health/live` | بدون وابستگی؛ ۲۰۰ تا وقتی پردازه کار می‌کند |
| آمادگی | `GET /health/ready` | بررسی پایگاه داده، ذخیره‌سازی فایل (نوشتنی بودن) و پیکربندی رسانه. ۵۰۳ اگر پایگاه داده یا ذخیره‌سازی خراب باشد؛ ۲۰۰ با وضعیت `Degraded` اگر فقط رسانه پیکربندی نشده باشد. جزئیات خطا هرگز در پاسخ نیست |
| متریک | `GET /metrics` (قالب Prometheus) | فقط وقتی `Metrics:Token` (حداقل ۱۶ نویسه) تنظیم باشد وجود دارد و با `Authorization: Bearer <token>` در دسترس است؛ وگرنه ۴۰۴ |
| لاگ | stdout به‌صورت JSON | شناسهٔ همبستگی `X-Correlation-Id` در هر لاگ و پاسخ و ممیزی (TASK-010) |

### متریک‌های برنامه (`KSC.MeetingManagement`)
- `ksc_room_joins_total{outcome}` — درخواست ورود به اتاق با نتیجه (`Granted`, `NotAllowed`, `NotOpen`, `NotOnline`, `NotConfigured`, `MeetingNotFound`)
- `ksc_requests_rate_limited_total` — درخواست‌های ردشده با Rate limit
- `ksc_authorization_denied_total` — درخواست‌های ردشده به‌دلیل مجوز (۴۰۳)
- `ksc_audit_failures_total` — رخدادهای ممیزی که نوشته نشدند (باید صفر باشد)
- به‌علاوه متریک‌های `System.Runtime` (`dotnet_*`: GC، حافظه، رشته‌ها)، ASP.NET Core Hosting (`http_server_request_duration_count/_sum` با برچسب‌های `http_response_status_code`, `http_route`, `http_request_method`)، Kestrel و RateLimiting. هیستوگرام‌ها به‌صورت `_count` و `_sum` صادر می‌شوند (بدون Bucket).
- برچسب‌ها فقط مجموعه‌های کوچک ثابت‌اند؛ شناسهٔ کاربر/Tenant/جلسه هرگز برچسب نمی‌شود.

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
  - job_name: ksc-ready
    metrics_path: /health/ready        # با blackbox exporter (HTTP probe) بهتر است
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

## داشبورد
`docs/operations/grafana-dashboard.json` — Grafana با منبع داده Prometheus؛ پنل‌ها: نرخ درخواست، نرخ ۵xx، میانگین زمان پاسخ، ورود به اتاق به تفکیک نتیجه، Rate limit و ردشدن مجوز، شکست ممیزی، حافظه. **وارد کردن (Import) آن در Grafana واقعی آزمایش نشده است.**

## محدودیت‌ها
- سلامت سرور رسانه (LiveKit) و TURN هنوز بررسی نمی‌شود؛ پس از استقرار (TASK-043/044) باید به `ready` یا به blackbox probe اضافه شود.
- متریک ظرفیت و نشست‌های Blazor Server (مدار فعال) هنوز اضافه نشده است.
- `/metrics` بر شبکهٔ داخلی محدود شود؛ توکن جایگزین محدودیت شبکه نیست.

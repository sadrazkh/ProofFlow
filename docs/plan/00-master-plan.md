# ProofFlow — پلن اصلی (معماری و فازبندی)

> **این سند «پلنی که هست» است** — همان پلن مصوب ابتدای پروژه، منتقل‌شده به داخل مخزن تا جلسات
> بعدی از خود مخزن بخوانندش، نه از حافظه یک گفت‌وگو.
>
> **وضعیت تا این لحظه:** فاز A کامل شده و نیمه موتوری فاز B هم (جزئیات در
> [progress.md](../progress.md)). سند همراه این سند، [01-design-plan.md](01-design-plan.md) است که
> آیتم‌های طراحی هر فاز را جفت همین فازها تعریف می‌کند. **هر جلسه اجرا از هر دو سند آیتم برمی‌دارد.**

## Context

هدف: ساخت **ProofFlow**، یک پلتفرم Visual و No-Code برای تست API، Regression، Snapshot و سناریوهای
چندمرحله‌ای — طوری که کاربر غیربرنامه‌نویس بتواند بدون نوشتن JavaScript یا C# کل سناریوی بخش ۲۱
سند نیازمندی‌ها را انجام دهد.

- ProofFlow یک **مخزن Git مستقل** است (`E:\Cash.Net\source\repos\sadrazkh\ProofFlow`) که الگوهای
  اثبات‌شده دو مخزن همسایه (FlowForge و Harbora) را اقتباس و بازنویسی می‌کند: یک Solution
  ASP.NET Core، Razor برای صفحات، Vue 3 به‌صورت Islands با Vite داخل `wwwroot/build`، بدون SPA
  جدا، EF Core با SQLite برای dev/test و PostgreSQL برای production.
- هر فاز باید به یک **محصول واقعاً قابل اجرا و تست‌شده** ختم شود — نه Skeleton — و ۲۰ گام پذیرش
  در سریع‌ترین زمان ممکن قابل انجام شوند. پیشرفت در `docs/progress.md` صادقانه ثبت می‌شود.

## تصمیمات فنی (ADR خلاصه)

| # | تصمیم | چرا |
|---|-------|-----|
| 1 | **.NET 10**، ASP.NET Core MVC + Razor، Vue 3 Islands با Vite + TypeScript + `vue-tsc` | یک Application یکپارچه و قابل Deploy، بدون SPA جدا |
| 2 | **EF Core 10 دوگانه**: SQLite پیش‌فرض dev/test/demo، PostgreSQL برای production، دو مجموعه Migration مجزا | ماشین توسعه نه Docker دارد نه Postgres؛ SQLite تنها مسیر Demo و Screenshot محلی است؛ Postgres در CI راستی‌آزمایی می‌شود |
| 3 | **Design System دست‌نویس سه‌لایه** (primitive → semantic → component) در CSS خالص، بدون Tailwind | «نباید ظاهر Generic Bootstrap Admin داشته باشد»؛ Canvas و Diff Viewer به‌هرحال CSS اختصاصی می‌خواهند |
| 4 | **Vue Flow** برای Canvas (`@vue-flow/core` + background/controls/minimap) + `@dagrejs/dagre` برای Auto Layout | «از ساخت دستی یک Graph Editor ضعیف خودداری کن» |
| 5 | **`ProofFlow.TestEngine` مستقل از UI و Infrastructure** — پورت‌های خودش را تعریف می‌کند | Engine بدون وب و دیتابیس قابل Unit Test است |
| 6 | **Node Registry**: هر Node یک `INodeHandler` مجزا با `NodeSpec` خودش؛ Catalog از Handlerهای ثبت‌شده ساخته می‌شود | Node‌ای که Engine نتواند اجرا کند در Palette ظاهر نمی‌شود |
| 7 | **JSON با `System.Text.Json.Nodes`**؛ `JsonPath.Net` و `JsonSchema.Net` (هر دو MIT) | بومی STJ، بدون Newtonsoft |
| 8 | **Diff مستقل و Semantic**: `SnapshotNormalizer` → `SemanticDiffEngine` → درخت `DiffNode` | مقایسه String ممنوع است |
| 9 | **صف Job پایدار در دیتابیس** با Lease/Claim + Reconciler در Boot؛ Worker پروسه مجزا | Restart نباید کار در حال اجرا را گم کند |
| 10 | **Live Run با جدول `RunEvent` (Append-only) + Tailer به SignalR Group** | روی هر دو Provider و هر دو توپولوژی کار می‌کند |
| 11 | **SSRF: اعتبارسنجی روی IP واقعی متصل‌شده** در `SocketsHttpHandler.ConnectCallback` | تنها راه بستن DNS Rebinding — **پیاده شد** |
| 12 | **Secret با AES-256-GCM** و Key Version؛ Redaction در Log و Viewer و Export | Plain Text ممنوع — **پیاده شد** |
| 13 | **متن‌ها فقط از کاتالوگ JSON**: Islandها Dictionary خود را از Razor می‌گیرند | هیچ متن Hard-code در Component |
| 14 | **Graph هم Normalized و هم Versioned**: ردیف‌های پیش‌نویس + `ScenarioVersion` با سند JSON کانونی | Graph غیرقابل Version ممنوع |

## ساختار Solution

```
src/  Domain · Contracts · Application · TestEngine · Infrastructure · Web · Worker
tests/  Tests (unit) · IntegrationTests · FakeApi · [e2e زیر Web/e2e]
```

قاعده وابستگی: `Domain ← Application ← Infrastructure ← Web/Worker`؛ `TestEngine` فقط `Domain` و
`Contracts` را می‌شناسد. Architecture Test این را نگه می‌دارد.

## مدل داده (خلاصه Entityها)

- **Tenancy/Identity:** Workspace، WorkspaceMember(Role)، User، ApiToken، AuditLog، Tag ✅
- **Project:** Project ✅، ProjectEnvironment ✅، EnvironmentVariable ✅، Secret ✅
- **Authoring:** TestSuite، TestScenario، ScenarioVersion، WorkflowNode، WorkflowConnection
- **Data:** DataSet، DataSetVersion، DataSetRow
- **Baseline:** Baseline، BaselineVersion، ComparisonRule، BaselineSample
- **Capture:** CaptureSession، CaptureSample
- **Execution:** TestRun (با Snapshot کامل Definition)، TestRunAttempt، NodeRun، AssertionResult، RunEvent، RunArtifact
- **Ops:** Schedule، Runner، Approval، Job، IdempotencyRecord

Payload بزرگ هرگز مستقیم در ستون: `IPayloadStore` (Db/File) + `RunArtifact` تا بعداً Object Storage
بدون تغییر Caller اضافه شود. Sweeper سیاست Retention را اعمال می‌کند.

## فازها

هر فاز: **Build ⇒ Test ⇒ اجرا با داده Demo ⇒ مرور صفحات با Screenshot** — مشکلات همان‌جا اصلاح.

- **A — Foundation** ✅ (مخزن، ۷ پروژه، EF دوگانه، Identity/Workspace، Localization فا/en با RTL،
  Theme، Design Tokens و Shell، Audit پایه، CI)
- **B — Environment، Secret، Request Builder** ✅ (Secretها، UrlGuard/SSRF، HttpExecutor،
  Redaction، Variable Resolver، FakeApi، و UI کامل: صفحات محیط/Secret، Request Builder،
  Response Viewer با منوی کلیک روی فیلد)
- **C — Assertion، Baseline، Semantic Diff** ✅ ⇒ گام‌های ۵–۱۰ پذیرش
- **D — Sample-based Regression، Capture Mode، Guided Wizard** ✅ ⇒ گام‌های ۴–۱۰ کامل
- **E — Workflow Builder (Canvas)** ✅ ⇒ گام ۱۱
- **F — Workflow Runner** ✅ (اجرای گراف، Branch/Loop/Retry/Poll/Cleanup، Cancel، Log زنده) ⇒ گام‌های ۱۲–۱۵ و ۱۸
- **G — Multi-environment** ✅ (Matrix، مقایسه محیط‌ها) ⇒ گام‌های ۱۶–۱۷
- **H — Schedule، Worker، CI** ✅ (Cronos، JUnit Export، Trigger از API/CLI، Flaky Detection) ⇒ گام ۱۹
- **I — Team و Approval** ✅ (شش Role، Permissionها به‌صورت Policy، چرخه تأیید Baseline، Audit UI)
- **J — Import/Export و Templates** ✅ (OpenAPI، cURL، Postman، فرمت داخلی Git-friendly، ۱۲ Template + دمو)
- **K — Security و Private Runner** ✅ (تست‌های SSRF به‌عنوان Gate، Retention، Runner Agent با Job امضاشده)
- **L — UI/UX Polish** ✅ (Onboarding، Empty/Error States، Responsive، Keyboard، a11y، Virtual Scrolling)
- **M — Hardening نهایی** ✅ (Docker، مستندات، Backup/Restore، تست Migration، **اجرای عملی سناریوی
  ۲۰ گامی بخش ۲۱ به‌عنوان معیار پایان**)

## ریسک‌ها

| ریسک | مهار |
|------|------|
| حجم کار | هر فاز محصول قابل اجرا؛ progress.md صادق |
| Docker روی ماشین توسعه نیست | Dockerfile فقط در CI راستی‌آزمایی می‌شود — صریح گفته شود |
| Postgres محلی در دسترس نیست | مسیر محلی SQLite؛ Migrationهای Postgres در CI اعمال می‌شوند |
| SQL خاص Provider | آزمون یکسان روی هر دو Provider (ProviderCompatibilityTests — یک مورد واقعی گرفت) |
| SSRF | اعتبارسنجی در ConnectCallback + تست‌های Gate — پیاده شد |
| کندی Canvas/Diff روی ورودی بزرگ | onlyRenderVisibleElements؛ Diff سمت سرور + Virtual Scroll؛ آزمون در L |
| جابه‌جایی Branch | قبل از هر Commit: `git branch --show-current`؛ Exit Code با `; echo EXIT=$?` |

## راستی‌آزمایی هر فاز

```bash
cd /e/Cash.Net/source/repos/sadrazkh/ProofFlow && dotnet build ProofFlow.slnx ; echo EXIT=$?
```

```bash
cd /e/Cash.Net/source/repos/sadrazkh/ProofFlow && dotnet test ProofFlow.slnx ; echo EXIT=$?
```

```bash
cd /e/Cash.Net/source/repos/sadrazkh/ProofFlow/src/ProofFlow.Web && npm run typecheck && npm run build ; echo EXIT=$?
```

سپس اجرای واقعی با Seed دمو و ماتریس Screenshot (`e2e/shoot.ts`) در دو زبان، دو Theme و سه اندازه.

**معیار پایان قابل مذاکره نیست:** سناریوی ۲۰ گامی بخش ۲۱، بدون یک خط کدنویسی توسط کاربر، عملاً
اجرا و با Screenshot مستند شود. تا آن لحظه کار تمام‌شده اعلام نمی‌شود.

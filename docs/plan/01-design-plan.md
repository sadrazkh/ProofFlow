# ProofFlow — پلن طراحی همراه (Design Companion Plan)

> این سند **جفتِ** [00-master-plan.md](00-master-plan.md) است. آن سند می‌گوید هر فاز *چه چیزی*
> ساخته می‌شود؛ این سند می‌گوید همان فاز *چه شکلی* باید باشد و با چه معیاری «از نظر بصری تمام»
> حساب می‌شود. هر جلسه اجرا، آیتم‌های فاز جاری را از **هر دو** سند برمی‌دارد.
>
> مبنای این سند: تحلیل عملی وضع موجود پس از فاز A و نیمه موتوری فاز B — با مرور کد و ۸۴
> Screenshot در دو زبان، دو Theme و سه اندازه.

---

## بخش ۰ — پروتکل اجرا برای جلسات بعدی

هر جلسه (هر مدل)، به همین ترتیب:

1. `docs/progress.md` را بخوان — بگو دقیقاً کجاییم.
2. `docs/plan/00-master-plan.md` و همین سند را بخوان.
3. فاز جاری را از progress.md پیدا کن؛ آیتم‌های کارکردی‌اش را از 00 و آیتم‌های طراحی‌اش را از
   بخش ۳/۴ همین سند بردار. اگر D-0 هنوز باز است، **اول D-0**.
4. بساز. سپس:
   ```bash
   cd /e/Cash.Net/source/repos/sadrazkh/ProofFlow && dotnet build ProofFlow.slnx ; echo EXIT=$?
   ```
   ```bash
   cd /e/Cash.Net/source/repos/sadrazkh/ProofFlow && dotnet test ProofFlow.slnx ; echo EXIT=$?
   ```
   ```bash
   cd /e/Cash.Net/source/repos/sadrazkh/ProofFlow/src/ProofFlow.Web && npm run build ; echo EXIT=$?
   ```
5. اپ را با Seed دمو اجرا کن (`Demo__Seed=true` + `Demo__Password=<انتخابی>`)، سپس ماتریس
   Screenshot را بگیر و **نگاه کن**:
   ```bash
   cd /e/Cash.Net/source/repos/sadrazkh/ProofFlow/src/ProofFlow.Web && PROOFFLOW_PASSWORD=<همان> npx tsx e2e/shoot.ts ; echo EXIT=$?
   ```
6. آنچه در Screenshot غلط است را همان‌جا اصلاح کن (سابقه: هر دور مرور تا الان حداقل دو نقص
   واقعی پیدا کرده است).
7. progress.md را به‌روز کن — صادقانه: چه کار می‌کند، چه نمی‌کند. آیتم‌های انجام‌شده همین سند را
   در بخش «وضعیت اجرا» انتهای سند تیک بزن.
8. `git branch --show-current` (باید `main` باشد) → Commit.

**قواعد سخت — بدون استثنا:**

- ادعای «تمام شد» بدون Screenshot ممنوع. ادعای «تست پاس شد» بدون خروجی واقعی ممنوع.
- تست کامل‌بودن ترجمه (`TranslationCompletenessTests`) باید سبز بماند؛ هر متن جدید UI در **هر دو**
  کاتالوگ `Resources/en.json` و `Resources/fa.json`.
- در Componentها فقط Semantic Token (`--surface`، `--ink`، …)؛ Primitive (`--gray-*`، `--violet-*`)
  فقط داخل `tokens.css`.
- فقط Logical Properties (`inline-start`، نه `left`) — RTL باید خودش آینه شود.
- `dir="auto"` روی هر متنی که کاربر تایپ کرده (نام، توضیح، Label).
- آیکون جدید = ثبت در `Scripts/lib/icons.ts` (Import گزینشی؛ Barrel ممنوع — bundle را ۶ برابر می‌کند).
- محتوای فنی (URL، JSON، Hash، مدت‌زمان) همیشه mono/LTR/ارقام لاتین؛ اعداد قابل کپی به تیکت باشند.
- Exit Code واقعی: `; echo EXIT=$?` — نه Pipe که کد را قورت می‌دهد.

---

## بخش ۱ — تحلیل وضع موجود

### ۱.۱ موجودی (چه چیزی الان هست)

**صفحات:** ورود/ثبت‌نام (دوپنلی با معرفی محصول)، Denied، داشبورد (StatTile + کارت پروژه +
Empty Stateها)، فهرست/ساخت/جزئیات/تنظیمات پروژه، رویدادها (Audit)، صفحات خطای 404/403/500.

**Shell:** Sidebar جمع‌شونده با آینه RTL، Topbar با Breadcrumb، Command Palette (Ctrl+K) که از
همان نقشه Navigation سمت سرور ساخته می‌شود (فقط مسیرهای مجازِ نقش کاربر را پیشنهاد می‌دهد)،
منوی Workspace با سوییچ Theme/زبان، Toast (چیپ آیکونی — نوار رنگی کناری عمداً حذف شد)،
Confirm دولایه (ساده / phrase-typed)، محافظ «تغییرات ذخیره‌نشده».

**سیستم طراحی:** `tokens.css` سه‌لایه با گروه‌های از پیش تدارک‌دیده برای Diff (شش دسته)،
JSON Syntax و Canvas؛ `base.css` (Reset، تایپوگرافی دوخطی، سیاست ارقام)؛ `components.css`
(دکمه/فرم/کارت/Badge/جدول/منو/Dialog/Toast/Skeleton/Tab/Segmented)؛ `shell.css`؛ `pages.css`.

**زیرساخت Frontend:** Islands (`lib/islands.ts` — هنوز هیچ Island واقعی مونت نمی‌شود)،
i18n (`lib/i18n.ts` — کاتالوگ از Razor به JSON)، `lib/api.ts` (CSRF + خطای قابل‌فهم)،
`lib/theme.ts` (اعمال پیش از اولین Paint).

### ۱.۲ قوت‌ها — این‌ها «قرارداد» می‌شوند، نه سلیقه

1. **Token آینده‌نگر:** زبان بصری Diff و Canvas از الان در `tokens.css` تعریف شده؛ فازهای C و E
   نباید رنگ جدید اختراع کنند — باید همین‌ها را مصرف کنند.
2. **Dark مستقل، نه Invert:** سطح‌ها در دارک بالا می‌روند، Accent یک پله روشن‌تر، سایه = حلقه تیرگی.
3. **RTL ساختاری:** Logical Properties همه‌جا؛ مارکر فعال، فلش‌ها، انیمیشن Skeleton خودشان
   آینه می‌شوند؛ فارسی یک پله بزرگ‌تر (x-height کوتاه‌تر Vazirmatn) و بدون Letter-spacing.
4. **وضعیت = نقطه + کلمه** — هرگز رنگ تنها (کوررنگی ~۸٪ مردان).
5. **صداقت داده:** «—» وقتی چیزی اجرا نشده؛ صفر یعنی واقعاً صفر.
6. **Empty State آموزنده:** می‌گوید این چیست و یک CTA می‌دهد؛ «No data» ممنوع.
7. **خطای انسانی:** بدون Stack Trace؛ «چه شد، چیزی ذخیره شد یا نه، حالا چه کن».
8. **a11y پایه:** focus-visible یکدست، skip link، `aria-expanded`، `role=status`، `prefers-reduced-motion`.

### ۱.۳ ضعف‌ها — یافته‌های مشخص با ارجاع فایل

| # | یافته | کجا | می‌رود به |
|---|-------|-----|-----------|
| ۱ | تاریخ میلادی با فرمت `yyyy-MM-dd HH:mm` و `ToLocalTime()` **سرور** (نه Timezone بیننده) در UI فارسی؛ کلیدهای `time.minutesAgo/hoursAgo/daysAgo` تعریف شده ولی هیچ‌جا مصرف نمی‌شوند | `Views/Activity/Index.cshtml:52` | D-0.1 |
| ۲ | ورود کاربر Audit نمی‌شود؛ کلید `audit.action.user.signedIn` در هر دو کاتالوگ هست ولی `SignIn` هرگز `IAuditLog.RecordAsync` را صدا نمی‌زند | `Controllers/AccountController.cs` | D-0.2 |
| ۳ | کامپوننت‌های تعریف‌شده ولی هنوز بی‌مصرف: `tabs`، `segmented`، `skeleton`، `badge-running`، بخشی از `kbd` — بدون صفحه مرجع زنده، در اولین مصرف واقعی واگرا می‌شوند | `Scripts/styles/components.css` | D-0.3 |
| ۴ | Command Palette بدون `role="listbox"/"option"` و `aria-activedescendant`؛ منوهای Dropdown بدون ناوبری Arrow-key (فقط Escape) | `Scripts/lib/shell.ts`، `_CommandPalette.cshtml` | D-0.4 |
| ۵ | Tooltip (`.has-tip`) فقط CSS-hover — روی لمس هیچ؛ و پس‌زمینه‌اش Primitive ثابت `--gray-900` است — نقض قاعده Semantic-only خود پروژه، و در دارک Flat | `components.css` | D-0.5 |
| ۶ | z-index خام و پراکنده: 20/25/30/40/50/60/70/80/100 — قبل از آمدن Canvas و Live Console باید مقیاس Token شود | `shell.css`، `components.css` | D-0.6 |
| ۷ | Contrast فقط چشمی بررسی شده؛ axe/contrast خودکار در CI نیست (`--ink-subtle` روی `--canvas` مرزی است) | CI | D-0.7 |
| ۸ | Skeleton در هیچ صفحه‌ای به کار نرفته و Islandها بدون Placeholder مونت می‌شوند → Layout Shift وقتی Canvas/Diff بیایند | `lib/islands.ts` | D-0.8 |
| ۹ | توضیح پروژه‌های Seed فقط انگلیسی است و وسط پنل فارسی می‌نشیند (در Screenshot موبایل fa مشهود) | `Infrastructure/Seeding/DemoDataSeeder.cs` | D-0.9 |
| ۱۰ | favicon فقط SVG؛ بدون fallback PNG و بدون `manifest.webmanifest` | `wwwroot/`، `_Layout.cshtml` | D-0.10 |
| ۱۱ | «فراموشی گذرواژه» وجود ندارد — نقص محصولی؛ نیازمند ایمیل است | `AccountController` | فاز I (با SMTP) |
| ۱۲ | `shoot.ts` وقتی Login کرده، صفحات auth را نمی‌گیرد (Redirect به داشبورد) — ماتریس باید per-page حالت auth داشته باشد | `e2e/shoot.ts` | D-0.11 |

### ۱.۴ ریسک‌های بصری پیشِ رو (هنوز رخ نداده‌اند — طراحی باید پیش‌گیرانه باشد)

- **Canvas در RTL:** جهت جریان گراف باید LTR بماند (استاندارد ذهنی Flow: چپ→راست) حتی وقتی UI
  فارسی است؛ فقط Chrome اطراف Canvas آینه شود. این تصمیم در D-E مستند و تست می‌شود.
- **Diff با Payload چند-MB:** رندر DOM کامل می‌میرد؛ Virtual Scroll از روز اول D-C، نه فاز L.
- **Live Log:** آپدیت هر چند ms؛ اگر هر خط یک Node DOM با Reflow باشد، Tab یخ می‌زند؛ Buffer +
  requestAnimationFrame در D-F.

---

## بخش ۲ — قرارداد طراحی (Design Contract)

اصولی که **هر** فاز باید رعایت کند. جلسه‌ای که یکی از این‌ها را نقض کند، قبل از Commit برگردد:

1. فقط Semantic Token در Component؛ Primitive فقط در `tokens.css`.
2. فقط Logical Properties؛ هیچ `left/right` مگر با دلیل مستند (مثل جهت جریان Canvas).
3. متن کاربر همیشه `dir="auto"`؛ محتوای فنی همیشه mono/LTR/ارقام لاتین.
4. وضعیت هرگز فقط رنگ: نقطه/آیکون + کلمه.
5. «—» برای «هنوز چیزی نیست»، صفر فقط برای صفر واقعی.
6. هر فهرست خالی یک Empty State آموزنده با حداکثر یک CTA.
7. خطای Validation کنار فیلد؛ Summary فقط برای خطاهایی که به هیچ فیلدی نمی‌چسبند.
8. عملیات مخرب برگشت‌پذیر = Confirm ساده؛ برگشت‌ناپذیرِ نام‌دار = phrase-typed.
9. هر دکمه icon-only: `aria-label` + `data-tip`.
10. متن جدید = هر دو کاتالوگ + پاس ماندن تست ترجمه.
11. آیکون جدید = ثبت گزینشی در `icons.ts`.
12. Loading = Skeleton هم‌اندازهٔ محتوای نهایی (نه Spinner وسط صفحه)، تا Layout نپرد.
13. z-index فقط از مقیاس Token (پس از D-0.6).
14. Motion فقط از Tokenهای `--motion-*`/`--ease-*`؛ `prefers-reduced-motion` محترم.
15. هر فاز با ماتریس کامل Screenshot بسته می‌شود: ۲ زبان × ۲ تم × ۳ اندازه، با مرور واقعی.

---

## بخش ۳ — D-0: اصلاحات فوری (جلسه بعد، قبل از UI فاز B)

ترتیب پیشنهادی اجرا؛ هر آیتم مستقل و کوچک است:

- **D-0.1 — سرویس تاریخ.** `Dates` در `ProofFlow.Web/Infrastructure`: جلالی در `fa` (با
  `PersianCalendar` خود .NET — Dependency جدید لازم نیست)، میلادی در `en`؛ خروجی همیشه داخل
  `<time datetime="ISO-8601">`؛ نمایش نسبی («۳ ساعت پیش») با کلیدهای موجود `time.*` برای
  رویدادهای کمتر از ۷ روز؛ Timezone بیننده با یک Cookie که `main.ts` یک‌بار می‌نویسد
  (`Intl.DateTimeFormat().resolvedOptions().timeZone`) و سرور می‌خواند — Fallback: UTC با برچسب.
  مصرف اول: `Views/Activity/Index.cshtml`. در Payload/Export/JUnit همیشه ISO میلادی می‌ماند.
- **D-0.2 — Audit ورود.** `audit.RecordAsync("user.signedIn")` پس از ورود موفق در
  `AccountController.SignIn`. (IP از قبل در مدل AuditEvent هست.)
- **D-0.3 — صفحه `/design`.** مرجع زنده همه کامپوننت‌ها (دکمه‌ها، فرم، Badge/Status، جدول،
  Empty، Skeleton، Toast، Dialog، Tabs/Segmented، Tooltip، Kbd) فقط در Development
  (`IWebHostEnvironment.IsDevelopment`، در Production 404). به ماتریس `shoot.ts` هم اضافه شود —
  از این به بعد Regression بصری کامپوننت‌ها همین یک صفحه است.
- **D-0.4 — a11y ناوبری.** Palette: `role="listbox"`، `role="option"`، `aria-activedescendant`،
  `aria-selected`. منوها: `role="menu"/"menuitem"` + ArrowUp/Down/Home/End در `shell.ts`.
- **D-0.5 — Tooltip.** پس‌زمینه از Token جدید Semantic (`--tip-bg`/`--tip-ink` در هر دو تم)؛ برای
  لمس: نمایش با `:focus-visible` کافی است + قاعدهٔ «icon-only در نوارهای موبایل ممنوع — یا Label
  بگذار یا در منوی سه‌نقطه جمع کن» وارد قرارداد شود.
- **D-0.6 — مقیاس z-index.** `--z-topbar/-sidebar/-menu/-overlay/-palette/-toast/-skip` در
  `tokens.css` و جایگزینی همه اعداد خام.
- **D-0.7 — axe در CI.** `@axe-core/playwright` روی صفحات اصلی (ورود، داشبورد، پروژه‌ها،
  `/design`) در هر دو تم؛ نقض Serious/Critical = شکست بیلد. Contrast `--ink-subtle` اگر زیر
  4.5:1 بود، مقدارش اصلاح شود (نه Exception).
- **D-0.8 — Placeholder برای Islandها.** قرارداد: هر `data-island` باید Markup سروری Skeleton
  هم‌اندازه داشته باشد که پس از Mount حذف می‌شود؛ در `islands.ts` پشتیبانی و در `/design` نمونه.
- **D-0.9 — Seed دوزبانه.** توضیح پروژه‌های Demo بر اساس Culture فعال، یا خنثی (نام محصول +
  توضیح در هر دو زبان در `DemoDataSeeder`).
- **D-0.10 — favicon و manifest.** PNG 32/192/512 + `manifest.webmanifest` + `apple-touch-icon`.
- **D-0.11 — ماتریس Screenshot.** `shoot.ts`: صفحات auth با Context بدون Login گرفته شوند حتی
  وقتی حالت Login فعال است؛ `/design` اضافه شود.

**پذیرش D-0:** همه تست‌ها سبز + axe سبز + Screenshot صفحه رویدادها در fa تاریخ جلالی نشان دهد +
`/design` در هر دو تم بی‌نقص.

---

## بخش ۴ — Spec طراحی هر فاز (جفت فازهای 00)

### D-B — UI محیط‌ها، Secretها، Request Builder (جفت ادامه فاز B)

**صفحات:** `projects/{id}/environments` (Master-Detail: فهرست چپ/راست بسته به جهت، فرم در
Detail)، بخش Secretها داخل همان Detail، و `projects/{id}/request` (آزمایشگاه Request — بعداً
مبنای گام «Request» در Wizard و Node HTTP).

**تصمیم‌های بصری:**

- کارت Environment: نام + Badge نوع (`Local/Dev/QA/Staging/Production/Custom` — Production با
  `--warn` متمایز)، Base URL به‌صورت mono، سوییچ‌های خطرناک (Private Network،
  Invalid Certificate) با توضیح یک‌خطی زیرشان، نه Tooltip تنها.
- Secret: در فهرست فقط نام + Preview چهارنویسه (`••••abcd`) + «آخرین استفاده»؛ دکمه Reveal فقط
  با Capability `ViewSecret`، هر Reveal یک رویداد Audit؛ مقدار Reveal شده بعد از ۳۰ ثانیه یا
  Blur دوباره مخفی.
- Request Builder: سطر اول Method-chip رنگی (GET=سبز، POST=آبی، PUT=نارنجی، PATCH=بنفش،
  DELETE=قرمز — از Tokenهای موجود، در `/design` ثبت شود) + URL mono با Highlight زندهٔ
  `{{…}}` (سبز=قابل حل، قرمز=Unresolved با پیام همان `VariableResolver.TryResolve`)؛ زیرش
  Tabs: Query/Headers/Body/Auth (کامپوننت `tabs` موجود — اولین مصرف واقعی‌اش).
- جدول‌های Key/Value با ستون Enabled (سوییچ)، دقیقاً مدل `KeyValueEntry` موجود.
- Response Viewer (اولین Island واقعی): Segmented موجود برای Tree/Raw؛ Status pill + مدت +
  اندازه در Header؛ کلیک روی هر فیلد در Tree → منوی هفت‌گزینه‌ای Brief (Save as Variable /
  Use in Next Step / Add Assertion / Ignore in Snapshot / Mark as Dynamic / Use as Array Key /
  Copy Path)؛ گزینه‌هایی که هنوز پشتوانه ندارند **نمایش داده نشوند** — نه Disabled، غایب. رنگ
  JSON از Tokenهای `--json-*` موجود.
- حالت‌های خطای Executor (`HttpFailureKind`) هرکدام Empty State خودش با راه‌حل (Timeout →
  «در تنظیمات محیط بیشترش کن»…) — متن‌ها از همین الگوی موجود `HttpFailure.Message`.

**پذیرش بصری:** یک Request واقعی به FakeApi از داخل UI؛ Highlight متغیر زنده؛ Reveal گیت‌شده و
Audit شده؛ ماتریس Screenshot کامل.

### D-C — Diff و Baseline (جفت فاز C)

- درخت Diff با ۶ دسته از Tokenهای `--diff-*` موجود؛ هر سطر: مارکر + مسیر + قدیم→جدید؛
  Ignored با `--diff-ignored-ink` کم‌رنگ اما **حاضر** (حذفش اعتماد را می‌کشد).
- نوار خلاصه چسبان بالای Diff: «۳ Added · ۱ Removed · ۲ Changed» — هر کدام کلیک = پرش به اولین
  مورد؛ دکمه «تفاوت بعدی/قبلی» با کیبورد (`n`/`p`).
- Side-by-side فقط Desktop؛ Tablet/Mobile به Tree می‌افتد (تصمیم Responsive صریح).
- Virtual Scroll از روز اول؛ آزمون با Payload ≥ 2MB در همین فاز، نه L.
- Rule Builder سطری: JSON Path (mono) + Select نوع Matcher + پارامتر؛ پیشنهادهای Dynamic
  به‌صورت سطرهای از پیش پرشده با Checkbox — «اعمال بدون تأیید ممنوع» (Brief).
- چرخه Baseline: شش وضعیت با Badgeهای موجود (Draft=idle، Pending=warn، Approved=pass،
  Superseded=idle، Rejected=fail، Archived=idle-کم‌رنگ)؛ Timeline عمودی نسخه‌ها.
- Accept/Reject سطحِ فیلد: دکمه‌های کوچک روی Hover هر سطر Diff؛ جمع انتخاب‌ها در نوار پایینی
  («۲ تغییر پذیرفته، ۱ رد — ذخیره به‌عنوان نسخه ۴»).

### D-D — Capture Mode و Wizard (جفت فاز D)

- Review Queue: فهرست Sampleها با چیپ شش‌وضعیتی (Captured=running، Reviewed=accent،
  Approved=pass، Rejected=fail، Outdated=warn، Failed=fail-کم‌رنگ)؛ انتخاب چندتایی؛ صفحه‌کلید
  `a`=Approve، `r`=Reject، `j/k`=پیمایش.
- Wizard ۹ گامی: Progress Rail عمودی (Desktop) / نوار افقی فشرده (Mobile)؛ هر گام یک کار؛
  خروج در هر لحظه بدون از دست دادن — «پیش‌نویس ذخیره شد»؛ گام آخر لینک «باز کردن در Canvas».
- ویرایشگر Dataset: جدول قابل ویرایش Inline + Paste چندخطی که Preview تفکیک می‌دهد
  (CSV/JSON/خطی) قبل از Import.

### D-E — Canvas (جفت فاز E)

- **جهت جریان LTR می‌ماند حتی در UI فارسی** — قرارداد ذهنی Flowchart؛ فقط Palette/Inspector/متن
  Nodeها محلی‌سازی می‌شوند. این تصمیم در سند و تست Screenshot fa ثبت شود.
- آناتومی Node: آیکون گروه + عنوان (dir=auto) + خلاصه یک‌خطی خروجی + حلقه وضعیت ۸حالته
  (idle/running/pass/fail/skip/wait/retry/cancel از Tokenهای موجود) + پورت‌ها: ورودی دایره،
  خروجی موفق دایره پر، خروجی Failure لوزی `--fail` — ناسازگاری Type = هشدار زرد روی Edge حین
  Drag، قبل از Drop.
- Palette: گروه‌های Core/Data/Testing/Flow/Auth با جست‌وجو؛ Drag → Ghost preview روی Canvas.
- Inspector: Header (آیکون+نام+سوییچ Disable)، بدنه فرم Property از `NodeSpec` (همان الگوی
  فرم‌های موجود)، پایین: خطاهای Validation همان Node.
- MiniMap/Background/Controls فقط از `--canvas-*`؛ Snap-to-grid با نقطه‌های `--canvas-dot`.
- Undo/Redo با شمارنده در Toolbar؛ `Ctrl+Z/Y`، `Del`، `Ctrl+D`، `Ctrl+A`، `Space`+Drag=Pan.

### D-F — Run Console (جفت فاز F)

- Layout سه‌ناحیه: گراف بالا (وضعیت زنده روی خود Nodeها)، Log پایین (جمع‌شونده)، Timeline کنار.
- Log زنده: Virtual Scroll + Buffer با requestAnimationFrame (نه یک DOM Node در هر Event)؛
  Auto-follow که با Scroll دستی قطع و با دکمه «پایین» وصل می‌شود؛ فیلتر سطح/Node.
- Timeline: نوار افقی هر Node با طول=مدت؛ Retry = بخش‌های تکراری با شکاف؛ کلیک = پرش به Log.
- Cancel با Confirm ساده؛ «Run from this node» از منوی راست‌کلیک Node.

### D-G — Matrix چندمحیطی (جفت فاز G)

- جدول: سطر=سناریو/Sample، ستون=Environment؛ سلول = نقطه+کلمه + مدت؛ کلیک = جزئیات.
- مقایسه دو محیط: همان Diff Viewer فاز C با Headerهای «Staging | Production» — زبان بصری جدید
  اختراع نشود.

### D-H — Schedule و Flaky (جفت فاز H)

- فهرست Schedule: Cron خام + ترجمه انسانی زیرش («هر روز ۰۶:۰۰») + «اجرای بعدی» نسبی با `Dates`.
- Flaky: Badge `warn` «ناپایدار» با Tooltip نرخ شکست؛ Quarantine = ردیف کم‌رنگ + برچسب، حذف نه.

### D-I — Team و Approval (جفت فاز I)

- کارت نقش‌ها با متن‌های موجود `workspace.role_*_help`؛ تغییر نقش = Confirm با پیش‌نمایش
  «چه چیزهایی اضافه/کم می‌شود».
- صندوق Approval: فهرست Pendingها با Diff خلاصه Inline؛ Approve/Reject با Comment اختیاری.
- «فراموشی گذرواژه» اینجا ساخته می‌شود (SMTP این فاز می‌آید).

### D-J — Import/Export و Templates (جفت فاز J)

- گالری Template: کارت با پیش‌نمایش SVG کوچکِ گراف (تولیدشده از خود Definition، نه عکس ثابت).
- Import سه‌مرحله‌ای: منبع → پیش‌نمایش آنچه ساخته می‌شود (شمارش: «۴ سناریو، ۲ محیط») → تأیید.
- هشدار صریح Export: «Secretها هرگز در Export نیستند» — جمله در خود Dialog.

### D-K — Security-visible (جفت فاز K)

- چیپ «redacted» یکدست (mono، `--idle-soft`) هرجا Redactor عمل کرده — کاربر باید بفهمد چرا
  مقدار نیست و این Feature است نه Bug؛ Tooltip توضیح.
- Enrollment Agent: صفحه با کد یک‌بارمصرف بزرگ mono + Countdown؛ وضعیت Agent = نقطه+کلمه.

### D-L — Polish (جفت فاز L)

جمع‌بندی: Onboarding Checklist در داشبورد (۴ گام اول کاربر)، ناوبری کامل کیبورد در جدول‌ها،
جدول→کارت در موبایل برای Runs/Activity، تست Canvas ۲۰۰+ Node و Diff چند-MB، مرور کامل
axe/Contrast، هر بدهی تیک‌نخورده این سند.

### D-M — Golden Screenshots (جفت فاز M)

ماتریس `shoot.ts` به‌عنوان Baseline بصری در `docs/ui/golden/` (با Redaction دستی) + مقایسه
Pixel-diff در CI برای صفحات پایدار؛ مستندات کاربر از همین Screenshotها.

---

## بخش ۵ — Definition of Done بصری هر فاز

- [ ] هر صفحه جدید در ماتریس `shoot.ts` هست و ۱۲ Screenshot آن مرور شده
- [ ] هر متن جدید در هر دو کاتالوگ؛ تست ترجمه سبز
- [ ] هر کامپوننت جدید به `/design` اضافه شده
- [ ] axe سبز روی صفحات جدید (هر دو تم)
- [ ] هیچ Primitive جدیدی خارج `tokens.css`؛ هیچ `left/right` بی‌دلیل
- [ ] Empty/Error/Loading هر سه طراحی شده — نه فقط Happy Path
- [ ] progress.md و بخش «وضعیت اجرا» زیر به‌روز شده

---

## وضعیت اجرا (جلسات بعدی تیک بزنند)

- [x] **D-0 — انجام شد** (۱۱ آیتم؛ جزئیات و یافته‌های حین اجرا در [progress.md](../progress.md))
- [x] **D-B — انجام شد** (محیط‌ها، Secretها، Request Builder، Response Viewer)
- [ ] D-C
- [ ] D-D
- [ ] D-E
- [ ] D-F
- [ ] D-G
- [ ] D-H
- [ ] D-I
- [ ] D-J
- [ ] D-K
- [ ] D-L
- [ ] D-M

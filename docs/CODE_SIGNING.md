# توقيع CleanSuite رقميًا (Authenticode) عبر SignPath Foundation

## لماذا التوقيع؟

البرنامج والمُثبِّت حتى الإصدار 1.1.0 غير موقّعين. نتيجة ذلك:

- **التحكم الذكي في التطبيقات (Smart App Control)** في Windows 11 يمنع تشغيل الملف تمامًا، ويظهر الخطأ
  «An Application Control policy has blocked this file» (4551). لا يستطيع المستخدم تجاوزه إلا بإيقاف الميزة، وإيقافها
  لا رجعة فيه إلا بإعادة تثبيت Windows.
- **SmartScreen** يعرض «Windows protected your PC» مع «ناشر غير معروف».

التوقيع بشهادة موثوقة يحل الأولى، ويخفف الثانية مع الوقت.

[SignPath Foundation](https://signpath.org) تقدّم للمشاريع مفتوحة المصدر شهادة توقيع وخدمة توقيع مجانًا. الشهادة باسم
المؤسسة، فيظهر الناشر «SignPath Foundation». الشهادات الأخرى إما مدفوعة (من نحو 200 إلى 400 دولار سنويًا مع مفتاح على
جهاز أمني)، أو مثل Azure Trusted Signing (نحو 10 دولارات شهريًا) الذي يشترط شركة أو تحققًا من الهوية قد لا يكون متاحًا
في كل البلدان.

## ما الجاهز في المستودع

| الملف | الدور |
| --- | --- |
| `.github/workflows/build.yml`، مهمة `release` | عند وسم `v*`: إذا وُجد السر `SIGNPATH_API_TOKEN` يوقّع البرنامجين وبرنامج إزالة التثبيت، ثم يبني المُثبِّت ويوقّعه، ثم يتحقق من التوقيعات قبل إنشاء مسودة الإصدار. إذا لم يوجد السر يبني نسخة غير موقّعة كما كان، مع تحذير في سجل CI. |
| `build.ps1` | خيارات البناء على مراحل: `-SkipInstaller` و`-PrepareUninstaller` و`-SkipPublish` و`-SignedUninstallerDir`. |
| `installer/CleanSuite.iss` | وضع `SignedUninstaller` (برنامج إزالة التثبيت يوقَّع خارج Inno Setup). |
| `Directory.Build.props` | اسم المنتج `CleanSuite` ورقم إصدار نظيف (`1.1.0` بلا رقم commit) في كل ملف؛ SignPath يتحقق منهما. |
| `.signpath/artifact-configurations/programs.xml` و`installer.xml` | إعداد الملفات التي يوقّعها SignPath. تُلصق في موقعه (الخطوة 5). |
| `README.md`، قسم «Code signing policy» | الصفحة التي تشترطها SignPath Foundation. |

جُرِّب المسار كاملًا محليًا بشهادة تجريبية: البناء، ثم توقيع البرنامجين وبرنامج الإزالة، ثم بناء المُثبِّت، ثم توقيعه.
الملفات الأربعة حملت التوقيع، والبرنامج الموقّع يعمل. الجزء الذي لم يُجرَّب هو الاتصال الفعلي بـ SignPath، لأنه
يحتاج حسابك.

## شروط SignPath Foundation (مختصرة)

- رخصة مفتوحة المصدر معتمدة من OSI. ✅ MIT.
- المشروع منشور ومستعمل وله إصدار سابق. ✅ 1.0.0 و1.1.0.
- لا برمجيات خبيثة ولا أدوات اختراق. ✅
- التوقيع فقط لما يبنيه CI من المستودع، وبعد موافقة يدوية على كل إصدار. ✅ مهيأ.
- اسم المنتج ورقم الإصدار في كل ملف موقّع. ✅ مهيأ.
- **المصادقة الثنائية (MFA)** على حساب GitHub وحساب SignPath. ⬜ عليك.
- **صفحة سياسة التوقيع** في الصفحة الرئيسية للمشروع: فيها عبارة «Free code signing provided by SignPath.io, certificate by
  SignPath Foundation»، والأدوار، وبيان الخصوصية. ✅ في README.

## ما عليك فعله

### 1) فعّل المصادقة الثنائية على GitHub

GitHub ← Settings ← Password and authentication ← Two-factor authentication.

### 2) قدّم الطلب

من [signpath.org](https://signpath.org) اختر **Apply** (أو صفحة شروط المؤسسة
[signpath.org/terms](https://signpath.org/terms)) واملأ النموذج:

- **Project name:** CleanSuite
- **Repository:** `https://github.com/abosalehg-ui/CleanSuite`
- **Homepage / code signing policy:** `https://github.com/abosalehg-ui/CleanSuite#code-signing-policy`
- **License:** MIT
- **Build system:** GitHub Actions (github.com)
- **Description:** مثلًا «Free, open-source Windows 10/11 maintenance tool (junk files, registry, duplicates, disk analysis)
  in Arabic and English. Built by GitHub Actions; installer made with Inno Setup.»
- **Files to sign:** `CleanSuite.App.exe`, `CleanSuite.Cli.exe`, the Inno Setup uninstaller, `CleanSuite-Setup-<version>.exe`

المراجعة قد تستغرق من أيام إلى أسابيع. إن سألوا عن شيء، فكل الإجابات في هذا الملف وفي README.

### 3) بعد الموافقة: الدخول إلى SignPath

ستصلك دعوة إلى [app.signpath.io](https://app.signpath.io). فعّل MFA في حسابك هناك. عادةً تُنشئ المؤسسة لك
**Organization** و**Project**، ومعهما سياستا توقيع: `test-signing` و`release-signing`. دوّن هذه القيم:

- **Organization ID**: في Settings الخاصة بالمؤسسة، أو في رابط الصفحة. صيغته مثل `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`.
- **Project slug**: مثل `CleanSuite`.
- **Signing policy slug**: `release-signing`.

### 4) اربط GitHub

1. ثبّت تطبيق [SignPath على GitHub](https://github.com/apps/signpath) على المستودع `abosalehg-ui/CleanSuite`.
2. في SignPath: Project ← **Trusted build systems** ← أضف **GitHub.com** إن لم يكن مضافًا.

بهذا يتحقق SignPath أن كل طلب توقيع جاء فعلًا من مهمة CI في هذا المستودع.

### 5) أنشئ إعدادي الملفات (Artifact configurations)

في SignPath: Project ← **Artifact configurations** ← Add. أنشئ اثنين بهذين الـ slug تمامًا، فسير العمل يستدعيهما بالاسم:

| Slug | المحتوى |
| --- | --- |
| `programs` | الصق محتوى `.signpath/artifact-configurations/programs.xml` |
| `installer` | الصق محتوى `.signpath/artifact-configurations/installer.xml` |

لا يهم أيهما الافتراضي.

### 6) أنشئ مستخدم CI ورمز API

1. SignPath ← Users ← **Add CI user** (مثلًا `github-actions`)، ثم أنشئ له **API token** وانسخه.
2. في سياسة `release-signing`: أضف هذا المستخدم في **Submitters**، وأضف نفسك في **Approvers**.

### 7) أضف السر والمتغيرات في GitHub

المستودع ← Settings ← Secrets and variables ← Actions:

| النوع | الاسم | القيمة |
| --- | --- | --- |
| **Secret** | `SIGNPATH_API_TOKEN` | رمز API من الخطوة 6 |
| Variable | `SIGNPATH_ORGANIZATION_ID` | Organization ID |
| Variable | `SIGNPATH_PROJECT_SLUG` | Project slug |
| Variable | `SIGNPATH_SIGNING_POLICY_SLUG` | `release-signing` (أو `test-signing` للتجربة، انظر أدناه) |

### 8) عدّل عبارة «applied for» في README

بعد الموافقة احذف من قسم Code signing policy عبارة «(applied for; releases are signed once the application is approved)»،
واحذف من القسم العربي «(الطلب مقدَّم)». وعدّل فقرة Install وسطر «التثبيت» حتى يذكرا أن الإصدارات من الإصدار التالي موقّعة.

## إصدار موقّع: ماذا يحدث

1. حدّث الإصدار في `src/CleanSuite.App/CleanSuite.App.csproj`، وأضف قسمه في `CHANGELOG.md`، ثم ادفع الوسم (مثلًا `v1.1.1`).
2. مهمة `release` تبني البرنامجين وبرنامج الإزالة، وترسل **طلب توقيع أول**. يصلك بريد من SignPath:
   افتح الطلب واضغط **Approve**.
3. يُبنى المُثبِّت من الملفات الموقّعة، ويُرسل **طلب توقيع ثانٍ**. وافق عليه أيضًا.
4. تتحقق المهمة أن كل الملفات موقّعة توقيعًا صالحًا، ثم تنشئ **مسودة إصدار** فيها المُثبِّت وملف SHA-256.
5. انشر المسودة من صفحة Releases.

المهمة تنتظر كل موافقة ساعتين بحد أقصى. إن فاتتك المهلة فشلت المهمة، فأعد تشغيلها (Re-run failed jobs)، وسيُرسل طلب
جديد.

**تجربة أولى (اختيارية):**

1. ضع `SIGNPATH_SIGNING_POLICY_SLUG` = `test-signing`، وادفع وسم الإصدار التالي (مثلًا `v1.1.1`).
2. التوقيع هنا بشهادة اختبار غير موثوقة، فستفشل خطوة «Check signatures»، وهذا متوقع. الغاية التأكد أن الاتصال
   والإعدادات تعمل حتى الخطوة الأخيرة.
3. احذف الوسم من GitHub ومن جهازك:

   ```bash
   git push --delete origin v1.1.1
   ```

   ```bash
   git tag -d v1.1.1
   ```

4. أعد المتغير إلى `release-signing`، ثم أنشئ الوسم وادفعه من جديد.

الوسم يجب أن يكون رقمًا فقط، مثل `v1.2.3`: `build.ps1` يرفض لاحقة مثل `-test`.

## ما تتوقعه بعد التوقيع

- **Smart App Control:** يقبل عادةً الملفات الموقّعة بشهادة من جهة موثوقة. الإصدار 1.1.0 غير الموقّع سيبقى ممنوعًا على
  تلك الأجهزة، فالحل للمستخدمين هو الإصدار الموقّع التالي.
- **SmartScreen:** يعتمد على «سمعة» الشهادة والملف. قد يظهر التحذير في الأيام الأولى بعد كل إصدار حتى تتراكم التنزيلات.
  اسم الناشر سيظهر «SignPath Foundation» بدل «ناشر غير معروف».
- **Get-AuthenticodeSignature** على أي ملف موقّع يعرض `Valid`.

## حل المشكلات

| المشكلة | السبب والحل |
| --- | --- |
| يُرفض الطلب بسبب `product-name` أو `product-version` | الملفات التي يكتبها Inno Setup (المُثبِّت وبرنامج الإزالة) تحفظ اسم المنتج والإصدار بمسافات زائدة في نهايتهما (`'CleanSuite      '`). إن لم يتجاهلها SignPath، احذف السمتين `product-name` و`product-version` من عنصر المُثبِّت في `installer.xml`. أما لبرنامج الإزالة في `programs.xml`، فانقل `uninst.exe` إلى عنصر `<pe-file path="uninst.exe">` مستقل بلا السمتين، أو اسأل دعم SignPath. البرنامجان نفساهما يحملان القيم دون مسافات. |
| `Resource not accessible by integration` أو تعذّر تنزيل الملف | تطبيق SignPath غير مثبت على المستودع (الخطوة 4)، أو صلاحية `actions: read` محذوفة من مهمة `release`. |
| `Trusted build system` أو origin غير صالح | GitHub.com غير مضاف في الخطوة 4، أو التشغيل ليس من GitHub Actions المستضاف. |
| فشل «Check signatures» مع `release-signing` | انظر أي ملف ليس `Valid` في سجل الخطوة. إن كان برنامج الإزالة، فتأكد أن `programs.xml` الملصق في SignPath يتضمن `uninst.exe`. |
| CI أنشأ إصدارًا غير موقّع | السر `SIGNPATH_API_TOKEN` غير موجود أو اسمه مختلف. السجل يعرض تحذيرًا بذلك. |

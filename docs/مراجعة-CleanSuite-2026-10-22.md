# مِحَك — مراجعة مستودع CleanSuite (الإصدار 1.0.0)

> تاريخ المراجعة: 2026-10-22 · الفرع: `main` (`d90c8a1` "CleanSuite 1.0.0") · نوع المشروع: **تطبيق سطح مكتب (Desktop App)** — ليس لعبة.
> نطاق المراجعة: قُرئت كل ملفات `src/` (16 مشروعًا، 134 ملف C#/XAML) وكل ملفات `tests/`، وشُغّل `dotnet build -c Release` (0 تحذيرات) و`dotnet test` (**241/241 ناجحة**، تغطية 77.9% للمحركات) و`dotnet list package --vulnerable` (لا حزم معرّضة).
> ملاحظة: هذه المراجعة الثانية للمشروع. المراجعة الأولى (`docs/مراجعة-CleanSuite.md`، 2026-10-21) كانت على فرع `feature/erase-history` وخرجت بـ 28 بندًا. أغلبها **أُصلح فعلًا** (معالج الاستثناءات العام، `ScanCleanViewModel`, `PathProbe` الموحّد، فحص O(n²) في النسخة الاحتياطية، CI، الرخصة). ما بقي منها مذكور هنا بوضوح.

---

## 1. نظرة عامة سريعة

- **وش يسوي:** أداة صيانة لويندوز 10/11 بالعربية والإنجليزية تعيد بناء وظائف Ace Utilities من الصفر: الملفات المهملة، ومسح آثار الاستخدام (بما فيها Firefox)، والملفات المكررة، والاختصارات المعطوبة، وتنظيف السجل (Registry) في 16 فئة، وبرامج بدء التشغيل، وإلغاء التثبيت، وتحليل القرص، والاستعادة، والجدولة عبر Task Scheduler، وتحليل ضاغط السجل (قراءة فقط).
- **البنية:** حل (Solution) فيه 16 مشروعًا: مكتبتان أساسيتان (`Safety`: السجل اليومي Journal والمسارات المحمية وكاتب/مستورد `.reg` ودفتر النسخ الاحتياطية SHA-256؛ `Matching`: مطابق الأنماط)، ومكتبة لكل محرك (`Junk`, `Duplicates`, `History`, `RegistryClean`, `Shortcuts`, `Startup`, `Uninstall`, `DiskUsage`, `Restore`, `Scheduler`, `RegistryCompact`, `Compat`)، وواجهة WPF (`CleanSuite.App`)، وواجهة سطر أوامر للقراءة فقط (`CleanSuite.Cli`)، ومشروع اختبارات xUnit.
- **التقنيات:** .NET 8 (`net8.0-windows10.0.17763.0`)، WPF، C# 12 مع `Nullable` و`TreatWarningsAsErrors` و`Deterministic`، `Microsoft.Data.Sqlite 8.0.10` + `SQLitePCLRaw.bundle_e_sqlite3 2.1.14` (مثبّتة عمدًا لتجاوز GHSA-2m69-gcr7-jv3q)، `System.IO.Hashing 8.0.0`، xUnit 2.9.3، Inno Setup 6.4 للمُثبِّت، GitHub Actions للـ CI.
- **الحجم:** 184 ملفًا متتبعًا في git. قرابة **19,200 سطر** في `src/` (C# + XAML) و**3,800 سطر** اختبارات. أكبر الملفات: `Localization/Strings.cs` (1,435)، `Themes/Controls.xaml` (859)، `JunkRemover.cs` (624)، `MainWindow.xaml.cs` (514).

**الانطباع العام:** مشروع ناضج ومنضبط بدرجة غير معتادة في هذا النوع من الأدوات. فلسفة "لا نسخة احتياطية = لا تعديل" مطبّقة فعلًا في كل مسار حذف، والواجهة العربية مبنية على فهم حقيقي لمشاكل WPF مع RTL. ما بقي من مشاكل يتركز في: (1) ثغرة TOCTOU كلاسيكية لأدوات التنظيف عبر الوصلات (junctions)، (2) جذور مسح تؤخذ من إعدادات قابلة للكتابة بلا تحقق، (3) تكرار هندسي واضح في "خط أنابيب الحذف"، (4) نصف الواجهة فقط انتقل إلى MVVM.

---

## 2. المراجعة التقنية والهندسية (45%)

### 2.1 البنية المعمارية وفصل المسؤوليات — **7/10**

**الإيجابي:** الطبقات واضحة وغير دائرية: `Safety` و`Matching` في القاع، المحركات فوقها، `Scheduler` و`App` و`Cli` في القمة. كل اعتمادية خارجية خلف واجهة قابلة للحقن: `IRecycleBin`, `IRegistryRoots`, `ISystemEnvironment`, `ITaskStore`, `IProcessLauncher`، ولهذا الاختبارات معزولة تمامًا (خلية سجل خاصة بـ `RegLoadAppKey`، سلة محذوفات وهمية). منطق المحركات **لا يتسرب** إلى الـ code-behind: `DiskView.xaml.cs` رغم طوله (451 سطرًا) كله غراء واجهة يستدعي `DiskScanner.Recycle/Forget/DeleteBlocked`.

**السلبي 1 — `Junk` صار مكتبة أساسية ثانية:** `IRecycleBin`, `ShellRecycleBin`, `RemovalMode`, `JunkBackupArchive`, `ZipComment` كلها تعيش في `src/CleanSuite.Junk/JunkRemover.cs`، فاضطرت `Duplicates` و`Shortcuts` و`DiskUsage` و`Restore` و`Compat` إلى الاعتماد على *ميزة* الملفات المهملة فقط لتصل إلى سلة المحذوفات أو تقرأ نسخة zip. و`DuplicateRemover` يلفّ `JunkRemover` ويصنع `JunkItem` وهمية:

> `src/CleanSuite.Duplicates/DuplicateRemover.cs` — في `Remove()`:
> ```csharp
> items.Add(new JunkItem(f.FullPath, f.Name, f.Folder, f.Size, JunkReason.None, "Duplicate", g.Number) { LastWriteUtc = f.LastWriteUtc });
> ```

**السلبي 2 — MVVM نصف مكتمل:** `ViewModels/ScanCleanViewModel<TRow>` تصميم ممتاز (علم انشغال واحد، `AsyncRelayCommand` يمنع التشغيل المزدوج، حدث `Error`، `Confirm`، `IsChanging`) ومختبَر، لكنه مستخدم في History وRegistry وShortcuts فقط. الملفات المهملة (`MainWindow`)، والمكررات، والقرص، وبدء التشغيل، وإلغاء التثبيت، والاستعادة كل واحد يعيد كتابة آلة الحالة نفسها يدويًا، فاضطر `MainWindow` إلى تعداد كل التبويبات:

> `src/CleanSuite.App/MainWindow.xaml.cs` — في `Window_Closing()`:
> ```csharp
> bool changing = _cleaning || RegistryTab.IsChanging || HistoryTab.IsChanging || DuplicatesTab.IsChanging || DiskTab.IsChanging
>                 || ShortcutsTab.IsChanging || StartupTab.IsChanging || RestoreTab.IsChanging;
> ```
> ونفس الشيء في `Window_KeyDown()` (سلسلة ternary من 10 فروع لربط التبويب بزر الفحص/الإلغاء).

**الحل:** نقل `IRecycleBin`/`ShellRecycleBin`/`RemovalMode`/`JunkBackupArchive` إلى `CleanSuite.Safety`؛ وواجهة `IToolPage { bool IsBusy; bool IsChanging; ICommand Scan; ICommand Cancel; }` تطبّقها كل الصفحات فيتكرر عليها `MainWindow` بدل تعدادها؛ ونقل الصفحات الست المتبقية إلى `ScanCleanViewModel`.

### 2.2 قابلية القراءة والصيانة — **7/10**

الكود كثيف لكنه مقصود، والتسمية واضحة، والتعليقات تشرح "ليش" (214 تعليقًا تربط السلوك بالبرنامج الأصلي وتبرر الانحرافات عنه: `// Invariant culture: with e.g. ar-SA the current calendar is Hijri…`). هذا نافع جدًا لإعادة بناء.

**الدوال الطويلة:**
- `src/CleanSuite.History/WindowsHistoryItems.cs` — `WindowsHistoryCleaner.Plan()`: ~130 سطرًا، `switch (id)` بـ 25 حالة رقمية (`case 16:`, `case 12:`, `case 34:`…) كل حالة حزمة قواعد سجل/ملفات بأرقام سحرية. البديل: جدول `Dictionary<int, Action<…>>` أو صنف لكل عنصر.
- `src/CleanSuite.History/FirefoxCleaner.cs` — `PlanProfile()`: 93 سطرًا، 10 كتل `if ((items & FirefoxItems.X) != 0)` متتالية.
- `src/CleanSuite.RegistryClean/Scanners.Classes.cs` — `ScanAssociationRoot()`: 77 سطرًا بتداخل 6 مستويات.
- `src/CleanSuite.Junk/JunkRemover.cs` — `Remove()`: ~80 سطرًا، ثلاث استراتيجيات (سلة/دائم/zip) داخل `switch` واحد.

**ضجيج:** 20 علامة `// (review fix N)` في `Strings.cs` وأسماء اختبارات توثّق مراجعة سابقة لا الكود. و`Strings.cs` ملف C# واحد من 1,435 سطرًا بقاموسين `En` ثم `Ar` بدل `.resx` — يصعّب على مترجم غير مبرمج.

### 2.3 التكرار (DRY) و Code Smells — **5/10**

الأوليات المشتركة موجودة في `Safety` ومستخدمة في كل مكان (`OperationJournal`, `BackupNames.Timestamp`, `RegBackupWriter`, `BackupLedger`, `FileIdentity`, `ProtectedPaths`) — تحسّن حقيقي عن المراجعة الأولى. لكن **خط الأنابيب** حولها منسوخ:

- **"تحقق → ارفض → أرسل للسلة → تأكد أنه اختفى → سجّل"** مكتوب ثلاث مرات بصيغ مختلفة:
  > `src/CleanSuite.Junk/JunkRemover.cs` — في `Remove()`:
  > ```csharp
  > bool ok = !notRecycled.Contains(it.FullPath) && !File.Exists(it.FullPath);
  > journal.Record("recycle", it.FullPath, ok, backupRef: ok ? "RecycleBin" : null, error: ok ? null : "not recycled");
  > ```
  > `src/CleanSuite.Shortcuts/ShortcutCleaner.cs` — في `Remove()`:
  > ```csharp
  > _bin.Recycle(batch.Select(b => b.FullPath).ToList(), silent);
  > int ok = 0;
  > foreach (var it in batch)
  > {
  >     bool gone = !File.Exists(it.FullPath) && !Directory.Exists(it.FullPath);
  > ```
  > لاحظ أن النسخة الثانية **تتجاهل القائمة التي يرجعها `Recycle`** كليًا. و`DiskScanner.Recycle` نسخة ثالثة.
- **"تخطَّ ما تغيّر بعد الفحص"** بأربع لهجات ونصوص سبب مختلفة ("changed since scan" / "changed since the scan") في `JunkRemover`, `ShortcutCleaner.CheckShortcut`, `HistoryActions`, `FileFilterAction`، فاضطر `Loc.Reason` إلى ترجمة عدة تهجئات.
- **مجلد النسخ الاحتياطية الافتراضي** `Path.Combine(…LocalApplicationData, "CleanSuite", "Backups")` مكتوب حرفيًا في 6 ملفات (`JunkRemover`, `RegistryAccess`, `ShortcutCleaner`, `StartupManager`, `UninstallManager`, `RegistryView.xaml.cs`)، و`LocalApplicationData` يظهر 17 مرة.
- **ستة مشّايات مجلدات** يدوية (`JunkScanner.ScanFolder`, `DuplicateScanner.Walk`, `DiskScanner.Scan`, `EmptyFolderFinder`, `ShortcutScanner`, `RulePlanner`) كل واحدة تعيد تجاهل نقاط إعادة التحليل (19 فحص `ReparsePoint`) وحارس MAX_PATH **بشكل غير متسق**: `DuplicateScanner` يحرس عند `>= 261`، `DiskScanner` عند `>= 260`، و`JunkScanner` بلا حارس ويتكرر على مكدس الاستدعاء.
- **خمسة سجلات نتائج** متطابقة تقريبًا بأسماء أعضاء مختلفة: `JunkRemovalResult.Deleted` / `ShortcutRunResult.Succeeded` / `RegCleanResult.Removed`.
- **8 أصناف صفوف** تكتب `INotifyPropertyChanged` يدويًا رغم وجود `ViewModelBase.Set<T>`.
- أرقام سحرية غير معلّقة: `AccessProbe.Probe` يمرر `0xC0010000, 0, …, 3, 0` و`DeviceIoControl(h, 0x2D1080, …)`.

**الحل:** مساعد `RecycleStep` في `Safety` يُستخدم من المواضع الثلاثة؛ و`FileIdentity.StillSameAs(size, lastWrite)` بنص سبب واحد؛ وصنف `CleanSuitePaths` للمجلدات؛ ومشّاية واحدة `SafeWalk(root, onDir, ct)` بحارس طول وتجاهل reparse وإلغاء.

### 2.4 معالجة الأخطاء والحالات الحدّية — **7/10**

**الإيجابي:** 186 موضع `catch` في `src/`، منها **2 فقط عاريتان** (`DiskGrouping.GroupOf` و`SystemEnvironment.GetTempPath`) وكلاهما مساعد "أفضل جهد". لا `catch (Exception)` بلا مرشّح في المحركات إلا في `ScheduledRunner.Run` (مبرر وموثق: تشغيل بلا مراقب يبلّغ ويكمل) و`ScanCleanViewModel.Run` (يمرر إلى حدث `Error`). كل الـ 19 `catch (Exception ex)` في الواجهة تذهب إلى `UiErrors.Show/Log`، و`App.OnStartup` يربط `DispatcherUnhandledException` و`UnobservedTaskException` و`UnhandledException` — المشكلة الحرجة في المراجعة الأولى (انهيار بلا رسالة) **أُصلحت**. واجهة سطر الأوامر صارت تلتقط `FormatException`/`OverflowException`.

**السلبي:**
- > `src/CleanSuite.Safety/BackupLedger.cs` — في `Record()`:
  > ```csharp
  > catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
  > ```
  > فشل تسجيل النسخة في الدفتر يُبتلع، فتظهر لاحقًا نسخة كتبها CleanSuite بنفسه بحالة `BackupTrust.Unknown` وتُرفض استعادتها إلى Program Files بلا أي أثر يفسر السبب.
- > `src/CleanSuite.Junk/JunkRemover.cs` — في `Remove()`:
  > ```csharp
  > if (!fi.Exists) { failed.Add((item.FullPath, "no longer exists")); continue; }
  > if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) { failed.Add((item.FullPath, "reparse point")); continue; }
  > ```
  > هذان الرفضان **لا يُسجَّلان في الـ journal** بخلاف "changed since scan" و"protected" بعدهما بسطرين.
- **الإلغاء غير مكتمل:** `ShortcutCleaner.Remove`, `StartupManager.Scan`, `UninstallScanner.Scan`, `EmptyFolderFinder.Find` لا تقبل `CancellationToken`؛ و`ShortcutsViewModel` يعلن `CanCancel => false` ويستدعي `s.Scan()` رغم أن `ShortcutScanner.Scan(CancellationToken ct = default)` يقبل الرمز (و`ScheduledRunner` يمرره فعلًا).
- > `src/CleanSuite.App/MainWindow.xaml.cs` — في `Scan_Click()`:
  > ```csharp
  > return (new JunkScanner().Scan(req, _cts.Token), tempFolders);
  > ```
  > قراءة الحقل `_cts` داخل lambda على خيط آخر؛ `DiskView` يعمل الصح (`var ct = _cts.Token` قبل `Task.Run`).

### 2.5 الأداء واستهلاك الموارد — **7/10**

**الإيجابي:** المكررات بإستراتيجية جيدة: مفتاح الحجم/الاسم أولًا، ثم `FileIdentity` لإسقاط الروابط الصلبة، ثم `PreFilter` يجزّئ أول وآخر 64 KiB فقط (XxHash64) للملفات فوق 128 KiB، ثم التجزئة الكاملة بكتل 1 MiB عبر `ArrayPool`. الملفات المهملة بـ `Parallel.ForEach` عامل لكل قرص فيزيائي. القرص بمشّاية تكرارية (`Stack`). كل مسح/تنظيف على `Task.Run` والتقدم عبر `Dispatcher.BeginInvoke`. مشكلة O(n²) في التحقق من zip (المراجعة الأولى) **أُصلحت** بقاموس.

**السلبي:**
- > `src/CleanSuite.App/DiskView.xaml.cs` — في `RenderAll()` و`Category_SelectionChanged()`:
  > ```csharp
  > var top = DiskScanner.Largest(f);
  > ExtensionGrid.ItemsSource = DiskScanner.Extensions(_current, c.Category)
  > ```
  > كلاهما يمشي `scope.AllFiles()` (الشجرة الفرعية كاملة) **متزامنًا على خيط الواجهة** عند كل اختيار عقدة. اختيار `C:\` على قرص بمليون ملف = تجمّد الواجهة طوال المشي. الحل: حساب الأكبر 100 والامتدادات مرة واحدة عند المسح وتخزينها في `DiskFolder`، أو على `Task.Run`.
- > `src/CleanSuite.App/MainWindow.xaml.cs` — في `UpdateTitle()`:
  > ```csharp
  > var sel = _rows.Where(r => r.IsChecked).ToList();
  > Title = Loc.F("AppTitleSelection", Loc.N0(sel.Count), Loc.N0(_rows.Count), Loc.FormatSize(sel.Sum(r => r.Item.Size)));
  > ```
  > يُستدعى من `UpdateSelectionStatus` عند كل تغيير `IsChecked`، و`CheckAll_Click` يغيّر كل صف في حلقة → O(n²) على 50 ألف صف (بقي من المراجعة الأولى). الحل: عدّاد ومجموع يُحدَّثان بـ O(1) عند كل تغيير، أو تعليق الإشعارات أثناء "تحديد الكل".
- `DuplicateRemover` يعيد قراءة الملفين بايتًا بايتًا (`FileIdentity.SameContent`) لكل مرشح قبل الحذف — آمن لكنه O(حجم المجموعة²) قراءات في المجموعات الكبيرة. مقبول كثمن للأمان، لكن يستحق توثيقه في الواجهة ("يتحقق من المحتوى…").
- التجزئة في المكررات بخيط واحد (لا توازٍ عبر الأقراص).

### 2.6 الاختبارات — **8/10**

17 ملفًا، 164 `[Fact]` + 15 `[Theory]` (65 `[InlineData]`) = **241 حالة، كلها ناجحة في ~2 ثانية**. العزل حقيقي: مجلدات مؤقتة بـ GUID، `IRecycleBin` وهمي، `AppHiveRegistryRoots` (خلية سجل خاصة)، `MemoryStore : ITaskStore` للجدولة، و**لا وصول إلى `Registry.CurrentUser/LocalMachine` في أي اختبار**. اختبارات للترجمة (تطابق 663 = 663 مفتاحًا مع تطابق العناصر النائبة) والثقافة (ar-SA والتقويم الهجري). CI يشغّلها على Windows مع تغطية وفحص حزم معرّضة.

**تغطية الأسطر لكل مشروع (من التشغيل الفعلي):** Duplicates 96%، Safety 95%، Startup 93%، DiskUsage 90%، Junk 90%، Compat 89%، Uninstall 87%، RegistryClean 84%، Restore 84%، RegistryCompact 75%، Shortcuts 69%، Scheduler 68%، **History 48%** (كتالوج عناصر ويندوز ومسارات Firefox/SQLite هي السطح الكبير غير المختبر).

**تحفّظ على رقم 77.7%:** `CleanSuite.App` (6,048 سطرًا منها 3,149 في `*.xaml.cs`) **غائب من تقرير coverlet**، فالرقم للمحركات فقط. لا اختبار لإطلاق برامج إلغاء التثبيت الحقيقية ولا لـ `ShellRecycleBin` (بالتصميم). و`ReviewFixTests.cs` (655 سطرًا، 28 اختبارًا) منظّم برقم بند المراجعة لا بالموضوع — يُدمج في ملفات الميزات.

### 2.7 التوثيق — **7/10**

`README.md` توثيق مستخدم حقيقي ثنائي اللغة (الميزات، نموذج الأمان، التثبيت، البناء). `docs/DEVELOPMENT.md` ممتاز: كل خيار CLI، أعلام المطوّر (`--snapshot`, `--auto-scan`)، جدول المشاريع، رموز الخروج (كانت ناقصة في المراجعة الأولى وأُضيفت). `NEXT_STEPS.md` سجل حالة وقرارات بالعربية. `LICENSE` موجود (MIT).

**الناقص:** لا `CHANGELOG` (تاريخ git عبارة عن commit واحد مضغوط "CleanSuite 1.0.0")، لا `CONTRIBUTING`، ولا وثيقة معمارية تشرح نموذج الأمان (journal/ledger/backup/skip-if-changed) — موجود فقط في تعليقات XML. و`docs/specs/` و`docs/Phase*.md` **متجاهَلة في git عمدًا** (تحليل البرنامج الأصلي)، فالمستودع المنشور بلا مبررات تصميم. ملاحظة إيجابية: صفر مراجع لأسماء دوال decompiler (`FUN_…`) في الكود المنشور.

### 2.8 اتفاقيات التسمية ونمط موحّد — **7/10**

C# متسق: `sealed` في كل مكان، file-scoped namespaces، `required init` records، `_field`. النظافة ممتازة: صفر ملفات `bin/obj` أو `.exe` متتبعة، `.gitignore` يغطي `artifacts/` و`TestResults/`.

**غير المتسق:** أسماء الأدوار: `Scanner/Remover` (Junk, Duplicates) مقابل `Scanner/Cleaner` (Registry, Shortcuts) مقابل `Manager` (Startup, Uninstall) مقابل `Executor` (History) مقابل `DiskScanner` ثابت يمسح **ويحذف**. أعداد النتائج `Deleted`/`Succeeded`/`Removed`. أصناف `Ace*Settings` موزعة على أربعة مشاريع. `JunkRemover.cs` يحوي أربعة أصناف عامة (`JunkRemover`, `JunkBackupArchive`, `ZipComment`, `ShellRecycleBin`) بينما `RegistryScanner` مقسّم إلى ستة ملفات جزئية.

**متوسط القسم:** (7 + 7 + 5 + 7 + 7 + 8 + 7 + 7) ÷ 8 = **6.9/10**

---

## 3. المظهر والتصميم وتجربة المستخدم (30%)

> ما يُحكم عليه هنا مستنتج من الكود (XAML، السمات، الترجمة). الرسم الفعلي على الشاشة ودقة DPI وسلوك قارئ الشاشة لم تُختبر على جهاز.

### 3.1 الاتساق البصري — **9/10**

- **صفر ألوان مكتوبة حرفيًا** في ملفات العرض الـ 14؛ كل فرشاة `{DynamicResource …}` من `Themes/Light.xaml` و`Dark.xaml` (36 مفتاحًا متطابقًا في كل منهما)، و`ThemeManager.HighContrastPalette` يشتق سمة التباين العالي من المفاتيح نفسها فلا يضيع مفتاح.
- خط واحد (`UiFont: Segoe UI, Tahoma`) وحجم أساس 13 يرتفع إلى 14 للعربية عبر `DataTrigger` على `IsRtl` في `Controls.xaml`.
- `FontSize` حرفي 5 مرات فقط (عنوان التطبيق، الصفحة الرئيسية، "حول"). **لكن `Margin` حرفي 151 مرة** (`0,0,0,10` ×17، `0,10,0,0` ×14) — المسافات متسقة بالعُرف لا بمورد.
- التسرب الوحيد: > `src/CleanSuite.App/DiskView.xaml.cs` — في `NodeHeader()`:
  > ```csharp
  > panel.Children.Add(new TextBlock { Text = size, Margin = new Thickness(10, 0, 10, 0), Opacity = 0.7 });
  > ```
  > يقلّد النص الباهت بالشفافية بدل مفتاح `TextMutedFg`.

### 3.2 سهولة الاستخدام والتنقل — **8/10**

- المهمة الأساسية: الرئيسية → "الملفات المهملة" (1) → فحص/F5 (2) → النتائج محددة مسبقًا (`JunkRow._isChecked = !IgnoredShown`) → "تنظيف المحدد" (3) → نعم (4). أربع نقرات بلا التفاف. عنوان النافذة يعكس التحديد، و`ScanOptionsExpander` يُطوى تلقائيًا بعد النتائج ليأخذ الجدول المساحة.
- الاختصارات **موصولة فعلًا**: `MainWindow.Window_KeyDown` يربط F5/Esc بزر الصفحة المرئية ولا يطلقه إلا `IsEnabled && IsVisible`؛ المسافة تبدّل خانة الصف عبر نمط Toggle في الأتمتة (`GridKeys.OnKey`).
- "وين أنا": `NavItem` يعرض علامة + أيقونة ملوّنة + نص غامق للصفحة الحالية.

**السلبي:**
- **لا مفاتيح وصول (access keys) إطلاقًا:** القوالب فيها `RecognizesAccessKey="True"` لكن صفر `_` في 663 نصًا، ولا `InputBinding` لـ Ctrl+A أو Delete.
- حجم النافذة وحالتها وحالة `ScanOptionsExpander` **لا تُحفظ** (`UiSettings` فيه `Language` و`Theme` فقط).
- فحص الاختصارات **لا يمكن إلغاؤه** (`ShortcutsViewModel.CanCancel => false`) وصفحته بلا زر إلغاء بخلاف كل صفحات الفحص.
- شروحات بالتلميح فقط (`DpCheckOneTip`, `RgThoroughHint`, `OptTempAllIgnoresTip`, `HsAllUsersHint`) — غير مرئية للمس ولوحة المفاتيح؛ `HistoryView` وحده يضبط `ToolTipService.ShowOnDisabled`.

### 3.3 الاستجابة — **7/10**

- `MainWindow`: `MinHeight="460" MinWidth="760"`؛ `app.manifest` يعلن `PerMonitorV2` و`longPathAware`؛ `Fit.MaxHeightShare` يحدّ ارتفاع لوحات الخيارات؛ الأشرطة `WrapPanel`؛ الفواصل قابلة للتشغيل بلوحة المفاتيح؛ الرسوم البيانية تنهار إلى عمود واحد تحت 620 بكسل.
- **الشريط الجانبي ثابت 212 بكسل ولا ينطوي** (`Nav.xaml`: `<ColumnDefinition Width="212" />`). عند العرض الأدنى 760 تبقى للصفحة ~510 بكسل.
- أعمدة جدول الملفات المهملة ثابتة بمجموع ~984 بكسل (`34+220+300+100+110+220`)، و`ResultsGrid_SizeChanged` يثبّت عمود المجلد عند `Math.Max(150, available)` فيظهر تمرير أفقي في النوافذ الصغيرة.
- عروض مثبتة بالبكسل ستقصّ النص العربي/الإنجليزي الطويل عند تكبير 125–150%:
  > `src/CleanSuite.App/RegistryView.xaml`: `<DataGridTextColumn Header="{loc:T RgColData}" … Width="260"`
  > `src/CleanSuite.App/RestoreWindow.xaml`: `<DataGridTextColumn Header="{loc:T ColOriginalPath}" … Width="440"`
  > `src/CleanSuite.App/MainWindow.xaml`: `<ComboBox x:Name="RemovalCombo" … Width="215"`، و`FoldersList Height="50"` (≈ صفّان).

### 3.4 العربية و RTL — **8/10**

**القوي (فُحص فعليًا، ليس افتراضًا):** كل `Window` يربط `FlowDirection="{Binding FlowDirection, Source={x:Static loc:Loc.Instance}}"`؛ `Loc.NumberCulture` مثبّت على en-US و`AppWindow` يضبط `NumberSubstitution.Substitution="European"` (لأن ar-SA تحت ICU تخلط الأرقام اللاتينية بفواصل عربية)؛ التواريخ ميلادية ISO بـ `InvariantCulture` (التقويم الافتراضي هجري)؛ `Loc.Digits` يقبل الأرقام الهندية المدخلة؛ نمط `PathCell` يفرض LTR + محاذاة يمين للمسارات؛ الأيقونات مثبتة LTR إلا الاتجاهية؛ `Bidi.SetText` يحوّل علامات LRE/PDF إلى `Run` LTR لأن محرك نصوص WPF يتجاهلها؛ **54 رمز جمع عربي** بقواعد المثنى و3–10 و11–99 الصحيحة في `Loc.Plurals`.

**العيب الأكبر — مسارات وأسماء تُلقى في جمل عربية بلا `Loc.Ltr`/`Bidi.SetText` (8 مواضع، نفس فئة الخطأ):**
> `src/CleanSuite.App/MainWindow.xaml.cs` — في `ExcludeAlways_Click()`:
> ```csharp
> SetStatus(() => Loc.F("ExcludedStatus", Loc.N0(count), file));
> ```
> `src/CleanSuite.App/UninstallView.xaml.cs` — في `Report_Click()` و`Remove_Click()`:
> ```csharp
> StatusText.Text = Loc.F("UnReportSaved", dlg.FileName);
> StatusText.Text = Loc.F("UnRemoved", r.BackupPath) + " · " + StatusText.Text;
> ```
> `src/CleanSuite.App/RegistryView.xaml.cs` — في `Render()`:
> ```csharp
> + (_vm.LastBackup != null ? " · " + Loc.F("RgBackupAt", _vm.LastBackup) : "")
> ```
> وكذلك `RestoreView.Reload`, `ShortcutsView.Retarget_Click` (`ScRetargetDone` مع `row.Item.Name`), `StartupView.Apply` (`StChanged`). كلها عبر `.Text` العادي، فمسار `C:\…` أو اسم برنامج لاتيني داخل جملة عربية **يُعاد ترتيبه** بمحرك bidi.

**إنجليزية تتسرب إلى الواجهة العربية:** `StartupRow.KeyName` يرجع `"Run"`, `"Policies Run"`, `"Terminal Server Run"` ملحقة بعد وسم عربي؛ `DiskRow.Attrib` يعرض `H/S/R`؛ `HistoryActionRow.TargetText` يربط بـ `"  ::  "`؛ رسائل استثناءات نظام التشغيل تمر بـ `Loc.Reason` بلا ترجمة.

**محاذاة:** أعمدة الحجم في `MainWindow`/`RestoreWindow` بـ `HorizontalAlignment="Right"` تنعكس إلى يسار بصري في RTL، فتنتهي الأرقام على الجهة المقابلة لنص `PathCell` في نفس الصف.

### 3.5 إمكانية الوصول — **8/10**

- كل زر أيقوني/مركّب له `AutomationProperties.Name`؛ خانات الجدول مسماة بالمسار؛ القوائم المنسدلة بـ `LabeledBy`؛ `NavTabControl.Peer` يكشف رأس/ذيل الشريط؛ `Live.Announce` على 12 كتلة حالة (polite، تجميع 700 ms). التباين العالي: `ThemeManager.HighContrastPalette()` يربط كل المفاتيح بـ `SystemColors` ويتابع `UserPreferenceChanged` ويزيل Mica. الحالة ليست باللون وحده أبدًا (`StatusChip` يحمل نصًا، `DonutChart` له مفتاح نصي وأسماء أتمتة لكل شريحة).
- **التباين (محسوب من قيم اللوحة):** الفاتح: `TextFg/SurfaceBg` 17.2، `TextMutedFg` 6.2، `AccentFg/AccentBg` 6.3، `DangerFg` 5.7، الشرائح 5.7–7.0، `ControlBorder` 3.45 (يحقق WCAG 1.4.11). الداكن: 12.4 / 6.5 / 10.5 / 7.0، الحدود 3.3. كلها ناجحة.

**الفشل:**
- > `src/CleanSuite.App/DiskView.xaml` — في `DataTemplate x:Key="ShareCell"`:
  > ```xml
  > <ProgressBar Minimum="0" Maximum="100" Value="{Binding Percent, Mode=OneWay}" Height="16" />
  > <TextBlock Text="{Binding PercentText}" HorizontalAlignment="Center" VerticalAlignment="Center" FontSize="11" />
  > ```
  > نص `TextFg` فوق تعبئة الأكسنت: **2.73 فاتح / 1.76 داكن** — دون 4.5:1 في السمتين.
- شرائح الرسم البياني مقابل الأبيض: 4 من 8 تحت 3:1 (`#EDA100` 2.17، `#1BAF7A` 2.82، `#E87BA4` 2.69) — مخفف بفجوات 2 بكسل والمفتاح النصي.
- **ترتيب التركيز:** > `src/CleanSuite.App/Themes/Nav.xaml` — في قالب `NavTabs`:
  > ```xml
  > <ContentPresenter DockPanel.Dock="Top" Content="{TemplateBinding theming:Nav.Header}" … />
  > <ContentPresenter DockPanel.Dock="Bottom" Content="{TemplateBinding theming:Nav.Footer}" … />
  > <ScrollViewer …><StackPanel IsItemsHost="True" KeyboardNavigation.DirectionalNavigation="Cycle" />
  > ```
  > الذيل (اللغة، السمة، حول) معلن **قبل** مضيف العناصر، فـ Tab يزور قوائم اللغة/السمة قبل قائمة التنقل.

### 3.6 التغذية الراجعة — **9/10**

- انشغال: أشرطة غير محددة في Junk/Registry/History/Compactor/ScheduledRun؛ عدّاد تقدم في Disk/Duplicates (`DpHashing "{0}%"`)؛ `SetBusy` يعطّل الأزرار الصحيحة ويبقي "إلغاء" فقط عندما يمكن الإلغاء.
- **16 تأكيد `YesNo` كلها بافتراضي `MessageBoxResult.No`**؛ الحذف الدائم يحوّل الزر إلى `DangerButton` والمربع إلى `Warning`؛ حراس "المجموعة كاملة" و"غير متحقق من المحتوى" في المكررات؛ الإغلاق يُرفض أثناء التنظيف (`WaitForClean`).
- كل تنظيف ينتهي بتقرير فيه مسار النسخة الاحتياطية والـ journal؛ `UiErrors.Show` يسجل ويعرض `ErrUnexpected` مع مسار السجل؛ حالات فارغة على 8 جداول.
- **الناقص:** لا تلميح فراغ على `DiskView.EmptyGrid` و`StartupView.Grid` و`UninstallView.Grid`؛ فحص الملفات المهملة لا يعرض عدد الملفات أثناء التشغيل (`Scanning… (read-only)` فقط) بخلاف القرص والمكررات.

**الترجمة:** 663 مفتاحًا في `En` و663 في `Ar`، صفر مفقود في الاتجاهين (محقق، وكذلك `LocalizationTests`). العربية الأفضل كتابة (54 رمز جمع)؛ الإنجليزية ما زالت تستخدم `(s)` / `entr(y/ies)` في 46 نصًا (`"{0} file(s), {1}, will be {2}."`) و`Loc.Plurals` لا يعالج إلا الصيغ العربية.

**متوسط القسم:** (9 + 8 + 7 + 8 + 8 + 9) ÷ 6 = **8.2/10**

---

## 4. المراجعة الأمنية (25%)

> السياق يحدد الحكم: أداة سطح مكتب **أوفلاين بلا خادم ولا شبكة ولا مصادقة**، تعمل بصلاحيات المستخدم (`asInvoker`). بنود المفاتيح/الأسرار وCORS/الترويسات والمصادقة **لا تنطبق** ولم تُحسب في الدرجة. الخطر الحقيقي هنا: عملية حذف تصيب الهدف الخطأ، وسباق TOCTOU، والثقة في بيانات قابلة للكتابة، وتصعيد عند "تشغيل كمسؤول".

**الحكم العام:** المحرك متين. كل مسار مدمّر محروس بفحص أخير مستقل، ويعيد التحقق من هدفه وقت التنفيذ، وينسخ قبل تغيير السجل، ويسجّل قبل الإبلاغ. **لا ثغرة حرجة**. اثنتان متوسطتان وست بسيطة.

### 🟠 أ1 — نقاط إعادة التحليل (junctions) في *أسلاف* الهدف لا تُفحص وقت الحذف (TOCTOU) + أسماء 8.3 غير محلولة

> `src/CleanSuite.Junk/JunkRemover.cs` — في `Remove()`:
> ```csharp
> var reason = _protected.Check(item.FullPath);
> …
> var fi = new FileInfo(item.FullPath);
> if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) { failed.Add((item.FullPath, "reparse point")); continue; }
> if (fi.Length != item.Size || item.LastWriteUtc != default && fi.LastWriteTimeUtc != item.LastWriteUtc)
> ```
> ونفس النمط في `src/CleanSuite.History/HistoryActions.cs` — `DeleteFileAction.Execute`. و`ProtectedPaths.Normalize` نصي بحت:
> ```csharp
> return Path.GetFullPath(path).TrimEnd('\\');
> ```

الفاحصات تتخطى reparse points **أثناء التعداد** (ممتاز)، لكن: (أ) جذور الفحص ومجلدات Temp نفسها لا تُفحص أبدًا (`TempFolderLocator.Locate`, `JunkScanner.NormalizeRoot`, `--root` في CLI — صفر فحص `ReparsePoint` في `TempFolderLocator.cs`)؛ (ب) بين الفحص والحذف تقدر عملية بنفس المستخدم تستبدل مجلدًا وسيطًا (`%TEMP%\foo`) بوصلة إلى `C:\Windows\System32\config` وتنشئ ملفًا بنفس الحجم والوقت بالاسم المطابق، فيتبع `SHFileOperation`/`File.Delete` الوصلة بينما `ProtectedPaths.Check` يرى المسار النصي البريء؛ (ج) `Path.GetFullPath` لا يوسّع أسماء 8.3، فـ `C:\Windows\SERVIC~1\x` أو `C:\PROGRA~1` لا تُطابَق مع `servicing`/`Program Files`. الأثر محدود بصلاحيات المستخدم إلا عند "تشغيل كمسؤول" (وهي نقرة واحدة) — هذه فئة الخطأ الكلاسيكية لأدوات التنظيف.

**الحل:** قبل كل حذف/إرسال للسلة/rmdir: حُلّ المسار الحقيقي (`GetFinalPathNameByHandle` عبر مقبض `FILE_FLAG_BACKUP_SEMANTICS`، أو `FileInfo.ResolveLinkTarget(true)` مع مشي على كل مكوّن مجلد وفحص `ReparsePoint`)، ثم شغّل `ProtectedPaths.Check` على المسار **المحلول**، وارفض إذا اختلف الجذر/الشجرة. وارفض جذور فحص أو مجلدات Temp هي reparse points.

### 🟠 أ2 — جذور مسح تؤخذ من إعدادات قابلة للكتابة بلا تحقق معقولية

> `src/CleanSuite.History/WindowsHistoryItems.cs` — في `PlanOutlookTemp()` (العنصر 31، **مفعّل افتراضيًا** فيدخل في التشغيل المجدول):
> ```csharp
> if (_reg.ReadString(RegHive.CurrentUser, $@"Software\Microsoft\Office\{ver}\Outlook\Security", "OutlookSecureTempFolder") is { Length: > 0 } f)
>     folders.Add(Environment.ExpandEnvironmentVariables(f).TrimEnd('\\'));
> …
> foreach (var sub in Directory.Exists(f) ? Directory.EnumerateDirectories(f).ToList() : new List<string>())
>     _files.AddEmptyFolder(a, itemKey, sub, removeFolder: true, ct);
> ```

أي عملية بصلاحيات المستخدم (أو Outlook مضبوط غلط) توجّه هذه القيمة إلى `C:\Users\me\Documents` تجعل المخطِّط يدرج **كل ملف في كل مجلد فرعي من المستندات** للحذف؛ `ProtectedPaths` يحمي جذر المستندات فقط لا محتوياته. تفاعليًا يرى المستخدم القائمة؛ في `ScheduledRunner.History` ما أحد يراها.

نفس الفئة في `src/CleanSuite.History/FirefoxCleaner.cs` — `PlanProfile()`/`CacheParentDirectory()`: مسار `Path=` مطلق في `profiles.ini` أو `browser.cache.disk.parent_directory` يجعل المنظّف يفرغ ويحذف `Cache` ويفرغ خمسة مجلدات تحت مجلد اعتباطي:
> ```csharp
> foreach (var dir in new[] { "thumbnails", "cache2", "jumpListCache", "minidumps", "startupCache" })
>     _files.AddEmptyFolder(plan.Actions, k, P(dir), removeFolder: false, ct);
> ```

**الحل:** Outlook — اقبل فقط مجلدات تحت `InternetCache`/`LocalAppData`/`Temp` ورقتها `Content.Outlook` (أو مجلداتها الفرعية بأسماء Outlook العشوائية ذات 8 أحرف). Firefox — مع مجلد cache مخصص تصرّف في `<parent>\cache2` فقط واشترط وجود `cache2\index`؛ ومع مسار ملف تعريف مطلق اشترط `prefs.js`/`times.json` قبل أي تخطيط.

### 🟡 أ3 — التشغيل المجدول (بلا مراقب) يمسح الحافظة

> `src/CleanSuite.Scheduler/ScheduledRunner.cs` — في `History()`: `WindowsHistoryCatalog.Items.Where(i => i.DefaultOn)`، و`src/CleanSuite.History/WindowsHistoryItems.cs`: `new HistoryItemInfo(9, HistoryPage.Windows, "win.clipboard", true)`.

مهمة تعمل عند تسجيل الدخول أو يوميًا تدمّر بصمت ما على حافظة المستخدم (`ClearClipboardAction`). (العنصر 26 الذي يغلق Explorer ويعيد تشغيله **غير مفعّل افتراضيًا** — صحيح.)
**الحل:** ثابت `UnattendedHistoryItems` يستثني العنصر 9، على غرار `UnattendedRegistryCategories` الموجود فعلًا للسجل.

### 🟡 أ4 — استعادة السجل بلا قائمة وجهات مرفوضة (استعادة الملفات لها واحدة)

> `src/CleanSuite.Safety/RegistryAccess.cs` — في `RegFileImporter.Import()`:
> ```csharp
> current = roots.Root(hive).CreateSubKey(slash < 0 ? "" : path[(slash + 1)..], writable: true);
> ```

ملف `.reg` غريب أو معدَّل في مجلد النسخ (`BackupTrust.Unknown`، الواجهة تحذّر) يقدر يزرع قيم `…\CurrentVersion\Run`, `RunOnce`, `Winlogon`, `Image File Execution Options`, `Services`؛ وتشغيل HKCU التلقائي لا يحتاج رفع صلاحيات. `JunkBackupArchive.RefusedDestination` يرفض مجلدات Startup للملفات؛ السجل بلا نظير.
**الحل:** في `BackupCatalog.RestoreRegistry` عندما الثقة ≠ `Verified`، تخطَّ (أو اعرض لتأكيد صريح) المفاتيح المطابقة لقائمة منع الاستمرارية/التشغيل التلقائي، وأبرزها في `RegistryKeys()`.

### 🟡 أ5 — نسخة zip تُتحقق بالطول فقط قبل حذف الأصول

> `src/CleanSuite.Junk/JunkRemover.cs` — في `JunkBackupArchive.Create()`:
> ```csharp
> archived = archived.Where(a => entryOf.TryGetValue(a.FullPath, out var en) && lengths.TryGetValue(en, out var len) && len == a.Size).ToList();
> ```

`ZipArchiveEntry.Length` يأتي من الرأس؛ تيار deflate تالف (خطأ قرص، امتلاء القرص وسط الكتابة) يمر، ثم تُحذف الملفات.
**الحل:** في مرحلة التحقق اقرأ تيار كل مدخل حتى النهاية (`entry.Open()` يتحقق من CRC عند القراءة).

### 🟡 أ6 — P/Invoke بلا `DefaultDllImportSearchPaths`

صفر استخدام للسمة في كل `src/`. `msi.dll` (ليست من KnownDLLs) مستوردة في `src/CleanSuite.Safety/ShellLinkFile.cs`. .NET يبحث في مجلد التطبيق أولًا، والتثبيت الافتراضي لكل مستخدم (`PrivilegesRequired=lowest`) + تشغيل مرفوع = سطح hijack.
**الحل:** `[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]` في كل مشروع محرك وفي التطبيق.

### 🟡 أ7 — إقحام معرّفات في SQL (نظافة؛ ثوابت فقط اليوم)

> `src/CleanSuite.History/SqliteActions.cs` — في `Sqlite.Distinct()`: `cmd.CommandText = $"SELECT DISTINCT \"{column}\" FROM \"{table}\"";` وفي `SqlDeleteKeysAction.Execute()`: `$"DELETE FROM \"{Table}\" WHERE \"{Column}\" = $k"`.

كل المستدعين يمررون ثوابت، والمفاتيح معلّمة (`$k`) — صحيح. لكن `Count()` يهرّب `"` والباقي لا.
**الحل:** هرّب كما في `Count` أو تحقق بـ `^[A-Za-z0-9_]+$`.

### 🟡 أ8 — تعابير نمطية/تخصيص بلا حدود على مدخلات غير موثوقة (DoS فقط)

`src/CleanSuite.Cli/Program.cs` — `PluginsDecode`: `Regex.Matches(text, @"(?m)^\[([^\]]+)\]\r?\n((?:(?!^\[).*\r?\n?)*)")` بلا `MatchTimeout`. و`JunkBackupArchive.CentralDirectoryNames`: `var cd = new byte[size];` بحجم 32 بت من EOCD (zip مصنوع 4 GB → OOM عند الإدراج). أثر منخفض؛ أضف مهلة وسقفًا.

### الاعتماديات

- `dotnet list package --vulnerable --include-transitive`: **لا حزم معرّضة** في الـ 17 مشروعًا (شُغّل فعلًا على مصدر nuget.org).
- `SQLitePCLRaw.bundle_e_sqlite3 2.1.14` مثبّت عمدًا لتجاوز GHSA-2m69-gcr7-jv3q — تصرف صحيح وموثق في `csproj`.
- 🟠 **`net8.0` ينتهي دعمه (LTS) في 10 نوفمبر 2026 — بعد ثلاثة أسابيع من هذه المراجعة.** الترحيل إلى `net10.0` (LTS) مطلوب قريبًا، والـ CI سيبدأ يحذّر.

### ما هو جيد فعلًا (بالتحديد)

- **`ProtectedPaths.Check`**: أشجار (System32, WinSxS, servicing, Installer, Fonts…) وجذور (الملف الشخصي، AppData، المستندات، سطح المكتب) و`AppContext.BaseDirectory` و"جذر قرص" و"يحتوي مجلدًا محميًا" للمجلدات؛ مطبّق في Junk وDuplicates وHistory (بقائمة سماح *دقيقة* في `HistoryExecutor.ForHistory`) وShortcuts وإجراءات SQL والاستعادة.
- **فحص "تغيّر بعد المسح" في كل مكان**: حجم + وقت (`JunkRemover`, `DeleteFileAction`, `FilterLinesAction` الذي يستبدل ذريًا)، و`RegistryCleaner.Clean` يقارن `RawValue` و`KeyStamp` (`RegQueryInfoKeyW`: عدد المفاتيح الفرعية والقيم ووقت الكتابة) ويرفض المفاتيح العليا.
- **سلة المحذوفات** (`ShellRecycleBin`): `WhyNotRecyclable` لكل وحدة تخزين (نوع القرص، `SHQueryRecycleBin`, `BitBucket\NukeOnDelete`)، و`FOF_WANTNUKEWARNING`، والوجود بعد العملية هو الحقيقة الوحيدة.
- **نسخ zip**: manifest بمسارات أصلية محققة (`ValidateOriginalPath` يرفض `..`، `.`، `\\?\`، النسبية)، `RefusedDestination` (مجلدات Startup؛ Program Files لغير `Verified`)، `ExtractToFile(overwrite:false)`، دفتر SHA-256. **لا zip-slip**: الوجهات لا تأتي من أسماء المدخلات أبدًا.
- **نسخة `.reg` قبل كل تغيير** (`RegBackupWriter`: البايتات الدقيقة عبر `RegQueryValueExW`، hex لغير القابل للتمثيل، يرفض الأسماء بـ CR/LF/NUL، إعادة محاولة `ERROR_MORE_DATA`)؛ المستورد يتجاهل `[-` و`=-`؛ `UninstallManager.RemoveEntry` يعيد قراءة الملف ويتأكد أن المفتاح فيه قبل الحذف.
- **`DuplicateRemover`**: نسخة واحدة مضمونة إلا مع `allowWholeGroups`: الحافظ موجود، وملف فيزيائي مختلف (`FileIdentity` يلتقط subst والروابط الصلبة)، ومطابق بايتًا بايتًا *وقت الحذف*؛ `Permanent` يُخفَّض إلى سلة المحذوفات.
- **إطلاق إلغاء التثبيت**: `ResolveCommand` يحل الملف التنفيذي الحقيقي ويمرره كـ `lpApplicationName` (فلا `C:\Program.exe`)، يرفض النسبي/المفقود، `runas` فقط عند `ERROR_ELEVATION_REQUIRED`؛ `HasProductCode` بتعبير نمطي يمنع حقن وسيطات.
- **الجدولة**: XML بـ `XElement` (مهرَّب)، `LeastPrivilege`/`InteractiveToken`، `TaskXml.Parse` يرفض المهام الغريبة، السجل بلا مراقب محدود بست فئات منخفضة الخطر، عزل الاستثناءات لكل خطوة.
- `PathProbe`: رفض الوصول والأقراص غير المقروءة (BitLocker/غير جاهز) تُعدّ موجودة؛ توائم WOW64.
- `OperationJournal` يُكتب قبل النتائج؛ CLI للقراءة فقط برفض صارم للخيارات المجهولة.

**غير قابل للتحقق ساكنًا:** هل `FOF_WANTNUKEWARNING` يظهر مربعًا حاجبًا في تشغيل مجدول صامت (ملف أكبر من السلة)؛ سلوك `SHFileOperation` الفعلي عبر الوصلات؛ كفاية قائمتي الحماية المضمّنتين على Windows 11 حي.

**درجة الأمن (من البنود المنطبقة فقط):** الحقن 9 · التحقق من المدخلات (أ2، أ8) 7 · الصلاحيات والتصعيد (أ1، أ6) 7 · البيانات الحساسة (أ3، أ4) 7 · الاعتماديات (صفر معرّض، لكن EOL وشيك) 8 → **7.6/10**

---

## 5. الدرجة الإجمالية المرجّحة

| الناحية | الدرجة | الوزن | المساهمة |
|---|---|---|---|
| تقنية وهندسية | 6.9/10 | 45% | 3.11 |
| UX/UI | 8.2/10 | 30% | 2.46 |
| أمن سيبراني | 7.6/10 | 25% | 1.90 |
| **الإجمالي** | | | **7.5/10** |

الحساب: (6.9 × 0.45) + (8.2 × 0.30) + (7.6 × 0.25) = 3.105 + 2.46 + 1.9 = **7.47 ≈ 7.5/10**

(المراجعة الأولى: 6.6 هندسة. التحسن الهندسي محدود رقميًا لأن أكبر بنود الإصلاح — الانهيارات والـ O(n²) — عولجت، لكن بنود التكرار والـ MVVM النصفي بقيت كما هي.)

---

## 6. خطة الإصلاح المرتبة بالأولوية

| # | الأولوية | الناحية | المشكلة | الحل المقترح | التأثير المتوقع بعد الحل |
|---|---|---|---|---|---|
| 1 | 🟠 مهم | أمن | TOCTOU عبر junctions في أسلاف الهدف، وأسماء 8.3 لا تطابق المسارات المحمية | `Safety/ProtectedPaths.cs`: دالة `ResolveReal(path)` بـ `GetFinalPathNameByHandle` + مشي على المكوّنات لفحص `ReparsePoint`؛ تُستدعى في `JunkRemover.Remove`, `HistoryActions.DeleteFileAction`, `DiskScanner.Recycle`, `ShortcutCleaner.Remove` قبل الحذف، وترفض عند اختلاف الجذر. و`TempFolderLocator.IsSafeTempFolder` + `JunkScanner.NormalizeRoot` يرفضان جذورًا هي reparse points | إغلاق فئة الثغرات الكلاسيكية لأدوات التنظيف؛ الحذف لا يتبع وصلة إلى System32 حتى عند التشغيل كمسؤول |
| 2 | 🟠 مهم | أمن | جذور مسح من `OutlookSecureTempFolder` وملفات Firefox بلا تحقق معقولية، وتدخل التشغيل المجدول | `History/WindowsHistoryItems.cs` `PlanOutlookTemp`: اقبل فقط ما تحت `InternetCache`/`LocalAppData`/`Temp` وورقته `Content.Outlook`. `History/FirefoxCleaner.cs` `PlanProfile`: اشترط `prefs.js` أو `times.json` في مجلد التعريف، وتصرف في `cache2` فقط إذا وُجد `cache2\index` | قيمة سجل خاطئة لا تحوّل المستندات إلى "مؤقتات Outlook" في تشغيل ليلي بلا مراقب |
| 3 | 🟠 مهم | أمن/تبعيات | `net8.0` ينتهي دعمه 2026-11-10 | رفع `TargetFramework` إلى `net10.0-windows10.0.17763.0` في الـ 17 `csproj`، وتحديث `Microsoft.Data.Sqlite`/`System.IO.Hashing` إلى 10.x، وتحديث `setup-dotnet` في `build.yml` | البرنامج يبقى على إصدار مدعوم أمنيًا؛ CI لا يبدأ بالفشل على تحذيرات EOL |
| 4 | 🟠 مهم | هندسة | `Junk` مكتبة أساسية ثانية؛ مجلد النسخ مكتوب 6 مرات | نقل `IRecycleBin`, `ShellRecycleBin`, `RemovalMode`, `JunkBackupArchive`, `ZipComment` من `Junk/JunkRemover.cs` إلى `Safety/`؛ صنف `Safety/CleanSuitePaths` (`Backups`, `Journal`, `Logs`, `Ignore_*`) يستبدل الـ 6 نسخ | إسقاط اعتماد Duplicates/Shortcuts/DiskUsage/Restore/Compat على Junk؛ مصدر حقيقة واحد للمسارات |
| 5 | 🟠 مهم | هندسة | خط أنابيب الحذف منسوخ 3 مرات، و`ShortcutCleaner` يتجاهل ناتج `Recycle` | `Safety/RecycleStep.cs`: `Run(IRecycleBin, IReadOnlyList<string>, OperationJournal, silent)` يطبّق تحقق → `WhyNotRecyclable` → `Recycle` → تأكد الاختفاء → journal؛ يستبدل الكود في `JunkRemover.Remove`, `DiskScanner.Recycle`, `ShortcutCleaner.Remove` | سلوك واحد ونص سبب واحد؛ إصلاح تجاهل القائمة المرتجعة في الاختصارات |
| 6 | 🟠 مهم | هندسة | نصف الصفحات خارج `ScanCleanViewModel`؛ `MainWindow` يعدّد 9 تبويبات يدويًا | نقل `MainWindow` (Junk), `DuplicatesView`, `DiskView`, `StartupView`, `UninstallView`, `RestoreView` إلى `ScanCleanViewModel<TRow>`؛ واجهة `IToolPage` في `App/` تكشف `IsBusy/IsChanging/ScanCommand/CancelCommand`؛ `Window_Closing` و`Window_KeyDown` يتكرران على `Tabs.Items.OfType<IToolPage>()` | زوال 6 آلات حالة مكررة؛ إضافة صفحة جديدة لا تتطلب تعديل `MainWindow` |
| 7 | 🟠 مهم | أداء | `DiskView` يمشي الشجرة الفرعية كاملة على خيط الواجهة عند كل اختيار | `DiskUsage/DiskModel.cs`: خزّن في `DiskFolder` قائمة الأكبر 100 وجدول الامتدادات محسوبين مرة واحدة في `DiskScanner.Scan` (أو احسبهما على `Task.Run` مع رمز إلغاء في `DiskView.RenderAll`) | اختيار `C:\` على قرص بمليون ملف لا يجمّد الواجهة |
| 8 | 🟠 مهم | UX/RTL | 8 مواضع تحقن مسارات/أسماء في جمل عربية بلا `Loc.Ltr` | `MainWindow.ExcludeAlways_Click`, `MainWindow.SetStatus` (حوّله إلى `Bidi.SetText`), `UninstallView.Report_Click/Remove_Click`, `RegistryView.Render`, `RestoreView.Reload`, `ShortcutsView.Retarget_Click`, `StartupView.Apply`: لفّ القيمة بـ `Loc.Ltr(...)` واعرض عبر `Bidi.SetText` | المسارات اللاتينية داخل الجمل العربية تظهر بترتيبها الصحيح |
| 9 | 🟡 تحسين | أمن | التشغيل المجدول يمسح الحافظة | `Scheduler/ScheduledRunner.cs`: ثابت `UnattendedHistoryItems = DefaultOn - {9}` يُستخدم في `History()` | لا فقدان صامت لمحتوى الحافظة عند تسجيل الدخول |
| 10 | 🟡 تحسين | أمن | استعادة `.reg` غير موثوق تقدر تزرع مفاتيح تشغيل تلقائي | `Restore/BackupCatalog.cs` `RestoreRegistry`: عندما `Trust != Verified` تخطَّ مفاتيح `Run`, `RunOnce`, `Winlogon`, `Image File Execution Options`, `Services`, `Policies` واعرضها في `RegistryKeys()` بوسم تحذير | ملف مزروع في مجلد النسخ لا يصير وسيلة استمرارية |
| 11 | 🟡 تحسين | أمن | تحقق zip بالطول فقط قبل حذف الأصول | `Junk/JunkRemover.cs` `JunkBackupArchive.Create`: في مرحلة التحقق افتح كل مدخل واقرأه إلى النهاية (`entry.Open().CopyTo(Stream.Null)`) لتفعيل فحص CRC | قرص ممتلئ أو تالف لا ينتج نسخة فاسدة وحذفًا للأصول |
| 12 | 🟡 تحسين | أمن | P/Invoke بلا تقييد مسار البحث | `[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]` في `AssemblyInfo.cs` لكل مشروع محرك و`App` (أو في `Directory.Build.props` عبر `AssemblyAttribute`) | إغلاق DLL hijacking من مجلد التثبيت لكل مستخدم عند التشغيل المرفوع |
| 13 | 🟡 تحسين | هندسة | إلغاء غير مكتمل؛ قراءة `_cts` على خيط آخر | مرّر `CancellationToken` إلى `ShortcutCleaner.Remove`, `StartupManager.Scan`, `UninstallScanner.Scan`, `EmptyFolderFinder.Find`؛ `ShortcutsViewModel`: `s.Scan(ct)` واحذف `CanCancel => false` وأضف زر إلغاء في `ShortcutsView.xaml`؛ `MainWindow.Scan_Click`: `var ct = _cts.Token` قبل `Task.Run` | Esc يعمل في كل الصفحات؛ لا سباق على الحقل |
| 14 | 🟡 تحسين | هندسة | رفضات غير مسجلة وابتلاع فشل الدفتر | `JunkRemover.Remove`: `journal.Record("skip", …)` لـ "no longer exists" و"reparse point"؛ `BackupLedger.Record` يرجع `bool` ويُسجَّل فشله؛ `DiskGrouping.GroupOf` و`SystemEnvironment.GetTempPath`: مرشّحات `ArgumentException or NotSupportedException or PathTooLongException` بدل `catch {}` | الـ journal يروي القصة كاملة؛ لا "Unknown" غامضة في الاستعادة |
| 15 | 🟡 تحسين | أداء | O(n²) عند "تحديد الكل" في الملفات المهملة | `MainWindow.xaml.cs`: عدّاد `_selectedCount` ومجموع `_selectedBytes` يُحدَّثان في `RowCheck` بـ O(1)، و`CheckAll_Click` يعلّق `UpdateSelectionStatus` حتى نهاية الحلقة | 50 ألف صف تُحدَّد فورًا بدل ثوانٍ |
| 16 | 🟡 تحسين | UX/A11y | ترتيب التركيز يبدأ بالذيل؛ `ShareCell` يفشل التباين (2.73/1.76) | `Themes/Nav.xaml`: استبدل `DockPanel` بـ `Grid` بثلاثة صفوف (رأس/عناصر/ذيل) بترتيب الإعلان الطبيعي. `DiskView.xaml` `ShareCell`: ضع `PercentText` بجانب الشريط أو على حبة بلون `SurfaceBg` | Tab يصل إلى التنقل أولًا؛ نسبة المشاركة مقروءة في السمتين |
| 17 | 🟡 تحسين | UX/RTL | إنجليزية تتسرب (`Run`, `Policies Run`, `H/S/R`) | `StartupView.xaml.cs` `KeyName` و`DiskView.xaml.cs` `DiskRow.Attrib`: مفاتيح في `Strings.cs` (`StKey_Run`, `DaAttr_Hidden`…) وعرض أسماء المفاتيح في `Run` LTR | واجهة عربية بلا كلمات إنجليزية ملحقة |
| 18 | 🟡 تحسين | UX | لا مفاتيح وصول؛ الحجم والحالة لا تُحفظ؛ شريط جانبي ثابت | `Strings.cs`: `_` في أزرار الفحص/التنظيف/الإلغاء؛ `MainWindow.xaml`: `InputBindings` (Ctrl+A, Delete)؛ `UiSettings.cs`: `Width/Height/WindowState/OptionsExpanded`؛ `Nav.xaml`: طيّ إلى أيقونات تحت ~1000 بكسل؛ استبدل `Width="260"/"440"/"285"` بـ `*`+`MinWidth` | استخدام بلوحة المفاتيح فقط ممكن؛ النافذة تعود كما تُركت؛ لا قصّ عند 150% |
| 19 | 🟡 تحسين | هندسة | ست مشّايات مجلدات بحراس MAX_PATH غير متسقة | `Safety/SafeWalk.cs`: مشّاية تكرارية واحدة (`Stack`) تتخطى reparse وتحرس الطول (ثابت واحد) وتقبل الإلغاء؛ تُستخدم من `JunkScanner`, `DuplicateScanner`, `DiskScanner`, `EmptyFolderFinder`, `ShortcutScanner`, `RulePlanner` | إزالة 19 فحص reparse مكرر؛ `JunkScanner` لا يفيض المكدس على شجرة عميقة |
| 20 | 🟡 تحسين | اختبارات | `History` 48% تغطية؛ `App` غائب من التقرير؛ `ReviewFixTests` بالأرقام | جدولة `WindowsHistoryCleaner.Plan` بالعنصر ثم اختبار كل عنصر ضد الخلية الخاصة؛ إضافة `CleanSuite.App` إلى coverlet أو توثيق الاستثناء في `NEXT_STEPS.md`؛ توزيع `ReviewFixTests.cs` على ملفات الميزات | الرقم 77.9% يصير صادقًا عن المشروع كله؛ أخطر محرك (مسح الآثار) مغطى |
| 21 | 🟡 تحسين | هندسة | `WindowsHistoryCleaner.Plan` 130 سطرًا بـ 25 حالة رقمية | `History/WindowsHistoryItems.cs`: `Dictionary<int, Action<PlanContext>>` أو صنف لكل عنصر مع `Id` و`Plan()` | قراءة عنصر واحد بلا تمرير في switch؛ يسهّل البند 20 |
| 22 | 🟡 تحسين | توثيق | لا CHANGELOG/CONTRIBUTING/ARCHITECTURE؛ `Strings.cs` بدل resx | `CHANGELOG.md` من 1.0.0؛ `CONTRIBUTING.md` قصير؛ `docs/ARCHITECTURE.md` يشرح الرسم الاعتمادي ونموذج journal/ledger/backup/skip-if-changed؛ ترحيل `Strings.cs` إلى `Strings.ar.resx`/`Strings.en.resx` مع حذف تعليقات `(review fix N)` | مساهم جديد يفهم نموذج الأمان بلا قراءة تعليقات XML؛ مترجم يعمل بلا مبرمج |
| 23 | 🟡 تحسين | أمن | معرّفات SQL غير مهرّبة؛ regex بلا مهلة؛ حجم EOCD بلا سقف | `SqliteActions.cs` `Distinct`/`SqlDeleteKeysAction`: هرّب `"` كما في `Count`؛ `Cli/Program.cs` `PluginsDecode`: `RegexOptions` + `TimeSpan.FromSeconds(5)`؛ `JunkBackupArchive.CentralDirectoryNames`: سقف 64 MB | إغلاق ثلاث مسارات DoS/نظافة منخفضة الخطر |
| 24 | 🟡 تحسين | CI | لا حد تغطية ولا فحص تنسيق ولا مهمة نشر | `build.yml`: خطوة `dotnet format --verify-no-changes`، وحد أدنى للتغطية (مثلًا 75%) عبر `coverlet` threshold، ومهمة `release` على الوسوم تستدعي `build.ps1 -SkipTests` وترفع المُثبِّت مع SHA-256 | الإصدارات تُبنى بنفس الطريقة كل مرة؛ التغطية لا تنحدر بصمت |
| 25 | 🟡 تحسين | UX | لا تلميح فراغ على 3 جداول؛ فحص الملفات المهملة بلا عدّاد | `DiskView.EmptyGrid`, `StartupView.Grid`, `UninstallView.Grid`: `Empty.Hint`؛ `JunkScanner`: `IProgress<long>` يُمرَّر من `MainWindow.Scan_Click` إلى `Scanning… {0}` | اتساق التغذية الراجعة بين كل الصفحات |

---

**الخلاصة بسطر:** المحرك آمن ومختبَر بدرجة تفوق أغلب أدوات التنظيف مفتوحة المصدر، والواجهة العربية مبنية بفهم؛ الأولويات الثلاث قبل أي إصدار 1.1: إغلاق مسار الوصلات (1)، وتحقق معقولية جذور Outlook/Firefox (2)، والترحيل من .NET 8 قبل انتهاء دعمه في 10 نوفمبر (3).

---

## 7. حالة التنفيذ (2026-10-22)

نُفّذت البنود الخمسة والعشرون كلها في الجلسة نفسها، وتحقّق منها البناء (0 تحذيرات) و269 اختبارًا ناجحًا على .NET 10، وفحص التنسيق، ولقطات لكل الصفحات باللغتين.

| # | الحالة | ما تغيّر فعليًا |
|---|---|---|
| 1 | ✅ | `ProtectedPaths.CheckResolved` يحل المسار الحقيقي بـ `GetFinalPathNameByHandle`، ويُستخدم قبل كل حذف في `FileRemover` و`HistoryActions` و`DiskScanner` و`ShortcutCleaner` و`EmptyFolderFinder` وحذف مجلدات Temp الفارغة؛ وجذور الفحص ومجلدات Temp التي هي روابط تُرفض |
| 2 | ✅ | `IsOutlookTempFolder` في `WindowsHistoryItems.cs`؛ و`IsProfileFolder` وقصر مجلد cache المخصص على `cache2` مع `index` في `FirefoxCleaner.cs` |
| 3 | ✅ | `TargetFramework` في `Directory.Build.props` وحده: `net10.0-windows10.0.17763.0`؛ و`Microsoft.Data.Sqlite` و`System.IO.Hashing` 10.0.13؛ و`setup-dotnet` 10.0.x؛ و`build.ps1` يشترط .NET 10 SDK |
| 4 | ✅ | `RecycleBin.cs` و`ZipBackup.cs` و`FileRemover.cs` (ومعه `RemovalMode`) و`CleanSuitePaths.cs` في `Safety`؛ وأُسقط اعتماد Duplicates وShortcuts وDiskUsage وRestore على Junk |
| 5 | ✅ | `RecycleStep` في `Safety/FileRemover.cs`، ومعه نص سبب واحد `FileIdentity.ChangedReason` |
| 6 | ✅ مع ملاحظة | `ToolViewModel` أساس واحد؛ وصفحة الملفات المهملة صارت `JunkView` + `JunkViewModel`؛ و`DuplicatesViewModel`؛ و`ListPageViewModel` لبدء التشغيل والبرامج المثبتة والاستعادة؛ و`IToolPage` لكل الصفحات، فلم يعد `MainWindow` يعدّد التبويبات. صفحة «تحليل القرص» أُعيد بناؤها شجرةَ تنقّل، فبقيت حالتها في الصفحة نفسها لأنها ليست نمط فحص ← مراجعة ← تنظيف، لكنها تطبّق `IToolPage` |
| 7 | ✅ | عروض المجلد تُحسب على خيط خلفي وتُحفظ لكل مجلد (`FolderSnapshot`) |
| 8 | ✅ | `Loc.Ltr` و`Bidi.SetText` في كل المواضع الثمانية |
| 9 | ✅ | `ScheduledRunner.UnattendedHistoryItems` بلا الحافظة ولا إغلاق المستكشف |
| 10 | ✅ | `AutoStartKeys` في `Safety`، ويطبَّق في صفحة الاستعادة وفي زر الاستعادة بصفحة السجل |
| 11 | ✅ | `JunkBackupArchive.ReadsBack` يقرأ كل مدخل ويقارن CRC-32 |
| 12 | ✅ | `DefaultDllImportSearchPaths(System32)` لكل التجميعات من `Directory.Build.props` |
| 13 | ✅ | رموز الإلغاء في فحص الاختصارات وبدء التشغيل والبرامج المثبتة والمجلدات الفارغة، وزر إلغاء في صفحة الاختصارات |
| 14 | ✅ | كل رفض يُسجَّل؛ و`BackupLedger.Record` يرجع نجاحه ويُسجَّل فشله؛ ولا `catch {}` عارية |
| 15 | ✅ | عدّاد التحديد في `ScanCleanViewModel` نفسه، فيستفيد منه كل الصفحات |
| 16 | ✅ | صفوف `Grid` في الشريط الجانبي بترتيب القراءة؛ و`ShareCell` بنسبة بجانب الشريط |
| 17 | ✅ | أسماء مفاتيح Run والخصائص والأسباب مترجمة (24 سببًا جديدًا في `ReasonPrefixes`) |
| 18 | ✅ | مفاتيح وصول للأزرار الإنجليزية، وCtrl+A وDelete، وحفظ حجم النافذة ولوحة الخيارات، وطيّ الشريط الجانبي تحت 1000 بكسل، وأعمدة بلا عروض ثابتة |
| 19 | ✅ | `SafeWalk` تستخدمه كل المشّايات: الملفات المهملة، والمكررات، والاختصارات، وقواعد مسح الآثار، والقرص، والمجلدات الفارغة |
| 20 | ✅ | 269 اختبارًا (كانت 241)؛ وتغطية History من 48% إلى 63%؛ و`CleanSuite.App` صار في تقرير التغطية (9.6%)، فالرقم الكلي الصادق 57% والمحركات 80.3%؛ و`ReviewFixTests.cs` وُزّع على عشرة ملفات بحسب الموضوع |
| 21 | ✅ | `WindowsHistoryCleaner.Planners()` جدول مفتاحه اسم العنصر، واختبار يتأكد أن لكل عنصر مخطِّطًا |
| 22 | ✅ | `CHANGELOG.md` و`CONTRIBUTING.md` و`docs/ARCHITECTURE.md`؛ والنصوص في `Strings.resx` و`Strings.ar.resx` |
| 23 | ✅ | `Sqlite.Quote`، ومهلة للتعبير النمطي في الـ CLI، وسقف 64 MB لدليل zip المركزي |
| 24 | ✅ | `build.yml`: فحص التنسيق (`.editorconfig`)، وحد أدنى 80% لتغطية المحركات، ومهمة إصدار تبني المُثبِّت عند وسم `v*` |
| 25 | ✅ | تلميحات فراغ في جداول بدء التشغيل والبرامج المثبتة والمجلدات الفارغة، وعدّاد ملفات أثناء فحص الملفات المهملة |

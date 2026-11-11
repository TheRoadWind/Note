# 便签 Note

<img src="docs/appicon.png" width="120" alt="应用图标" />

基于 .NET MAUI 的 Android 本地便签与大文档阅读器。全部数据保存在应用私有目录，不联网、不上传、无账号。

![便签应用图标](Resources/AppIcon/appicon.png)

---

## 功能介绍

### 便签

- 新建、编辑、删除、置顶；**返回即自动保存**，标题与正文支持全文搜索
- 每条便签可**单独设置卡片背景色**（7 种预设）与**字体属性**（小 / 标准 / 大、粗体、斜体），编辑页与主页列表同步生效
- **插入图片**：从系统图片选择器选图，正文写入 `[img:文件名]` 标记并在底部显示缩略图；点缩略图可移除，删除便签时自动清理图片文件
- **一键复制**正文到剪贴板（自动过滤图片标记），顶部状态位短暂提示「已复制」，不弹窗

### 文档阅读

- 支持 `TXT / HTML / DOC / DOCX`，流式解析，可处理十几 MB 以上的大文档
- **自动生成目录**：识别「第X章 / 卷X / Chapter X / Markdown 标题 / HTML h1~h6 / 序章·番外」等，按章节整章分页，展示该章全部字符数（不再按固定字符上限截断）
- **翻页交互**：左右区域点击翻页、水平滑动翻页；屏幕中间点击不翻页，用于设置自动滑动
- **自动滑动**：可选 1 / 2 / 3 / 5 / 8 行每秒，自动滑动中点击中间即停止；滚到当页底部自动切换下一页；首页左侧、末页右侧的点击与滑动被屏蔽
- 自动滑动期间**屏幕常亮**，停止或离开阅读页自动释放
- **阅读记忆**：记录页、章节与页内滚动比例，从浏览记录重新打开时自动定位到上次位置
- 首次打开大文档显示解析进度；缓存命中时秒开且不闪屏

### 文件与缓存

- 底部 **主页 / 文件** 两个标签；文件页是浏览记录，可浏览手机存储、清空记录
- 以**内容指纹（SHA256）**识别文档：不同路径的同一份文档只保留一条浏览记录、一套预览缓存
- 预览缓存带模板版本，模板升级自动失效重建；只保留当前文档的预览
- 外部文件管理器 / 系统文件选择器打开时规范化临时副本，不重复占用空间
- 一键部署脚本会自动删除手机上的旧版本安装包，**只保留当前版本 APK**

---

## 项目文件夹结构

```text
Note/
├── App.xaml / App.xaml.cs              应用入口、全局颜色与控件样式
├── AppShell.xaml / AppShell.xaml.cs    Shell 导航（主页 / 文件 两个底部标签）
├── MauiProgram.cs                     MAUI 启动配置、平台 Handler 映射
├── MainPage.xaml / .cs                主页：便签列表、搜索、新建、打开新文件
├── NoteEditPage.xaml / .cs            便签编辑：图片、样式、复制、置顶、删除
├── FilesPage.xaml / .cs               文件：浏览记录列表
├── FileBrowserPage.xaml / .cs         手机存储文件浏览器
├── DocumentPage.xaml / .cs            阅读页：目录、翻页、自动滑动、位置记忆
├── Models/
│   ├── NoteItem.cs                    便签模型（背景色、字体属性、图片标记预览）
│   └── DocumentRecord.cs              浏览记录模型（上次阅读位置、内容指纹）
├── Services/
│   ├── NoteStore.cs                   便签 JSON 存储
│   ├── NoteImages.cs                  便签内嵌图片存取与标记解析
│   ├── HistoryStore.cs                浏览记录与阅读位置、按内容指纹去重
│   ├── DocumentReader.cs              流式解析、章节分页、内容指纹与预览缓存
│   ├── DocumentOpener.cs              统一打开入口、进度遮罩挂载
│   ├── OpenProgress.cs                大文档打开进度遮罩
│   ├── ChapterInfo.cs                 自动生成的目录条目
│   └── AppState.cs                    页面间临时状态（外部打开意图）
├── Platforms/Android/
│   ├── AndroidManifest.xml            权限与 application 标签
│   ├── MainActivity.cs                外部文件打开意图、系统栏配色
│   ├── MainApplication.cs
│   └── Resources/values/colors.xml
├── Resources/
│   ├── AppIcon/appicon.png            应用图标 + 启动页
│   ├── AppIcon/dl_icons8.png          icons8 图标素材
│   ├── Fonts/OpenSans-Regular.ttf
│   ├── Fonts/OpenSans-Semibold.ttf
│   ├── Images/                        预留图片目录（当前为空）
│   ├── Raw/AboutAssets.txt
│   └── Styles/Colors.xaml、Styles.xaml
├── Properties/launchSettings.json
├── tools/
│   ├── deploy.ps1                     一键构建 + 安装 + 手机 APK 清理
│   ├── rollback.ps1                   回滚到上一版本
│   └── build.log、check.log           构建输出记录
├── Note.csproj / Note.slnx
├── backups/Note_previous.apk          上一版本备份（部署脚本生成）
└── bin/、obj/                         构建产物
```

数据文件（运行时生成于应用私有目录）：`notes.json`、`history.json`、`fingerprints.json`、`note_images/`；缓存目录：`previews/`、`docs/`、`shared/`。

---

## 运行时

| 项目 | 值 |
| --- | --- |
| 目标框架 | `net10.0-android`（仅 Android） |
| .NET SDK | 10.0.401（构建环境） |
| UI 框架 | .NET MAUI，`Microsoft.Maui.Controls` 10.0.20（`$(MauiVersion)`） |
| 语言 | C# / XAML，`ImplicitUsings` + `Nullable` 开启，`MauiXamlInflator=SourceGen` |
| 最低系统 | Android API 21（Android 5.0）；安装目标 `targetSdk=36` |
| 权限 | `READ_EXTERNAL_STORAGE`（读取本地文档）；`GET_CONTENT` 查询（系统文件选择器） |
| 第三方依赖 | 仅 MAUI 与 `Microsoft.Extensions.Logging.Debug`，无其它 NuGet 包 |
| 应用 ID | `com.memo.note` |
| adb | 自动探测：`-AdbPath` → `PATH` → `ANDROID_HOME` / `ANDROID_SDK_ROOT` → `vswhere` → 常见默认位置；均找不到时自动下载 Platform-Tools，无硬编码绝对路径 |

### 构建与部署

```powershell
# 仅构建
dotnet build Note.csproj -f net10.0-android -c Release

# 一键部署（升版本 → 构建 → 安装 → 手机只保留当前 APK → 启动）
powershell -File tools\deploy.ps1

# 回滚
powershell -File tools\rollback.ps1
```

`deploy.ps1` 可选参数：`-DeviceId <序列号>`（多设备时指定）、`-AdbPath <路径>`（显式指定 adb）、`-NoBump`（不升版本）、`-NoBackup`（不备份上一版 APK）。`rollback.ps1` 同样支持 `-AdbPath`。
产物：`bin/Release/net10.0-android/com.memo.note-Signed.apk`；备份：`backups/Note_previous.apk`；手机安装包：`/sdcard/Download/Note_v<版本>.apk`。

### adb 自动安装

若 adb 与 Android SDK 都找不到，`deploy.ps1` 会自动下载 [Android SDK Platform-Tools](https://dl.google.com/android/repository/platform-tools-latest-windows.zip)（约 8 MB），解压到 `%LOCALAPPDATA%\Android\Sdk\platform-tools`，然后：

- 把该目录加入当前进程与用户级 `PATH`
- 仅当 `ANDROID_HOME` / `ANDROID_SDK_ROOT` 均未配置时才设置它们，**不覆盖已有 SDK**，避免影响 MAUI 构建
- 用户级环境变量写入失败时自动 UAC 提权写入机器级；提权被拒绝也不影响本次部署
- 下载或解压失败时打印手动安装步骤（下载地址、解压位置、环境变量、`-AdbPath`，或 Android Studio 的 SDK Manager）

`rollback.ps1` 不自动下载，缺少 adb 时会提示先运行一次 `deploy.ps1` 或按上述步骤手动安装。

---

## 版本

| 字段 | 当前值 |
| --- | --- |
| 显示版本 `ApplicationDisplayVersion` | **1.0.13** |
| 内部版本 `ApplicationVersion` | **14** |
| 手机安装包 | `/sdcard/Download/Note_v1.0.13.apk` |
| 版本定义位置 | `Note.csproj` |

版本号由 `tools/deploy.ps1` 在每次部署时自动递增（patch 与 version code 各 +1），脚本使用 UTF-8 无 BOM 读写 `Note.csproj`。

---

## 📁 目录结构

```
Note/
├── backups/
│   └── Note_previous.apk
├── docs/
│   └── appicon.png
├── Models/
│   ├── DocumentRecord.cs
│   └── NoteItem.cs
├── Platforms/
│   └── Android/
│       ├── Resources/
│       ├── AndroidManifest.xml
│       ├── MainActivity.cs
│       └── MainApplication.cs
├── Properties/
│   └── launchSettings.json
├── Resources/
│   ├── AppIcon/
│   │   ├── appicon.png
│   │   └── dl_icons8.png
│   ├── Fonts/
│   │   ├── OpenSans-Regular.ttf
│   │   └── OpenSans-Semibold.ttf
│   ├── Images/
│   ├── Raw/
│   │   └── AboutAssets.txt
│   └── Styles/
│       ├── Colors.xaml
│       └── Styles.xaml
├── Services/
│   ├── AppState.cs
│   ├── ChapterInfo.cs
│   ├── DocumentOpener.cs
│   ├── DocumentReader.cs
│   ├── HistoryStore.cs
│   ├── NoteImages.cs
│   ├── NoteStore.cs
│   └── OpenProgress.cs
├── tools/
│   ├── build.log
│   ├── check.log
│   ├── deploy.ps1
│   └── rollback.ps1
├── .gitignore
├── App.xaml
├── App.xaml.cs
├── AppShell.xaml
└── …（其余 16 项未列出）
```

## 🛠️ 环境要求

- 运行时：.NET 10.0（Android）



## 更新日志

### v1.0.13 (2026-11-11)

- 主页「导入文件」改为「打开新文件」，文件页移除重复的「打开新文件」按钮
- 便签编辑页去掉「保存」按钮（返回自动保存）
- 便签支持插入图片、缩略图预览与移除；删除便签自动清理图片
- 每条便签可单独设置背景色、字号、粗体与斜体
- 新增「复制」按钮，一键复制正文并以顶部状态位提示，不弹窗

### v1.0.12 (13)

- 自动滑动期间保持屏幕常亮；停止自动滑动或离开阅读页时释放常亮

### v1.0.11 (12)

- 阅读页新增左右区域点击翻页、水平滑动翻页；中间点击不翻页
- 中间点击可选自动滑动速度（行/秒），自动滑动中点击中间停止
- 滚到当页底部自动切换下一页；首页 / 末页屏蔽对应方向点击
- 预览 HTML 增加模板版本号，模板升级时旧缓存自动重建

### v1.0.10 (11)

- 修复外部打开时进度遮罩挂在后台页面导致看不到的问题，改为挂到当前可见页面

### v1.0.9 (10)

- 用内容指纹统一文档身份：重复打开同一文档不再新增浏览记录或预览缓存
- 外部打开 / 文件选择器副本规范化，`file://` 直接读原文件，不再复制
- 大文档首次打开显示解析进度，耗时解析放到后台线程，缓存命中秒开

### v1.0.8 (9)

- 主界面改为「主页 / 文件」两个底部标签，文件页为浏览记录
- 大文档自动生成目录，按章节整章分页并显示章节字符数
- 新增文档阅读位置记忆与自动恢复
- 部署脚本增加手机端只保留当前版本 APK，并修复 csproj 中文被转码破坏的问题

### v1.0.7 (8)

- 基线版本：便签增删改查与搜索、手机存储文件浏览、大文档流式读取与分页预览、外部文件打开意图、一键部署与回滚脚本

---

## 图标

| 资源 | 用途 |
| --- | --- |
| `Resources/AppIcon/appicon.png` | 应用图标（`MauiIcon`）与启动页（`MauiSplashScreen`，`BaseSize=128,128`） |
| `Resources/AppIcon/dl_icons8.png` | 原始图标素材，来自 [icons8](https://icons8.com) |
| `Resources/Images/` | 预留目录，当前为空 |
| `Resources/Fonts/OpenSans-*.ttf` | 界面字体 OpenSans Regular / Semibold |

图标与启动页底色为 `#FFF6DE`（米黄），主题强调色为 `#FFB000`（橙），正文色 `#1E2126`。MAUI 在构建时由 `appicon.png` 生成各密度 `mipmap` 资源，`AndroidManifest.xml` 通过 `@mipmap/appicon` 与 `@mipmap/appicon_round` 引用。

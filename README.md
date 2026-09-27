# 素材库 MediaLibrary

面向学校视频剪辑工作的本地素材管理软件（Windows 桌面应用）。

拖入视频/照片/音频 → 自动**复制**进素材库、统一命名、视频转 H.265（NVENC 硬件加速）、照片转 PNG、音频原样复制 —— 原始文件保持不动。

## ✨ 功能

- **缩略图网格**：打开即见，虚拟化滚动不卡顿
- **悬浮预览**：视频静音播放 + 进度条刮擦（LibVLC），音频自动播放，照片放大
- **框选多选**：空白处拖动即框选，拖出边界自动滚动（向下），纯累加（扫过即选中），Ctrl+A / 反选 / 点击空白取消
- **批量操作**：重命名（模板 + 连续序号）、重新转码、删除到回收站
- **任务队列**：转码/转换/复制全后台，可暂停/继续/取消/重试，重启自动续跑
- **拖出导出**：从卡片按住拖动把文件复制到资源管理器/其他程序（多选批量导出）
- **文件夹管理**：树形导航 + 右键新建/重命名/删除文件夹
- **搜索 + 排序**：按文件名过滤、按名称/大小/时长排序
- **设置**：存储位置切换、右键菜单自定义、悬浮预览开关与延迟、磁盘预警阈值
- **安装包**：Inno Setup 自包含（目标电脑无需安装 .NET）

## 🛠 技术栈

.NET 10 (WPF) · LibVLCSharp 3.10 · FFmpeg 8.1 (hevc_nvenc) · SQLite (Dapper) · VirtualizingWrapPanel

## 📦 构建

```powershell
# 准备 ffmpeg（仓库不含二进制）：下载 BtbN win64-gpl 8.1，解压 ffmpeg.exe / ffprobe.exe 到 tools\ffmpeg\
# 构建（框架依赖，开发用）
dotnet build src\MediaLibrary.App\MediaLibrary.App.csproj -c Release

# 安装包（自包含 x64，先安装 Inno Setup：winget install JRSoftware.InnoSetup）
pwsh installer\build-installer.ps1
```

## 🧪 验收与诊断

```powershell
# 单元测试
dotnet test tests\MediaLibrary.Core.Tests

# 冒烟测试（导入管线 / 队列控制）
dotnet run --project tests\MediaLibrary.SmokeTest -c Release -- phase1 <库目录>
dotnet run --project tests\MediaLibrary.SmokeTest -c Release -- phase2 <源目录> <目标库>
dotnet run --project tests\MediaLibrary.SmokeTest -c Release -- phase4 <源目录> <目标库>

# 应用内诊断（无人值守，用后即退；MEDIALIBRARY_LIBRARY_ROOT 可指向临时库）
$env:MEDIALIBRARY_DIAG_HOVER="1"    # 悬浮预览 26 项（含真实鼠标）
$env:MEDIALIBRARY_DIAG_JOBS="1"     # 队列面板/框选 21 项
$env:MEDIALIBRARY_DIAG_BATCH="1"    # 批量操作端到端
$env:MEDIALIBRARY_DIAG_THUMBS="1"   # 缩略图补全修复
```

## 📄 文档

- `docs/技术设计文档.md` —— 架构与各阶段设计
- `docs/阶段0~4验收报告.md` —— 每个阶段的验收记录与踩坑修复

## ⚖️ 许可

MIT。FFmpeg / libvlc 为第三方组件，构建产物遵循各自许可（GPL 构建仅校内自用，见设计文档风险表）。

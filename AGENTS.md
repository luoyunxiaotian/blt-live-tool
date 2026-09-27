# 直播小帮手 · 开发与发布全局铁律 (AGENTS.md)

> 本文件是本工作区所有 AI 代理（包括 Antigravity、Gemini、Claude 等）在处理本项目时的最高优先级强制规范。
> **每次涉及编译、打包、测试和发布时，必须无条件严格遵循以下铁律！**

---

## 🚨 核心铁律一：MAUI 客户端发布必须 100% 独立运行库模式（Self-Contained）

### 历史事故警示
- **教训**：因 `dotnet publish` 遗漏自包含参数，曾**两次**导致打包产物降级为框架依赖模式（Framework-Dependent），缺失 `coreclr.dll`、`clrjit.dll` 等 330 多个 .NET 10 底层核心组件（文件数从 ~808 锐减至 ~478）。用户解压或安装后双击 `直播小帮手.exe` 时，系统弹窗强制要求下载安装 `.NET 10.0 Desktop Runtime`，严重破坏用户开箱即用体验。
- **严正禁止**：**严禁任何形式的框架依赖编译发布！严禁打包缺少 `coreclr.dll` 的产物！**

### 强制标准操作规程 (SOP)
1. **统一编译发布命令**：
   编译 MAUI 客户端时，**必须且只能**携带 `-r win-x64 --self-contained true -p:WindowsPackageType=None` 参数：
   ```powershell
   dotnet publish "BiLi_live_Tool\BiLi_live_Tool\BiLi_live_Tool.csproj" -f net10.0-windows10.0.19041.0 -c Release -r win-x64 --self-contained true -p:WindowsPackageType=None -o <publish_dir>
   ```
   **强烈推荐直接调用官方构建脚本**：
   ```powershell
   python ../tools/publish-app.py <version>
   ```
2. **硬门禁防护检查（全流程自动化拦截）**：
   - `publish-app.py`、`make-package.py`、`make-release.py`、`make-installer.py` 均已内置硬拦截代码。
   - 检查标准：
     - 必须包含 `coreclr.dll`（大小 > 4 MB）。
     - 必须包含 `clrjit.dll`。
     - 打包文件总数必须 **>= 800** 项（框架依赖仅约 470 项，一旦触发立刻终止执行并报错退出）。
3. **本地启动实测验证**：
   打包完成后，必须在发布目录直接拉起 `直播小帮手.exe`，确认进程秒级启动且无任何运行库弹窗。测试完成后立即清理测试生成的 `data\`、`*.WebView2\` 与 `config.json`。

---

## 🚨 核心铁律二：发布产物脱敏与隐私安全绝对零容忍

1. **发布前脱敏扫描**：
   在打包或上传 GitHub 之前，必须执行：
   ```powershell
   python ../tools/sanitize-scan.py <release_dir_or_zip>
   ```
   必须确保 **0 敏感信息命中（100% PASS）**。
2. **严防测试残留打包**：
   - 严禁打包以下敏感文件和缓存：`data/`（用户配置与历史）、`config.json`（测试配置与 Cookie）、`*.WebView2/`（浏览器登录与缓存目录）、`*.log`、`*.pdb`。
   - `make-installer.py` 和 `make-package.py` 会自动执行 `purge_runtime_dirs()`，但代理在执行打包前仍需主动检查与确认。

---

## 🚨 核心铁律三：发布更新完整闭环流线

每个版本发布必须按顺序严格执行以下闭环：
1. **代码与版本号就绪**：在 `BiLi_live_Tool.csproj` 中更新版本号（如 `0.1.9`）。
2. **独立运行库编译**：`python tools/publish-app.py <version>`
3. **打包全量 zip 与清理脚本**：`python tools/make-package.py <version> <publish_dir> <out_dir> --prev-manifest <prev_manifest>`
4. **生成清单与增量补丁**：`python tools/make-release.py --to <out_dir>/pkg --version <version> --from-manifest <prev_manifest> --out <out_dir> --full-zip <full_zip_path>`
5. **编译安装包**：`python tools/make-installer.py <version> <out_dir>/pkg <out_dir>`
6. **脱敏扫描与实机回归**：`python tools/sanitize-scan.py <out_dir>` 并实测运行 `直播小帮手.exe`。
7. **GitHub Release 发布与资产同步**：`python tools/make-github-release.py upload|replace <version> <out_dir>`。
8. **看板记录与 Git 提交**：更新 `AgentWork.md`，同步 git 提交与远端推送。

# 巡巡 XunXun

巡巡是一个 Windows 桌宠。它会在桌面上散步、运动、跟你互动，也能把 Claude Code、Codex CLI 和 T3 Code 的任务提醒放到桌面上。

![巡巡的审批、提问和完成气泡](preview/xunxun-preview.png)

## 先跑起来

巡巡使用 .NET 9 和 WPF。当前构建产物是需要桌面运行时的 Windows x64 单文件程序。

- **已有 `XunXun.exe`**。安装 [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) 的 Windows x64 版本。若收到的是 ZIP，先把 EXE 解压到常用目录，再双击运行。
- **从源码构建**。在 Windows 上安装 .NET 9 SDK，克隆仓库，在仓库根目录运行下面的命令。构建完成后双击 `release\XunXun.exe`。

```powershell
git clone https://github.com/xiaoaf45/xunxun-desktop-pet.git
cd xunxun-desktop-pet
dotnet publish .\src\XunXun.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o .\release
```

重新构建前可以用右键菜单的 **退出巡巡** 关闭正在运行的同一份 EXE。

双击启动后，巡巡会自由漫游并监听已配置的任务提醒。想快速体验气泡，右键巡巡或系统托盘图标，选择 **播放提醒演示**。演示依次展示审批、提问和完成。点 **回到任务** 可进入模拟任务窗口，也可以直接用以下命令启动演示。

```powershell
Start-Process .\release\XunXun.exe -ArgumentList '--demo'
```

## 桌面互动

- **漫游和运动**。巡巡会随机散步，轮流小跳、拍球运球和踢球，也会挑相邻屏幕当目的地。
- **遇到桌面图标**。有空间时会轻轻绕开单个图标。图标密集或正在追鼠标时会缩小穿过，离开图标区后恢复原大小。
- **追鼠标和摸头**。在它附近移动指针，它会凑过来，也能沿相邻屏幕的公共边缘追过去。指针移到它身上会变成一只小手。单击摸头，来回滑动挠痒。
- **拖动和击掌**。按住拖动时它会扭身、晃耳朵、蹬腿，松手后继续漫游。悬停一会儿，等它伸爪再单击可以击掌。小手光标会随爪子抬起，手动移动鼠标可以放开。
- **喂食、抛球、跳舞和小睡**。把鼠标移到它身上即可看到按钮。睡着后点击巡巡或点 **叫醒**，它会重新站起来。

[查看动作预览](preview/xunxun-play.png)

## 任务提醒

任务进行中，巡巡照常漫游。需要提问、审批或任务完成时，气泡会停在弹出的位置，巡巡可以继续走。提醒逐条显示，每条从 10 秒开始倒数，可以提前点 × 收起。用右键菜单里的 **免打扰** 可暂时隐藏气泡。

| 工具 | 结构化提问 | 待审批 | 完成 | 气泡入口 |
| --- | --- | --- | --- | --- |
| Claude Code | `AskUserQuestion` 和 MCP 结构化提问 | 工具权限请求 | 本轮回复结束 | 已绑定的终端窗口 |
| Codex CLI | — | 工具权限请求 | 本轮回复结束 | 已绑定的终端窗口 |
| T3 Code | — | — | 主会话和后台工作结束 | 优先打开桌面端对应会话 |

Claude Code 和 Codex 的完成提醒来自 `Stop` Hook。它表示当前一轮回复结束，任务结果可回到原会话查看。工具审批仍由你在原工具里处理。

### 从巡巡启动 Claude Code 或 Codex CLI

双击巡巡，或从巡巡和托盘的右键菜单选择 **启动 Claude / Codex**。选一个已安装的工具，再输入、浏览或从下拉列表选取项目路径。启动过的最近 12 个路径会记在本机，再次打开面板时默认填入上一次的路径。

每次启动会打开一个独立的 PowerShell 窗口，从所选目录运行 CLI。巡巡优先使用 Windows Terminal 显示 PowerShell，并启用 UTF-8。找不到 Windows Terminal 时使用 Windows 自带控制台。连接好对应 Hook 后，点击任务气泡中的 **切到任务窗口** 可回到该会话。CLI 退出后，PowerShell 会保留供你查看结果。

**无需审批**默认关闭，关闭时沿用工具设置。勾选后仅本次会话生效。Claude Code 使用 `--permission-mode bypassPermissions`，Codex CLI 使用 `--ask-for-approval never` 并沿用现有沙箱权限。项目历史只记录路径。

### Claude Code 和 Codex CLI

1. 在 Windows 上安装对应的 CLI，并启动巡巡。巡巡会检测已安装的 CLI，列出缺少或需要迁移的提醒 Hook。
2. 在弹窗中勾选工具，再点击 **注册所选 Hook**。选择 **暂不接入** 后，可以用巡巡或托盘的右键菜单 **设置任务提醒** 再次打开。
3. 重新打开 Claude Code 或 Codex 会话。Codex 还需在 `/hooks` 中审查并信任巡巡的 Hook。

自动注册会合并巡巡的条目，保留已有的其他 Hook，并在原配置目录留一份修改前的备份。同一事件只保留一条由巡巡注册的 Hook。Claude Code 使用明文 PowerShell 命令，从当前用户的固定目录启动接收程序，因此把桌宠移动到其他目录后也能继续使用。

| 内容 | 默认位置 |
| --- | --- |
| Claude Code 用户配置 | `%USERPROFILE%\.claude\settings.json`，可由 `CLAUDE_CONFIG_DIR` 指定目录 |
| Codex CLI 用户 Hook | `%USERPROFILE%\.codex\hooks.json`，可由 `CODEX_HOME` 指定目录 |
| 巡巡的固定接收程序 | `%LOCALAPPDATA%\XunXun\HookRelay\XunXun.exe` |

[Claude Code Hooks](https://code.claude.com/docs/en/hooks) · [Codex Hooks](https://learn.chatgpt.com/docs/hooks)

### 手动配置和终端绑定

在仓库根目录运行脚本生成 Claude Code 和 Codex 的样例，输出在 `adapters/examples/`。样例引用指定 EXE 的绝对路径，移动程序后可重新生成。

```powershell
.\adapters\New-HookExamples.ps1 -Executable .\release\XunXun.exe
```

- **Claude Code**。从目标终端运行 `.\adapters\Start-Harness.ps1 -Provider claude`，以 `--settings` 加载样例。手动样例需要支持 `shell: powershell` 的 Claude Code 版本。
- **Codex CLI**。将生成的 `codex-hooks.json` 中的条目合并进受信任项目的 `.codex/hooks.json`，再从目标终端运行 `.\adapters\Start-Harness.ps1 -Provider codex`，在 `/hooks` 中审查并信任。

手动启动脚本会记录当前窗口，使气泡显示可用的 **切到任务窗口** 按钮。多个 Windows Terminal 标签页共用一个窗口时，进入窗口后可手动切到相应标签页。已使用用户级自动注册时，可直接从对应 CLI 接收提醒，生成样例适用于有手动配置需求的场景。

### T3 Code

确保当前 Windows 用户能运行 `t3` CLI。在 `XunXun.exe` 同目录创建 `t3-projects.json`，写入要关注的项目 ID 数组，再重启巡巡。

```json
[
  "00000000-0000-0000-0000-000000000001"
]
```

将示例值换成实际项目 ID。可以先用 `t3 thread list --json --project <项目 ID>` 确认 CLI 能读取对应项目。

巡巡会轮询配置中的项目，启动时先记录已有状态，后续在主会话及后台工作结束时提醒一次。点击 **回到 T3 Code** 会优先通过 `t3code://` 打开桌面端的对应会话。Windows 找不到桌面端协议或无法启动时，会打开会话网页。

## 排查提醒

- **旧版配置指向压缩包临时目录**。退出旧巡巡，把新版 EXE 解压到常用目录再启动。确认一次迁移后，配置会改为固定接收程序的路径，并合并重复的巡巡条目。
- **Claude Code 未收到提醒**。检查实际启动 CLI 的 Windows 用户与巡巡是否处在同一会话，确认 CLI 使用的配置目录与注册窗口显示的目录一致，重开 CLI 后再试。
- **Codex Hook 已写入**。重开 Codex，在 `/hooks` 查看巡巡条目及其信任状态。
- **T3 Code 未收到提醒**。核对 `t3-projects.json` 与 EXE 同目录，使用实际项目 ID，并在终端确认 `t3 thread list` 能读到该项目。

Claude Code 和 Codex 的 Hook 通过当前用户和会话的 Windows 命名管道转发会话 ID、事件类型、工作目录末级名称和窗口绑定信息。T3 Code 的状态来自本机 CLI。

## 项目结构和自测

| 路径 | 用途 |
| --- | --- |
| [`src/`](src) | WPF 桌宠、动作绘制、窗口与气泡、Hook 注册和接收、T3 Code 完成提醒 |
| [`src/Assets/`](src/Assets) | 应用图标和鼠标指针资源 |
| [`adapters/`](adapters) | Hook 样例生成和终端启动脚本 |
| [`preview/`](preview) | 自测生成的桌宠动作和模拟提醒预览 |

构建后可运行内置自测，报告及预览图会写到指定目录。

```powershell
$testDir = Join-Path (Get-Location).Path 'src\obj\xunxun-self-test'
$result = Start-Process -FilePath .\release\XunXun.exe -ArgumentList @(
    '--self-test', ('"{0}"' -f $testDir)
) -Wait -PassThru
Get-Content -LiteralPath (Join-Path $testDir 'self-test.txt') -Encoding UTF8
if ($result.ExitCode -ne 0) { throw '巡巡自测失败。' }
```

自测覆盖 Hook 注册与迁移、事件转发、动作轮换、桌面图标避让和双屏路线。双屏窗口切换等依赖本机桌面环境的检查会在报告里单独说明。

## 许可证

本项目采用 [MIT 许可证](LICENSE)。

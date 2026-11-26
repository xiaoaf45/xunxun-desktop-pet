# 巡巡 · Windows 桌宠原型

在 Windows 上按文末命令构建后，双击 `release\XunXun.exe` 即可开始漫游并监听已配置的任务提醒。这个 x64 单文件版需要 .NET 9 Desktop Runtime。

1. 右键巡巡或托盘图标，选择 **播放提醒演示**，约两秒后会弹出模拟审批。也可以用 `--demo` 启动演示。
2. 点击 **回到任务** 可打开模拟任务窗口。窗口里的 **下一段演示** 或气泡上的 × 可依次进入提问和完成提醒。
3. 巡巡会随机游走，轮流小跳、拍球运球和踢球，也会挑相邻屏幕当目的地。遇到单个图标时，路线允许就轻轻绕一下。图标密集时会缩小，继续沿自己的方向散步。右键宠物或托盘图标，可以重播、切换免打扰或退出。

## 和巡巡玩

- **单击巡巡**可以摸头，它会眯眼、摇尾巴并冒爱心。**按住拖动**时它会扭身、晃耳朵、蹬脚，松手后继续漫游。
- **在它附近移动鼠标**，巡巡闲着时会主动追过来，可以沿相邻显示器的公共边缘跨屏。经过图标会缩小穿过，接近指针后停下。指针移到它身上会变成一只小手。**来回滑动**可以挠痒。**停一会儿**，等巡巡伸爪，再点击就能击掌。击掌时小手会跟着爪子抬起，手动移动鼠标即可放开。
- **喂食**会丢出一根胡萝卜，巡巡会接住嚼两口。**抛球**会让它跑去接球再回来。**跳舞**会让它踩着节拍蹦跳。
- **小睡**会让它趴下打盹。再点一下巡巡，或点 **叫醒**，它会重新站起来。

鼠标移到宠物身上即可呼出按钮。这些动作也可以在有任务提醒时使用。[互动预览](preview/xunxun-play.png)

[审批、提问和完成提醒预览](preview/xunxun-preview.png)由原型界面直接渲染。演示窗口、动作和工作区名称都是模拟数据。

气泡停在弹出的位置，巡巡继续漫游。每条提醒显示 10 秒倒计时，到时自动收起。多条提醒依次显示。

## 接入本机 CLI

巡巡启动时查找 Claude Code 和 Codex 的 CLI 启动器。已安装且缺失提醒 Hook 的工具会列在弹窗里，可以勾选后注册。注册会在原配置目录保留修改前的备份。Codex CLI 还需重开会话，在 `/hooks` 审查并信任巡巡的 Hook。Claude Code 可提醒结构化提问、待审批和本轮结束，Codex CLI 的 Hook 接入目前覆盖待审批和本轮结束。

Claude Code 的 Windows Hook 使用兼容的 PowerShell 启动命令。自动注册会把接收程序保存为 `%LOCALAPPDATA%\XunXun\HookRelay\XunXun.exe`。更新巡巡后，若弹窗提示注册，确认一次即可迁移旧条目并合并同事件的多条巡巡 Hook。每种事件保留一条巡巡 Hook，其他 Hook 会保留。暂不接入的工具可从巡巡右键菜单的 **设置任务提醒** 再次选择。迁移后重开 Claude Code，确保它与巡巡在同一 Windows 用户会话里运行。

双击 `release\XunXun.exe` 可开始监听。若要生成手动配置样例，从原型根目录运行以下命令。样例包含当前可执行文件的绝对路径，移动程序后可重新生成。

```powershell
.\adapters\New-HookExamples.ps1
```

- **Claude Code**。从前台 PowerShell 窗口运行 `.\adapters\Start-Harness.ps1 -Provider claude`。启动脚本通过 CLI 的 `--settings` 带入样例。提问提醒来自 `AskUserQuestion` 和 MCP 结构化提问。[Claude Code Hooks](https://code.claude.com/docs/en/hooks)
- **Codex CLI**。将生成的 `adapters/examples/codex-hooks.json` 条目合并进目标受信任项目的 `.codex/hooks.json`，保留已有条目。通过前台 PowerShell 运行 `.\adapters\Start-Harness.ps1 -Provider codex`，路径按原型位置填写，并在 Codex 的 `/hooks` 审查并信任这些条目。[Codex Hooks](https://learn.chatgpt.com/docs/hooks)

在各 CLI 的独立终端窗口里执行启动脚本，以便提醒可以切回该窗口。Windows Terminal 共用窗口时会切回整个窗口，标签页需要手动选。通过其他方式启动并加载 Hook 时也可以收到提醒，窗口按钮需要启动时绑定。

Hook 用当前用户和 Windows 会话内的命名管道转发会话 ID、事件类型、工作目录末级名称和窗口绑定信息。它不发送模型提示、命令正文和审批决定。收到回复结束事件表示该轮回复结束，任务结果在对应的 CLI 会话里查看。

本机检查通过了模拟 Hook 载荷、原生提示、打开模拟任务窗口和前台切换。真实 CLI 流程需要在启用样例后联调。

## T3 Code 完成提醒

在 `XunXun.exe` 同目录创建 `t3-projects.json`，内容是要关注的 T3 Code 项目 ID 数组，然后重启巡巡。巡巡通过当前 Windows 用户的 `t3 thread list --json` 读取会话状态。启动时记录已有状态，之后在主会话变为完成且后台工作结束时提醒一次。气泡的 **打开 T3 会话** 使用 CLI 返回的会话网页链接。

## 重新构建

```powershell
dotnet publish .\src\XunXun.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o .\release
```

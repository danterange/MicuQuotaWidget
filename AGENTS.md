# MicuQuotaWidget 项目协作规则

## 项目目标与边界

MicuQuotaWidget 是一个 Windows WPF 桌面小组件：以紧凑窗口展示 Micu 当前余额，默认每 10 秒只读刷新一次，并支持用户切换“始终置顶”。

- 主界面只承载余额、查询状态、最近刷新时间、刷新、置顶和设置；不要把它扩展成通用控制台。
- 余额查询逻辑参考公开的 MicuCodeLine / MicuSubCodeLine 源码，但不得启动或解析其 CLI 输出。CLI 没有稳定 JSON 契约，可能进入 TUI 或写入 Claude 配置目录。
- 当前接口路径属于公开源码观察结果，不等于官方稳定 API 契约。修改接口、字段换算或鉴权方式时，必须同步更新 `docs/米醋余额适配器.md`、集成测试和 README。
- API Key 是秘密：不得写入 Git、日志、截图、异常文本或文档示例。仅通过 Windows Credential Manager 保存；测试一律使用模拟值。
- 仅允许 HTTPS 且固定在 Micu 官方域名白名单；不接受任意自定义 HTTP endpoint、Cookie 或脚本执行。
- 10 秒轮询必须防止重叠请求，窗口关闭后必须取消轮询并释放资源；失败时保留上次成功余额并给出明确恢复动作。

## 目录与文档

- `src/MicuQuotaWidget/`：WPF UI、ViewModel、Windows 本地设置与凭据存储。
- `src/MicuQuotaWidget.Core/`：余额查询协议、HTTP 客户端与可测试的领域模型。
- `tests/MicuQuotaWidget.Core.Tests/`：unit test 与 mock HTTP integration test。
- `tests/MicuQuotaWidget.UiTests/`：WPF STA UI smoke test 与渲染契约检查。
- `scripts/`：可重复执行的质量门禁、UI 验证与发布脚本。
- `docs/`：中文技术、测试、发布与适配器文档；专业名称保留英文。
- `design-system/micu-quota-widget/MASTER.md`：小组件视觉基线；UI 修改前先阅读。

## 代码与 UI 规范

- 使用 .NET 8、WPF 和零业务第三方依赖；若引入新依赖，必须说明必要性、许可证、离线构建影响和替代方案。
- 新增或修改的每个方法、构造函数、事件处理器和 enum 均需有说明目的、边界或副作用的中文 XML 注释；不要写逐行复述式注释。
- 使用 MVVM 边界：UI 不直接发网络请求，Core 不依赖 WPF。
- `HttpClient` 由应用生命周期复用；网络调用使用 cancellation token 和短超时；不得 `.Result` 或 `.Wait()` 阻塞 UI 线程。
- 所有图标使用 WPF vector `Path` 或文字标签，禁止 emoji 作为结构性图标。
- 交互控件必须具备可见键盘 focus、`AutomationProperties.Name`、tooltip、禁用/查询/失败状态和键盘路径。主窗口应保持约 300–360px 宽，不产生裁切或横向滚动。
- 改动 UI 后必须运行 UI test、生成并实际查看截图；验证初始、查询中、成功、失败、未配置和置顶/未置顶状态。

## 每次修改后的强制流程

本项目明确要求每次代码、脚本、测试、UI 或发布配置修改后执行完整质量流程。不得以“只是小改动”为由跳过；某一阶段受环境阻塞时必须保留现场并在最终报告中说明。

1. 阅读当前 `AGENTS.md`、相关 `docs/`、实际 `git diff` 和调用链。
2. 同步修改 unit test、integration test、UI test、中文技术文档以及双语 README；UI 可见变化须更新 `docs/images/widget-preview.svg` 或真实演示截图，演示数据必须明确标注。
3. 运行 `powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File .\scripts\Invoke-QualityGate.ps1`。
4. 对所有变更执行 C# / WPF code review：检查异步取消、异常与资源释放、凭据边界、10 秒轮询、窗口置顶、键盘可达性、截图结果、注释和文档一致性。明确安全的问题先修复，再完整复审并重跑相关验证。
5. 质量门禁通过后执行本地 Git 提交；只暂存本次任务拥有的精确路径，先检查 staged diff 和 `git diff --check`，禁止盲目 `git add .`、`--no-verify`、强推或改写历史。
6. 已配置 `origin` 且用户对本次任务授权外部发布时，传入**覆盖本次全部变更且不含任何无关文件**的精确相对路径。例如本项目首次发布可使用 `scripts\Invoke-Delivery.ps1 -Paths @('.editorconfig','.github','.gitignore','AGENTS.md','CHANGELOG.md','Directory.Build.props','LICENSE','MicuQuotaWidget.sln','README.md','design-system','docs','scripts','src','tests') -Commit -Push -Release`；后续任务必须按实际 diff 缩小或调整该列表。脚本自动递增版本、更新 `CHANGELOG.md` / README / `docs/releases/`、推送、打 tag、触发 GitHub Actions 发布 GitHub Release 并校验附件哈希。
7. 若没有 remote、GitHub CLI 认证或目标 Release 仓库，禁止虚构地址、创建陌生远端或绕过认证。完成本地提交后明确报告阻塞条件和可执行下一步。

## 发布定义与脚本约定

- 唯一发布入口是 `scripts/Invoke-Delivery.ps1`；它负责版本更新、质量门禁、Git commit、push、tag、等待 `.github/workflows/release.yml` 创建 GitHub Release 与下载哈希复核。
- `scripts/Invoke-QualityGate.ps1` 必须依次执行 restore、format check、build、unit test、integration test、UI test 和 publish smoke check。
- `scripts/Test-Ui.ps1` 生成不含真实余额与 Key 的演示截图，输出到忽略的 `artifacts/`，并检查窗口大小、关键控件、置顶状态和可访问名称。
- 每次 Release 必须创建 `docs/releases/vX.Y.Z.md`，列出功能、验证、兼容性、升级/回滚说明与 SHA-256。
- 发布完成条件为：本地质量门禁通过、审查通过、Git 工作区干净、远程 push 成功、GitHub Actions 成功、Release 非草稿且 ZIP/哈希附件齐全。仅 `git push` 成功不算完成。

详细步骤见：

- [开发与测试流程](docs/开发与测试流程.md)
- [米醋余额适配器](docs/米醋余额适配器.md)
- [UI 测试](docs/UI测试.md)
- [发布流程](docs/发布流程.md)

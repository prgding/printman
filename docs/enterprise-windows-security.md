# 企业 Windows IPP 修复与验收

本分支基于上游 v2.1.1 的提交 `fb428a0cf0957d4427b450cb90fb4d7eca1d72b9`，用于评估 Windows USB 打印共享。第一阶段修复保留匿名 IPP、自定义端口和现有 Windows 打印驱动。正式启用前仍需完成 Windows 客户端与打印机驱动验收。

## 修复范围与风险

风险等级针对公司内存在恶意或受感染终端、服务账户可以访问本机打印队列的场景。

| 问题 | 原风险 | 本分支处理 | 主要代码 |
| --- | --- | --- | --- |
| 客户端 fileId 进入文件搜索和删除流程 | 高 | 严格校验十六进制 ID，仅访问缓存根目录中的已知格式普通文件，拒绝符号链接及歧义旧文件 | `Services/FileCacheService.cs` |
| 相同内容不同格式共享 ID | 中 | 内容与扩展名共同参与完整 SHA-256，关闭去重时采用完整随机 GUID | `Services/FileCacheService.cs` |
| 网页取消按钮把任务名拼入 JavaScript | 高 | 使用事件监听器传递数据，移除动态内联取消脚本 | `Server/Web/index.html` |
| 匿名客户端查询、取消及给其他客户端任务提交文档 | 中 | 由真实 TCP 对端 IP 关联任务，覆盖列表、详情、取消、Close-Job、Send-Document 和 Cancel-My-Jobs，随机正整数任务编号 | `Services/Ipp/IppRequestHandler.cs`、`Core/Models/IppModels.cs` |
| 并发提交绕过任务上限，上传与取消竞争 | 中 | 全局原子任务准入，单次文档接收声明，提交与取消共享同步锁，超时与中断释放任务状态 | `Services/Ipp/IppJobStore.cs`、`Core/Models/IppModels.cs` |
| 缓存、上传和队列耗尽资源 | 中 | 上传槽位、累计缓存字节预留、队列与批次数量限制，排队文件引用保护，停止时关闭写入并清理队列 | `Services/FileCacheService.cs`、`Services/PrintJobPipeline.cs` |
| 大尺寸或多页文件消耗渲染资源 | 中 | 页数、像素与尺寸检查，光栅索引缓存限制 | `Services/Renderers/DocumentRenderLimits.cs`、各渲染器 |
| 任意打印机名称进入队列操作 | 中 | HTTP API 只接受已枚举队列的准确名称，拒绝 UNC 名称，网络任务流水线采用准确名称匹配 | `Server/PrintingWebServerHost.cs`、`Services/PrintJobPipeline.cs` |
| 网页会话过期失效、PIN 猜测、跨站请求 | 中 | 检查会话有效期、限制会话数量与错误 PIN 次数、固定时间比较、校验 Origin 与 Referer、要求自定义请求头、校验 Host | `Server/WebSessionSecurity.cs` |
| PIN 保存在网址和浏览器持久存储 | 中 | 浏览器通过 HttpOnly 会话 Cookie 工作，去除 URL 身份凭据支持和持久 PIN 存储 | `Server/Web/index.html`、`Server/WebSessionSecurity.cs` |
| 指定 IPP 端口占用后自动改用其他端口 | 中 | 明确启动失败，管理员显式选择可用端口 | `Server/PrintingWebServerHost.cs` |

旧的唯一 16 位十六进制缓存 ID 继续支持。同一旧 ID 对应多个扩展名时需要重新上传。网页自动携带 PIN 的旧书签需要重新输入 PIN，已有 API 客户端可继续使用 PIN 或会话请求头。

本次采用的默认保护值需要结合实际办公文档确认。

- 4 个同时进行的文件缓存上传。
- 50 个活动 IPP 任务，所有共享打印机合计。
- 50 个等待处理的打印批次，每批最多 50 个文件。
- IPP 请求最长 10 分钟，服务器最多 100 个连接，接收请求头最长 30 秒。
- 每份文档最多 1,000 页，单页最多 4,000 万像素，单边最多 20,000 像素，光栅索引最多缓存 64 份文档。
- 网页会话 8 小时有效，最多 256 个会话，整个进程每分钟最多接受 20 次错误 PIN 尝试。
- 原有默认缓存上限 500 MiB、网页文件 50 MiB、IPP 文档 256 MiB。IPP 文档上限同时受缓存配置限制。

## 配置可以控制的风险

公司场景优先使用纯 IPP 共享模式，指定已有 USB 队列、网卡 IP 和 10631。下面的命令是部署候选示例，IP 与队列名称需要替换为测试环境的实际值。

```powershell
.\printman.exe share "USB 打印队列一" "USB 打印队列二" --ip 192.168.10.20 --ipp-port 10631 --no-mdns --cache-limit-mb 500
```

根据启动输出取得每台打印机的实际路径。Windows 系统“添加打印机”中的按 URL 添加流程使用类似 `http://192.168.10.20:10631/ipp/print/<实际队列 slug>` 的地址。各 Windows 版本的 IPP URL 入口、可选 Internet Printing Client 组件及 inbox IPP 驱动选择均纳入验收。

- 主机防火墙只放行 TCP 10631 到指定程序、指定公司网络配置文件及获准客户端网段。由网络管理员按公司流程申请规则。
- `--no-mdns` 关闭本项目的 UDP 5353 广播。固定 IP 或 DHCP 保留地址，优先让客户端直接连接主机 IP。
- 以专用普通账户运行，授予所需队列的打印权限。程序目录由管理员维护，缓存目录只授予运行账户、管理员和 SYSTEM 必要权限。
- 每个安装目录运行一个实例。专用电脑持续供电并禁用工作时段休眠，普通办公电脑注销、睡眠和重启都会影响共享可用性。
- 默认打印后清理缓存，按需设置磁盘加密、受限备份、日志保留期以及 Windows 打印队列的文档保留选项。
- 系统与打印驱动使用当前安全更新，驱动来源经过公司批准。Windows Protected Print Mode 与现有第三方 USB 驱动的兼容性需要单独确认。

本项目从客户端接收打印任务可只开放 TCP 10631。Windows 本地打印子系统与具体驱动的出站行为需要通过防火墙日志或抓包核验，尤其是已有网络队列、驱动自动更新和设备发现功能。

## 仍需接受或进一步解决的风险

| 剩余风险 | 当前判断 | 应对方向 |
| --- | --- | --- |
| 匿名打印产生耗材滥用和队列占用 | 中 | 按用户要求保留，依靠来源网段限制、网络隔离和监控 |
| IP 关联任务无法区分同一主机上的用户或同一 NAT 后的客户端 | 中 | 采用客户端直连和稳定地址，强身份隔离需要以后增加认证 |
| 现有 HTTP IPP 传输明文，文档与网页凭据可被链路上的攻击者观察或篡改 | 中，高敏感文档场景可达高 | 用隔离打印 VLAN、受控路由和源地址规则降低风险，后续评估客户端证书信任及 IPPS |
| Windows PDF、GDI+ 图像解码器与第三方打印驱动处理不可信数据 | 中至高 | 尺寸限制降低部分资源耗尽风险，文件最初解析仍进入系统组件，保持补丁和最小权限，后续考虑可终止的独立渲染进程 |
| 原生渲染或驱动调用长时间阻塞 | 中 | 当前取消机制无法保证中止已进入原生组件的调用，专用机验收包括超时、崩溃和恢复 |
| 文档与任务元数据进入缓存、Windows spool、日志、备份、崩溃转储 | 中 | 默认清理缓存结合目录 ACL、磁盘加密和保留策略。删除文件不提供安全擦除保证 |
| 程序崩溃后任务状态丢失，自动重试可能重复打印 | 中 | 当前 IPP 元数据保存在内存中，重启后的处理需要人工确认，后续设计 spooler job ID 关联及持久化 |
| 强制退出或文件被占用导致缓存残留 | 中 | 下次启动尝试清理，管理员检查目录权限和残留，单实例运行 |
| 网页仍具有共享 PIN 下的管理权限 | 中 | 纯 IPP 部署关闭网页，启用网页时限定管理员来源并保护传输链路 |
| 发布包供应链与代码签名 | 中 | 以审核提交自行构建并记录散列，发布阶段增加不可变版本、构建来源校验与签名 |

## Windows 后台服务评估

当前版本仍按现有交互进程模型运行。专用机无人登录的正式服务支持需要后续实现和 Windows 验收。

Microsoft 文档说明 `System.Drawing`/GDI+ 类型不支持在 Windows 服务中使用。项目 Windows PDF 与图像路径使用这些类型，Session 0 服务包装和计划任务的“无论用户是否登录都运行”都需要面对同一渲染兼容性问题。后续方案应评估直接使用适合服务环境的原生 GDI/WIC 路径，保留现有 IPP 与队列抽象，并为每种 USB 驱动核验权限、用户配置、字体和纸型。服务宿主可评估 Microsoft 官方 WindowsServices 支持包，依赖策略需要在该阶段明确。 [System.Drawing 文档](https://learn.microsoft.com/en-us/dotnet/api/system.drawing?view=windowsdesktop-10.0#remarks) [Windows Service 文档](https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service)

后台化阶段还应实现服务停止与恢复策略、任务接收停止顺序、真实 spooler job ID 关联及重启恢复规则。Windows `StartDocW` 返回的任务标识可用于关联 spooler 状态，实际纸张输出仍受打印机和驱动状态影响。 [StartDocW 文档](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-startdocw)

## 2026 年 11 月 16 日本机验证结果

- macOS arm64 上运行 1,119 项测试，1,117 项通过，2 项 Windows 文件锁测试跳过，失败 0 项。
- 仓库既有统计范围内，行覆盖率为 99.7632%（2,949/2,956），分支覆盖率为 92.4115%（2,192/2,372）。按覆盖率 XML 核验达到原有 99%/92% 门槛。当前 Mac 的覆盖率检查使用等价数值校验，Windows 验收继续运行仓库 PowerShell 检查脚本。
- Windows x64 主程序和测试项目交叉编译通过，Linux x64 主程序构建通过，两者均为 0 警告、0 错误。
- 网页 JavaScript 语法检查通过。真实 Kestrel 回环测试验证了 10631、匿名 IPP、纯共享模式路由隔离、端口冲突退出、认证与队列参数校验。
- 覆盖率沿用仓库排除项，Windows 原生 API、渲染和驱动运行效果由 Windows 环境验收。
- Windows 原生添加打印机、USB 驱动、真实文档渲染、Windows 文件锁和无人登录打印仍待验收。公司网络部署和远程发布处于待评估状态。

## 测试方案

自动测试使用模拟打印对象和本机回环 HTTP 服务。新增测试覆盖路径攻击、旧缓存兼容、符号链接、配额与引用生命周期、并发上传、任务隔离、重复 Send-Document、上传中取消、中断释放、队列满与停机清理、网页认证和 CSRF、未知队列、10631 监听、端口占用和渲染限制。

测试项目新增按平台选择目标框架，通用逻辑可在 macOS/Linux 运行，Windows 继续使用 Windows 目标。构建和测试使用 .NET SDK 10.0.404。覆盖率门槛沿用仓库的 99% 行覆盖率、92% 分支覆盖率。

```powershell
dotnet test Printman.slnx -c Release --coverage --coverage-output-format cobertura --coverage-settings coverage.runsettings --results-directory TestResults
.\eng\check-coverage.ps1
dotnet build src/Printman -c Release -r win-x64
dotnet build src/Printman -c Release -r linux-x64
```

Windows 虚拟机验收按以下顺序进行，测试文件使用虚拟队列和文件输出。

1. 记录 Windows 版本、架构、更新状态、运行账户与 .NET 版本。安装或确认 Microsoft Print to PDF/XPS 虚拟队列。
2. 在虚拟机运行完整自动回归与覆盖率检查。Windows 文件锁测试应运行，符号链接测试当前在 Windows 跳过。
3. 启动虚拟队列共享并设置 `--ipp-port 10631 --no-mdns --output-dir <测试输出目录>`。确认纯共享模式只监听预期地址与端口，网页/API 返回 404。
4. 在另一台 Windows 测试客户端使用系统“添加打印机”，按 IP、10631 和实际路径添加。确认使用系统内置 IPP 驱动，测试 PDF、Office 文档、图片、中文、横向、双面、纸型、份数和页码范围。
5. 两台客户端并发提交，验证 A 无法查看、取消或覆盖 B 的任务。测试客户端断网、服务器重启、端口占用、队列满、磁盘配额和文件占用后的状态与清理。
6. 检查服务主机只接受获准来源的 10631 请求，并核验打印期间封禁端口和其他网卡的实际流量。
7. USB 转接和实际驱动的设备识别可在测试环境检查。实体出纸由用户安排受控人工验收。
8. 普通办公电脑验收锁屏、睡眠、重连和注销后的可用性。专用机的无人登录和服务恢复测试在后台服务实现阶段进行。

GitHub Actions 的 Windows runner 适合持续编译和自动回归。用户的 Windows 虚拟机用于真实客户端、USB 转接、驱动与登录状态验收。远程推送和 Actions 执行等待用户授权。

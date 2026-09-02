# StackPivot Agent 部署说明

本目录只包含 Linux Agent 的 systemd 部署文件。Agent 是 SignalR 客户端，主动通过 `wss://<control-host>/hubs/agent` 连接主控的 443 端口；Agent 不监听入站端口，也不需要公网 IP。

## 一期固定约束

- 仅支持 Linux 主控、Linux Agent 和 Docker Compose v2。
- Agent 工作根目录固定为 `/opt/agent-main`。每个堆栈使用 `/opt/agent-main/{workspace}/{stack}` 下的永久本地 Git 仓库。
- `workspace` 和 `stack` 名称只能匹配 `[a-z0-9_]{1,50}`。不能通过配置传入其他工作目录。
- systemd 服务使用专用非 root 用户 `stackpivot-agent`。Docker 权限必须由主机运维按受控边界提供。
- Agent 只接受指定完整 commit hash（40 或 64 位十六进制），不接受 branch、tag、路径或 shell 片段。
- Agent 只执行参数化的固定 Git 操作和 `docker compose up -d`。一期不提供文件编辑、Git 提交推送、单服务操作或交互终端。
- 应用心跳为 30 秒，SignalR keep-alive 为 20 秒，客户端超时为 60 秒，连接断开后的重连间隔为 5/10/20/30 秒。这些参数由 Agent 应用实现，systemd 不修改它们；部署任务失败不自动重试。
- Agent 必须使用 Linux `memfd_create` 承载一次性 Git askpass。memfd 不可用时必须以失败结果退出，不得回退到普通临时文件、命令行参数或 credential helper。

## 文件和配置边界

unit 假定 .NET 8 Agent 发布文件为 `/opt/stackpivot-agent/StackPivot.Agent.dll`，并从 `/opt/agent-main` 启动。发布文件应由受控发布流程以 root 所有、不可由 Agent 写入的方式安装。

`stackpivot-agent.service` 使用以下配置：

| 配置 | 来源 | 约束 |
| --- | --- | --- |
| `STACKPIVOT_AGENT_ID` | `/etc/stackpivot/agent.env` | 已注册 Agent 的 UUID |
| `STACKPIVOT_CONTROL_HUB_URL` | `/etc/stackpivot/agent.env` | 必须是 `wss://<control-host>/hubs/agent`，只允许主控 443 出站 |
| `StackPivot__AllowedRemoteHosts` | `/etc/stackpivot/agent.env` | 映射到 `StackPivot:AllowedRemoteHosts`；必须是非空、逗号分隔的精确 HTTPS Git 主机名集合，不接受通配符 |
| `STACKPIVOT_AGENT_WORK_ROOT` | systemd unit | 固定为 `/opt/agent-main` |
| `STACKPIVOT_AGENT_API_KEY_FILE` | 固定 ExecStart 环境 | 只能指向 systemd runtime credential 的 `%d/agent-api-key`，不是 key 内容 |

API_KEY 使用 `LoadCredential=agent-api-key:/etc/stackpivot/agent-api-key` 注入。systemd manager 读取 root-only 源文件，再把名为 `agent-api-key` 的 runtime credential 暴露给 `stackpivot-agent`；服务进程只读取 `STACKPIVOT_AGENT_API_KEY_FILE` 指向的 runtime 路径。源文件必须由 root 拥有、权限为 `0600`，服务停止后 runtime credential 会被清理。API_KEY 不得写入仓库、`agent.env`、命令行参数、URL、query string、日志或异常。

`EnvironmentFile=/etc/stackpivot/agent.env` 由 systemd manager 以 root 身份读取，即使其权限为 `0600` 也不会阻止非 root 服务启动；服务进程不直接读取文件。安装时只写入 `STACKPIVOT_AGENT_ID`、`STACKPIVOT_CONTROL_HUB_URL` 和 `StackPivot__AllowedRemoteHosts`。unit 使用 `UnsetEnvironment=STACKPIVOT_AGENT_API_KEY_FILE` 清除 manager 或 `EnvironmentFile` 注入的同名变量，再由固定的 `ExecStart=/usr/bin/env STACKPIVOT_AGENT_API_KEY_FILE=%d/agent-api-key ...` 设置服务子进程环境；因此可编辑的 `EnvironmentFile` 不能覆盖 credential 路径。`%d` 是该服务的 runtime credential 目录。不要在 `agent.env` 中定义 API_KEY、`STACKPIVOT_AGENT_API_KEY_FILE` 或其他 secret。

`StackPivot__AllowedRemoteHosts` 是 .NET 环境变量配置语法，对应应用配置键 `StackPivot:AllowedRemoteHosts`。它必须至少包含一个实际批准的 HTTPS Git 主机名，例如 `git.example.invalid` 仅作为占位格式；安装前必须替换为中央 Git remote 的实际主机。多个主机用逗号分隔，匹配不区分大小写；每项只能是精确主机名或 IP，不得包含 `*`、通配符、协议、端口、路径、用户信息或 token。应用会拒绝非 HTTPS remote 以及不在该集合内的 host。主控侧也必须为 `CentralGit:AllowedRemoteHosts` 配置相同的非空精确主机集合。

不要在本仓库创建真实的 `agent.env` 或 API_KEY 文件。配置文件和 credential 文件都属于目标主机的外部配置。

## 首次安装

以下步骤需要目标 Linux 主机上的受控管理员权限。命令均为固定路径的账户、systemd、凭据或 Docker Compose 操作，不向 Agent 开放任意宿主机 shell。

### 1. 创建运行账号和工作根目录

仅在账号尚不存在时执行：

```text
sudo useradd --system --home-dir /nonexistent --shell /usr/sbin/nologin stackpivot-agent
```

创建固定工作根目录并授予 Agent 写入堆栈目录所需的最小目录权限：

```text
sudo install -d -o stackpivot-agent -g stackpivot-agent -m 0750 /opt/agent-main
```

服务 unit 使用现有的 `docker` 组作为 Docker socket 权限边界。不要在安装文件中创建 Docker group，也不要把 Agent 加入其他主机管理组。Docker group 通常等同于宿主机 root 权限，主机运维必须单独批准并记录这一边界；若主机没有受控的 Docker 权限，服务应保持停止状态。

### 2. 安装 Agent 发布文件

通过现有发布流程把 .NET 8 Agent 安装为：

```text
/opt/stackpivot-agent/StackPivot.Agent.dll
```

发布目录和文件由 root 管理，Agent 用户只能读取。发布流程不得把 API_KEY、Git token 或主机凭据写入该目录。

### 3. 写入非敏感运行配置

先创建配置目录：

```text
sudo install -d -o root -g root -m 0700 /etc/stackpivot
```

从受控输入写入 `/etc/stackpivot/agent.env`，文件权限必须为 `0600`：

```text
sudo install -o root -g root -m 0600 /dev/stdin /etc/stackpivot/agent.env
```

随后输入下面三个字段；`git.example.invalid` 是非敏感的占位主机，必须替换为实际批准的中央 Git HTTPS 主机；这些仅是字段格式，不是可提交的配置样例值：

```text
STACKPIVOT_AGENT_ID=<registered-agent-uuid>
STACKPIVOT_CONTROL_HUB_URL=wss://<control-host>/hubs/agent
StackPivot__AllowedRemoteHosts=git.example.invalid
```

安装完成后确认该文件为 root 所有且权限为 `0600`。`StackPivot__AllowedRemoteHosts` 不得为空，也不得写 `*` 或任意主机通配；应逐项列出中央 Git remote 的 HTTPS 主机。不要在该文件中放 API_KEY；API_KEY 使用下一步的 systemd credential。

### 4. 写入一次性 API_KEY credential

管理员先在主控的 Agent 管理接口创建 Agent，API_KEY 只在创建响应中显示一次。将该值通过受控标准输入写入 root-only 文件，不要把值放入 shell 历史、命令行参数、URL 或日志：

```text
sudo install -o root -g root -m 0600 /dev/stdin /etc/stackpivot/agent-api-key
```

输入完成后结束标准输入。该文件由 unit 的 `LoadCredential` 在服务启动时加载；缺少、不可读或为空时 `LoadCredential`/Agent 必须 fail closed。应用必须只读取 `STACKPIVOT_AGENT_API_KEY_FILE`，不得读取或打印 API_KEY 环境变量。不要为 `LoadCredential` 使用忽略缺失错误的 `-` 前缀，也不得退化到明文临时文件。

### 5. 安装并启动 unit

```text
sudo install -o root -g root -m 0644 deploy/agent/stackpivot-agent.service /etc/systemd/system/stackpivot-agent.service
sudo systemctl daemon-reload
sudo systemctl enable stackpivot-agent.service
sudo systemctl start stackpivot-agent.service
```

unit 缺少配置、credential、工作根目录、.NET 运行时或受控 Docker 权限时应启动失败。memfd 不可用时 Agent 也必须 fail closed；`Restart=on-failure`、`RestartSec=5s`、`TimeoutStopSec=30s` 和 `KillMode=control-group` 只提供受控进程生命周期管理，不提供明文凭据降级路径。

## 升级

1. 先确认主控已经支持当前 Agent 协议 `schemaVersion=1`，并保留 `/opt/agent-main` 下的永久堆栈仓库和非空主机白名单。
2. 使用受控发布流程停止服务、替换 `/opt/stackpivot-agent/StackPivot.Agent.dll`，再启动服务；若 unit 文件有更新，重新安装固定 unit 并执行 `daemon-reload`：

```text
sudo install -o root -g root -m 0644 deploy/agent/stackpivot-agent.service /etc/systemd/system/stackpivot-agent.service
sudo systemctl daemon-reload
sudo systemctl stop stackpivot-agent.service
sudo systemctl start stackpivot-agent.service
```

3. 升级过程中不要修改 `/opt/agent-main` 中的部署内容，不要删除本地 Git 元数据，不要变更 `agent.env`、白名单内容或 credential 的权限。
4. 升级后按下方日志和连接健康检查确认 `agent_connected`、主控 `online` 和最新 `lastSeenAt`。失败只保留失败状态，由用户明确发起新部署；Agent 不自动回滚或重试部署任务。

## API_KEY 首次注册、轮换和吊销

| 操作 | 步骤 |
| --- | --- |
| 首次注册 | 平台管理员创建 Agent，保存一次性 API_KEY 到目标主机的 `/etc/stackpivot/agent-api-key`，确认 `0600` 后启动服务。原始 key 不进入 Git 或工单文本。 |
| 轮换 | 平台管理员调用 Agent key rotate 接口并取得新 key；停止服务，将新 key 写入 `/etc/stackpivot/agent-api-key.next` 后再原子替换正式 credential 文件，最后启动服务。主控会关闭旧连接并记录 key rotation 审计事件。 |
| 吊销 | 平台管理员先调用 revoke 接口，再停止服务并删除目标主机上的 credential 文件。吊销后旧连接不得重新注册；主控记录 revoke 审计事件。 |
| 丢失 | 不恢复或复制旧 key。直接吊销旧版本、创建新版本并按轮换流程安装。 |

轮换或吊销不能把 API_KEY 放入 `systemctl` 参数、`ExecStart`、URL、查询参数、日志或部署任务历史。Git access token 是单次部署凭据，与 Agent API_KEY 分离，不能写入同一日志或配置字段。

轮换时先停止服务，使用固定目标路径写入临时文件，然后原子替换正式 credential：

```text
sudo systemctl stop stackpivot-agent.service
sudo install -o root -g root -m 0600 /dev/stdin /etc/stackpivot/agent-api-key.next
sudo mv -f /etc/stackpivot/agent-api-key.next /etc/stackpivot/agent-api-key
sudo systemctl start stackpivot-agent.service
```

替换完成后按健康检查确认旧 key 已失效、新连接已建立。不要保留 `.next` 文件；若 credential 缺失、不可读或格式非法，服务必须启动失败。

## Docker Compose 和部署行为

启动前 Agent 检测 `docker compose version` 的主版本必须为 2；检测失败时不执行 Git 或 Compose。完整部署执行以下受控流程：

1. 校验任务 schema、Agent ID、完整 commit、过期时间、workspace/stack 名称和 `/opt/agent-main` 子路径。
2. 在固定的永久堆栈目录中以参数化 Git 进程完成 remote 校验、`git fetch --no-tags`、commit 存在性校验和指定 subtree 物化。
3. 关闭并清理 memfd askpass、文件描述符和内存 token 缓冲区。
4. 只在该堆栈根目录执行固定参数 `docker compose up -d`，回传有限、脱敏的 stdout/stderr 和退出码。

Agent 不启动 shell 解释器，不接受任意宿主机命令、用户传入的工作目录或用户传入的 command。Git remote 不含 token，日志至少脱敏本次 Git token、Authorization、X-Agent-Api-Key 以及常见 password/secret/token 键值。`.env` 敏感配置默认不由系统提交，`.secret.env` 只由目标主机外部托管。

## 日志和连接健康检查

检查 unit、进程和最近日志：

```text
sudo systemctl is-enabled stackpivot-agent.service
sudo systemctl is-active stackpivot-agent.service
sudo systemctl show stackpivot-agent.service -p ActiveState -p SubState -p ExecMainStatus
sudo journalctl -u stackpivot-agent.service --since "15 minutes ago" --no-pager
```

核对 unit 的固定运行契约：

```text
sudo systemctl show stackpivot-agent.service -p User -p Group -p WorkingDirectory -p Restart -p RestartUSec -p TimeoutStopUSec -p KillMode -p LoadCredential
```

预期 `User`/`Group` 为 `stackpivot-agent`，`WorkingDirectory` 为 `/opt/agent-main`，`Restart` 为 `on-failure`，重启间隔为 5 秒，停止超时为 30 秒，`KillMode` 为 `control-group`，credential 名称为 `agent-api-key`。不要用 `systemctl set-environment` 或 drop-in 覆盖固定 credential 路径。

健康状态应同时满足：

- unit 为 `active/running`，`ExecMainStatus` 为 0 或进程仍在正常运行；
- 日志只出现脱敏后的连接、心跳和任务状态，不能出现 API_KEY、Git token、Authorization、完整 `.env` 或 credential 文件内容；
- 主控 deployment target 查询显示该 Agent `online=true`，并且 `lastSeenAt` 持续更新；
- Agent 仅有到主控 `wss` 的出站连接。unit 没有 `[Socket]`、`ListenStream` 或任何入站监听声明；主机网络策略只需放行到主控的 TCP 443 出站。

检查 Compose v2 权限和版本：

```text
sudo -u stackpivot-agent docker compose version
```

输出的主版本必须为 `2`。Docker daemon 不可用、socket 权限不受控或 Compose 主版本不是 2 时，修复主机运维配置后再重启服务；不要通过添加任意 Linux capability、开放入站端口或启用任意命令来绕过检查。

## 卸载

先在主控吊销 Agent API_KEY，再停止服务并移除固定 unit：

```text
sudo systemctl disable --now stackpivot-agent.service
sudo rm -f /etc/systemd/system/stackpivot-agent.service
sudo systemctl daemon-reload
sudo rm -f /etc/stackpivot/agent-api-key /etc/stackpivot/agent.env
```

按主机保留策略清理 root 管理的发布文件。不要自动删除 `/opt/agent-main`；其中的永久堆栈仓库和部署数据必须由单独的数据保留审批处理。卸载后确认主控中的 Agent 已吊销，且无新的 `agent_connected` 审计事件。

## 静态检查

在安装目标主机上可执行：

```text
sudo systemd-analyze verify /etc/systemd/system/stackpivot-agent.service
sudo stat -c '%a %U:%G' /etc/stackpivot/agent.env
sudo stat -c '%a %U:%G' /etc/stackpivot/agent-api-key
```

预期 unit 校验无错误；两个配置文件均由 root 拥有，权限分别为 `0600`。仓库内只能出现本 unit 和本 README，不应出现 API_KEY、Git token、主机凭据或真实 `agent.env`。

当前 macOS 工作站无法完成 `systemd-analyze verify`、真实 systemd credential 权限和 `%d` 展开、Linux `memfd_create`、Docker Compose v2 权限以及主控 `wss/443` 连通性验证；这些必须在 Linux 目标主机上实机验证，不能以仓库文本检查替代。

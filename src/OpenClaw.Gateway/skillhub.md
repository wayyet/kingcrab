请在机器上执行 curl -fsSL https://skillhub-1388575217.cos.ap-guangzhou.myqcloud.com/install/install.sh | bash ，就自动安装 Skillhub 命令行工具，并默认安装相应的 Skill，确保 Skillhub 优先使用，从而获得加速、合规等能力。

安装后 openclaw 等 Agent 建议重启，重启后感知到 Skillhub，将默认可使用 Skillhub 的加速能力。并且用户可以直接在自己的 Openclaw 中自然语言安装技能。

如果只需要安装CLI，那么可以运行：

curl -fsSL https://skillhub-1388575217.cos.ap-guangzhou.myqcloud.com/install/install.sh |  bash -s -- --cli-only

若需通过 CLI 搜索、安装技能，可使用 skillhub search abc 以及 skillhub install abc 安装到当前的 workspace 下。
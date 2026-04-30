# memory-session projection contract

这个目录为 `software-developer` 提供第 4 个完整 topic，演示如何在现有 `ontology_extraction` producer 下把 memory / session 领域落成真实可绑定的 projection 主题。

当前主文件：

- `memory-session.domain-model.projection.json`
- `memory-session.json-schema.projection.json`
- `memory-session.prompt-constraint.projection.json`
- `memory-session.workflow-contract.projection.json`

当前目录展示了 memory / session 主题下的四种 target view：

- `domain-model`：面向 session、memory store、retention policy 等实现对象
- `json-schema`：面向会话持久化载荷、检索请求与配置边界的结构校验
- `prompt-constraint`：面向 recall boundary、clarification policy 与术语约束
- `workflow-contract`：面向 recall、retention sweep 与 session lifecycle 的步骤流

当前目录的定位是“完整主题样例”，而不是只够跑通路由的最小骨架。

推荐读取顺序：

1. 先按请求产物形态选读对应 projection 文件
2. 再读 `REVIEW.md` 看当前治理结论
3. 如需扩展或重排 view 优先级，再同步修改 `contract-index.json` 的评分和路由定义

# 配置文件治理

## 评估配置文件

插件使用外部 JSON 配置文件管理沙箱端点和评估参数：

```json
{
  "endpoints": {
    "generator": {
      "wsUrl": "ws://generator:8080/chat",
      "authToken": "env:SANDBOX_AUTH_TOKEN",
      "requestTimeoutSeconds": 120
    },
    "target": {
      "wsUrl": "ws://target-sandbox:9090/chat",
      "requestTimeoutSeconds": 300
    },
    "trace": {
      "wsUrl": "ws://trace-reader:7070/chat"
    },
    "ontology": {
      "wsUrl": "ws://ontology-kb:6060/chat"
    },
    "evalReport": {
      "wsUrl": "ws://report-gen:5050/chat"
    }
  },
  "evaluation": {
    "maxTestcases": 50,
    "enableDualValidation": false,
    "defaultDimensions": [
      "功能完整性",
      "交互质量",
      "响应准确性",
      "效率性能"
    ]
  }
}
```

## 凭据安全规则

1. **绝不硬编码** — AuthToken 必须使用 `env:VAR_NAME` 引用
2. **绝不提交** — 包含 `raw:` 明文凭据的配置文件绝不提交到版本控制
3. **绝不输出** — 评估报告、日志、trace 中绝不出现凭据原文
4. **范围最小化** — 每个端点使用独立的凭据，不使用共享凭据

## 配置位置

- 默认路径：`./evaluation-config.json`（插件根目录）
- 可通过 `-ConfigPath` 参数指定自定义路径
- 环境变量中的值优先级高于配置文件：
  - `GENERATOR_WS_URL` > 配置文件 `endpoints.generator.wsUrl`
  - `TARGET_WS_URL` > 配置文件 `endpoints.target.wsUrl`
  - 以此类推

## 配置修改原则

1. 修改端点地址需经过网络连通性验证
2. 修改超时参数需考虑沙箱的典型响应时间
3. 修改评分维度需与 `ontology` 沙箱保持一致
4. 每次配置修改后运行一次最小化测试确保连通性

# 评估报告格式

## 报告结构

```json
{
  "report_id": "EVAL-20260509-001",
  "evaluated_at": "2026-05-09T10:30:00Z",
  "target_endpoint": "ws://target-sandbox:9090/chat",
  "scores": [
    {
      "dimension": "功能完整性",
      "score": 85,
      "max_score": 100,
      "comment": "所有核心功能点正确实现..."
    }
  ],
  "total_score": 325,
  "max_possible_score": 400,
  "overall_rating": "良好",
  "strengths": ["强项1", "强项2"],
  "weaknesses": ["弱项1", "弱项2"],
  "suggestions": [
    {
      "area": "错误处理",
      "suggestion": "建议增加输入验证...",
      "priority": "high"
    }
  ],
  "summary": "综合评估结论..."
}
```

## 字段说明

| 字段 | 必需 | 说明 |
|------|------|------|
| `report_id` | 是 | 唯一报告标识 |
| `evaluated_at` | 是 | 评估时间 (ISO 8601) |
| `target_endpoint` | 是 | 被评估沙箱端点地址 |
| `scores` | 是 | 各维度评分数组 |
| `total_score` | 是 | 总分（各维度分数之和） |
| `max_possible_score` | 是 | 满分 |
| `overall_rating` | 是 | 综合评级 (A/B/C/D/F) |
| `strengths` | 否 | 强项列表 |
| `weaknesses` | 否 | 弱项列表 |
| `suggestions` | 否 | 改进建议数组 |
| `summary` | 否 | 综合评估结论摘要 |

## 评分维度条目

```json
{
  "dimension": "维度名称",
  "score": 85,
  "max_score": 100,
  "comment": "评分依据和说明"
}
```

## 改进建议条目

```json
{
  "area": "问题领域",
  "suggestion": "具体建议内容",
  "priority": "high|medium|low"
}
```

## 报告文件名规范

`evaluation-{YYYYMMDD}-{HHmmss}.json`

示例：`evaluation-20260509-103000.json`

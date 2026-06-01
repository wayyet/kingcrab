# STEP 9 — buildOverallReport (LLM synthesis, dual-format output)

**Kind**: LLM synthesis (prose only, exactly once)
**Authority**: workflow contract `S9` + K4 + K6 + K7 + K11 + scoring-judgement K5 (`AllIssuesMustBeReported`)
**Inputs**: `evaluation_context`, STEP 5/6/7 artifacts, all ScenarioReport files, `evaluation_report.schema.json`
**Outputs**: two files (see below)

## Two output files

| File | Path | Purpose |
|---|---|---|
| JSON | `./runs/<eval_id>/reports/evaluation_report.json` | Machine-readable, validated against `evaluation_report.schema.json` |
| HTML | `./runs/<eval_id>/reports/evaluation_report.html` | Human-readable, self-contained single-file report |

## Numeric fields are byte-copies (K7)

`dimension_scores` / `overall_score` / `red_line` / `passed` MUST be byte-identical to STEP 5 / 6 / 7 outputs:

| EvaluationReport field | Source file |
|---|---|
| `per_metric_final_scores` | `aggregated_metric_scores.json` |
| `dimension_scores` | `dimension_scores.json` |
| `red_line` (incl. `triggered`, `evidence`) | `red_line_check.json` |
| `overall_score` | `dimension_scores.json` (weighted) |
| `passed` | derived deterministically (see passing criteria below) |

The LLM is allowed to author **only**:

- `executive_summary`
- `strengths`
- `weaknesses`
- `cross_scenario_patterns`
- `improvement_plan`
- `open_questions`

Any LLM-authored value that contradicts the byte-copied numbers is a K7 violation; the report MUST be regenerated.

## Pass/fail derivation

```
passed = (red_line.triggered == false)
         AND (overall_score >= 70)
         AND (∀d ∈ dimension_scores: d.value >= 60)
```

These thresholds are the customer-service-ecommerce defaults; per-template overrides may be declared in the relevant workflow-contract projection.

## Open questions surface (K11 + K16)

`EvaluationReport.open_questions[]` MUST contain entries for:

- every Tier-2 case (`provenance.reliability == "low"`) → caveat `synthesized_from_sop_only_no_user_grounding` (K11)
- every K-rule violation that tainted the run (K8 / K9 / K10 / K12 / K13 / K14 / K16) → severity `critical`
- every duplicate-`scored_at` pair found at STEP 5 input gate (K16) → severity `critical`
- every rejected trace (K14) → list affected `tc_id`s
- missing user consultation when `test_case_status == "missing"` was hit (K11)

Language for Tier-2 / tainted findings MUST be downgraded: use "indicative" / "preliminary" rather than "definitive".

## scenario_report inclusion (K6)

STEP 9 MUST link to `./runs/<eval_id>/reports/scenarios/<tc_id>.report.json` files. **MUST NOT inline them.** STEP 9 also MUST NOT begin until every applicable scenario has a ScenarioReport file.

## HTML generation procedure

1. Load the template at `./runtime-schemas/report-template.html`.
2. Collect all scenario data: for each test case, gather `{ report: <scenario .report.json>, trace: <.trace.json>, enriched: <enriched-case .json> }`.
3. Replace placeholders in the template:

   | Placeholder | Replacement | Notes |
   |---|---|---|
   | `{{REPORT_DATA}}` | full `evaluation_report.json` content as a JSON string | drives the radar chart and headline numbers |
   | `{{SCENARIOS_DATA}}` | array of scenario objects as JSON string | one Tab per scenario |
   | `{{EMPLOYEE_NAME}}` | employee display name | inside `<title>` and the page header |

4. Write the final HTML to `./runs/<eval_id>/reports/evaluation_report.html`.

These three placeholders are **a contract**. If you change the template, keep the placeholder names stable, or update this playbook + `runtime-schemas/report-template.html` together.

## HTML report features

- **能力雷达图**: 5 维度能力覆盖范围，同心圆参考线（0/20/40/60/80/100），灰色虚线目标值（85分），维度标签外置并注明权重
- **场景 Tab 切换**: 每个用例一个 Tab，展示会话聊天历史、模拟器决策过程、工具调用（工具名 + 参数 + 结果）、指标得分、叙述分析
- **自包含**: 单个 HTML 文件，仅依赖 Chart.js CDN，可直接用浏览器打开
- **Tainted run banner**: when `EvaluationReport.open_questions` contains a `critical` entry, the HTML MUST render a red banner above the radar chart explaining the run is tainted

## Anti-patterns

| Anti-pattern | K-rule | Failure mode |
|---|---|---|
| LLM "improves" `overall_score` based on its judgement of context | K7 | Report regenerated |
| LLM flips `red_line.triggered` from true to false | K4 + K7 | Report regenerated |
| Inline scenario-report contents into the overall report instead of linking | K6 | Report rejected |
| Begin STEP 9 before STEP 5 / 6 / 7 artifacts exist | K12 | STEP 9 refuses to run |
| Omit Tier-2 caveat in `open_questions` when run has any `reliability=low` case | K11 | Report flagged |
| Omit duplicate-`scored_at` pairs from `open_questions` when STEP 5 input-gate found them | K16 | Report flagged |

---
name: daily-news-digest
description: Produce a daily news brief (AI/security/dev) with links, key takeaways, and action items.
metadata: {"openclaw":{"emoji":"📰"}}
---

When asked for “daily news”, “today’s news”, or a “news digest”:

1) Clarify scope if missing:
   - topics (AI, security, software, business), region, and desired length.
2) Prefer 8–15 high-signal items over many low-signal ones.
3) Use web tools when available:
   - Search first, then open 1–2 sources per item to confirm details.
   - Deduplicate: don’t repeat the same story across multiple outlets.
4) Output format:
   - Date (include timezone)
   - Top stories (bullets with links)
   - 5 key takeaways
   - “What to watch” (next 24–72h)
   - Optional: “Recommended reads” (3 links)
5) Be explicit about uncertainty and avoid quoting large blocks from sources.

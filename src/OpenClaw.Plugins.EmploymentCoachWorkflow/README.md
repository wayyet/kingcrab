# OpenClaw.Plugins.EmploymentCoachWorkflow

Dynamic native OpenClaw plugin that packages the employment coach workflow skills and registers the `ontology_ingest` tool used by `ontology_extraction`.

Packaged skills:

- `employment-coach-conversation`
- `diagnosis`
- `external_config`
- `skill_generation`
- `ontology_extraction`

## Build

```powershell
dotnet build src/OpenClaw.Plugins.EmploymentCoachWorkflow/OpenClaw.Plugins.EmploymentCoachWorkflow.csproj
```

The plugin manifest and `skills/` directory are copied to the build output directory. Point `Plugins:DynamicNative:Load:Paths` at that output directory, or package the output directory as a native plugin folder.

## Gateway Configuration

```json
{
  "Plugins": {
    "DynamicNative": {
      "Enabled": true,
      "Load": {
        "Paths": ["src/OpenClaw.Plugins.EmploymentCoachWorkflow/bin/Debug/net10.0"]
      },
      "Entries": {
        "employment-coach-workflow": {
          "Enabled": true,
          "Config": {
            "tooling": {
              "workspaceRoot": "env:OPENCLAW_WORKSPACE",
              "allowedReadRoots": ["*"],
              "allowedWriteRoots": ["*"]
            }
          }
        }
      }
    }
  }
}
```

`Config` may either be a `ToolingConfig` object directly or an object with a nested `tooling` property. The plugin is a dynamic native plugin and therefore requires JIT runtime mode.

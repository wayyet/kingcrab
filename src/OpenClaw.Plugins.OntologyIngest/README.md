# OpenClaw.Plugins.OntologyIngest

Dynamic native OpenClaw plugin that registers the `ontology_ingest` tool.

## Build

```powershell
dotnet build src/OpenClaw.Plugins.OntologyIngest/OpenClaw.Plugins.OntologyIngest.csproj
```

The plugin manifest is copied to the build output directory. Point `Plugins:DynamicNative:Load:Paths` at that output directory, or package the output directory as a native plugin folder.

## Gateway Configuration

```json
{
  "Plugins": {
    "DynamicNative": {
      "Enabled": true,
      "Load": {
        "Paths": ["src/OpenClaw.Plugins.OntologyIngest/bin/Debug/net10.0"]
      },
      "Entries": {
        "ontology-ingest": {
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

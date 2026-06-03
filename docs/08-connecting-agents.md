# Connecting AI Agents to Wendmem

Wendmem is an MCP server. Any AI agent that supports the Model Context Protocol can connect to it. This guide covers the two transport modes and concrete configuration for popular agents.

## Prerequisites

1. **Build and publish wendmem:**

Use the provided build script to produce a self-contained binary:

```powershell
.\build.ps1
```

This produces `publish\Wendmem.exe` with all dependencies bundled (AOT-compiled, no .NET runtime needed).

2. **Ensure model files exist:**

Run the download script in the `integrations/` folder:

```powershell
cd integrations
.\download-model.ps1
```

The files should be in `models/embeddinggemma/`:
- `model_quantized.onnx`
- `model_quantized.onnx_data`
- `tokenizer.model`

3. **Choose a database location** (optional — defaults to `palace.duckdb` in the working directory):

```powershell
$env:WENDMEM_DB="C:\path\to\palace.duckdb"
```

## Transport Modes

### Stdio (default)

The MCP server communicates over stdin/stdout. This is the standard mode for local agents.

```bash
publish\Wendmem.exe
```

No arguments needed — it starts the MCP stdio server immediately (unless a CLI subcommand is recognized).

### HTTP

For agents that prefer HTTP-based MCP:

```bash
publish\Wendmem.exe serve
```

Listens on `http://localhost:5133/mcp` by default. Override the port with the `Palace:HttpPort` config key in `appsettings.json`.

The HTTP endpoint is stateless (`Stateless = true`) — each request is independent.

## Agent Configuration

### Goose (Block / Goose CLI)

Add wendmem as a stdio extension in your Goose config (`~/.config/goose/config.yaml`):

```yaml
extensions:
  wendmem:
    name: wendmem
    type: stdio
    cmd: <install-path>\Wendmem.exe
    args: []
    envs:
      WENDMEM_DB: <path-to>\palace.duckdb
    description: "Personal knowledge bank — search, store, and synthesize memories"
    timeout: 300
```

### Claude Desktop

Edit `claude_desktop_config.json`:

**Stdio mode:**
```json
{
  "mcpServers": {
    "wendmem": {
      "command": "<install-path>\\Wendmem.exe",
      "env": {
        "WENDMEM_DB": "<path-to>\\palace.duckdb"
      }
    }
  }
}
```

**HTTP mode:**
```json
{
  "mcpServers": {
    "wendmem": {
      "url": "http://localhost:5133/mcp"
    }
  }
}
```

### Cursor

Add to Cursor's MCP settings (settings -> Features -> MCP):
- **Name**: wendmem
- **Type**: command (for stdio) or http (for HTTP mode)
- **Command**: `<install-path>\Wendmem.exe`
- **URL**: `http://localhost:5133/mcp`

### Any MCP-Compatible Agent

Wendmem implements the standard MCP protocol. For any agent:

| Parameter | Value |
|-----------|-------|
| **Transport** | stdio (default) or HTTP at `http://localhost:5133/mcp` |
| **Tools exposed** | 14 tools: WakeUp, SearchMemories, GrepExact, GetDrawer, AddMemory, AddTriple, InvalidateTriple, WikiRead, WikiWrite, WikiSearch, ListPendingUpdates, DismissPendingUpdate, LintWiki, Distill |
| **Resources** | 1 resource: `palace://schema` (auto-generated wing info + conventions) |
| **Protocol version** | MCP (latest) |
| **Auth** | None — local-only |

## Verifying the Connection

After configuring your agent, test it:

1. Start a session with your agent
2. Ask it to call `WakeUp` with a wing you've mined
3. You should see synthesis pages, recent drawers, and semantic results

If nothing comes back:
- Check that `palace.duckdb` exists and has data (`wendmem stats`)
- Check that model files exist in `models/embeddinggemma/`
- Check stderr logs

4. Check `palace://schema` resource — it should return wing names, routing keywords, and conventions.

## Running as a Background Service

For HTTP mode, you may want wendmem running persistently:

**Windows (PowerShell):**
```powershell
Start-Process -FilePath "publish\Wendmem.exe" -ArgumentList "serve" -WindowStyle Hidden
```

**With a specific port:**
Add to `appsettings.json`:
```json
{
  "Palace": {
    "HttpPort": "5133"
  }
}
```

## Multi-Agent Setup

Multiple agents can share the same wendmem instance:

| Setup | How |
|-------|-----|
| **Stdio** | Each agent starts its own wendmem process. DuckDB supports concurrent readers. |
| **HTTP** | Run one `wendmem serve` instance. All agents connect to `http://localhost:5133/mcp`. |

For write-heavy workloads, prefer HTTP mode with a single process to avoid DuckDB write contention.

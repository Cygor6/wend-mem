#!/usr/bin/env node
/**
 * wakeup-hook.mjs — Wendmem WakeUp hook
 *
 * Called on SessionStart by Claude Code, Codex, and Gemini CLI.
 * Fetches context from wendmem via MCP-over-HTTP and returns it in the
 * format each agent expects.
 *
 * Requirements: Node.js 18+ (built-in fetch). Wendmem server must be running
 * on WENDMEM_PORT (default 5133).
 *
 * Output contract (portable):
 *   - Claude Code / Codex: { additionalContext }
 *   - Gemini CLI:          { systemMessage }
 *
 * Hook output is capped at ~10,000 chars by some agents; anything larger is
 * shunted to a file and replaced by a preview. We trim to MAX_CONTEXT_CHARS
 * and tell the agent to call WakeUp directly for the rest.
 */

import { readFileSync, existsSync, readdirSync } from "fs";
import { join, basename, dirname, resolve, relative, isAbsolute } from "path";
import { env, cwd, argv, stdout, stderr } from "process";

const WENDMEM_PORT = env.WENDMEM_PORT ?? "5133";
const WENDMEM_URL = `http://localhost:${WENDMEM_PORT}/mcp`;
const TIMEOUT_MS = 20_000;
// Default code-root convention. Override per-project via:
//   - WENDMEM_CODE_ROOT env var
//   - .wendmem-code-root marker file (read upward from cwd)
const DEFAULT_CODE_ROOT = "C:\\dev";
const MAX_CONTEXT_CHARS = 9_000;

// ---------------------------------------------------------------------------
// Detect wing
// ---------------------------------------------------------------------------
function detectWing() {
    if (env.WENDMEM_WING) return env.WENDMEM_WING;
    const markerValue = readUpwardMarker(".wendmem-wing");
    if (markerValue) return markerValue;
    return basename(cwd());
}

function readUpwardMarker(fileName) {
    let dir = resolve(cwd());
    while (true) {
        const marker = join(dir, fileName);
        if (existsSync(marker)) {
            return readFileSync(marker, "utf8").trim().split("\n")[0].trim();
        }
        const parent = dirname(dir);
        if (parent === dir) return "";
        dir = parent;
    }
}

function isInsideOrEqual(candidate, parent) {
    const rel = relative(
        resolve(parent).toLowerCase(),
        resolve(candidate).toLowerCase(),
    );
    return rel === "" || (!rel.startsWith("..") && !isAbsolute(rel));
}

function hasProjectMarker(dir) {
    const fixedMarkers = [
        ".git",
        "package.json",
        "pnpm-workspace.yaml",
        "nx.json",
        "pyproject.toml",
        "Cargo.toml",
        "go.mod",
    ];
    if (fixedMarkers.some((name) => existsSync(join(dir, name)))) return true;
    try {
        return readdirSync(dir, { withFileTypes: true }).some((entry) => {
            if (!entry.isFile()) return false;
            const name = entry.name.toLowerCase();
            return (
                name.endsWith(".sln") ||
                name.endsWith(".csproj") ||
                name.endsWith(".fsproj")
            );
        });
    } catch {
        return false;
    }
}

function inferCodeRootFromBase(codeBase) {
    const start = resolve(cwd());
    const base = resolve(codeBase);
    if (!isInsideOrEqual(start, base)) return codeBase;

    let dir = start;
    while (isInsideOrEqual(dir, base)) {
        if (hasProjectMarker(dir)) return dir;
        if (resolve(dir).toLowerCase() === base.toLowerCase()) return base;
        dir = dirname(dir);
    }

    const firstSegment = relative(base, start)
        .split(/[\\/]/)
        .filter(Boolean)[0];
    return firstSegment ? join(base, firstSegment) : base;
}

function detectCodeRoot() {
    const configuredBase =
        env.WENDMEM_CODE_ROOT ||
        readUpwardMarker(".wendmem-code-root") ||
        DEFAULT_CODE_ROOT;
    return inferCodeRootFromBase(configuredBase);
}

function buildOperationalContext(wing, codeRoot) {
    return [
        "[wendmem operational context]",
        `Active wing: ${wing}  ·  Code root: ${codeRoot}`,
        `Use wing: "${wing}" on every wendmem call this session; never mix wings.`,
        "Ground project-specific claims in retrieved drawers/wiki, not training data.",
        "Exact symbol/error/ID → GrepExact; concept/topic → SearchMemories.",
        "End non-trivial sessions in order: RecordEpisode → Distill → WikiWrite (cited).",
        "Full tool parameters: references/tools.md / the wendmem skill — load on demand.",
    ].join("\n");
}

// ---------------------------------------------------------------------------
// MCP HTTP call (stateless)
// ---------------------------------------------------------------------------
async function mcpPost(body) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), TIMEOUT_MS);
    try {
        const res = await fetch(WENDMEM_URL, {
            method: "POST",
            headers: {
                "Content-Type": "application/json",
                Accept: "application/json, text/event-stream",
            },
            body: JSON.stringify(body),
            signal: controller.signal,
        });
        const text = await res.text();
        if (res.headers.get("content-type")?.includes("text/event-stream")) {
            const lines = text.split("\n");
            const dataLine = lines.find((l) => l.startsWith("data:"));
            return dataLine ? JSON.parse(dataLine.slice(5).trim()) : null;
        }
        return JSON.parse(text);
    } finally {
        clearTimeout(timer);
    }
}

async function callWakeUp(wing, seedQuery) {
    await mcpPost({
        jsonrpc: "2.0",
        id: 1,
        method: "initialize",
        params: {
            protocolVersion: "2024-11-05",
            capabilities: {},
            clientInfo: { name: "wendmem-hook", version: "1.0" },
        },
    });

    const args = { wing };
    if (seedQuery) args.seedQuery = seedQuery;

    const response = await mcpPost({
        jsonrpc: "2.0",
        id: 2,
        method: "tools/call",
        params: { name: "WakeUp", arguments: args },
    });

    const content = response?.result?.content;
    if (!Array.isArray(content)) return null;
    return content
        .filter((c) => c.type === "text")
        .map((c) => c.text)
        .join("\n");
}

function clampContext(text) {
    if (text.length <= MAX_CONTEXT_CHARS) return text;
    return (
        text.slice(0, MAX_CONTEXT_CHARS) +
        "\n\n…[wendmem context truncated to fit the hook output cap — " +
        "call WakeUp(wing, seedQuery) directly for the full palace map]"
    );
}

function detectCaller(hookInput) {
    // Goose (Open Plugins) is the ONLY one that puts `working_dir` in the
    // payload. The `event` field alone is ambiguous — Gemini also sends
    // "SessionStart" — so do NOT key off event name here.
    if (hookInput && typeof hookInput.working_dir === "string") return "goose";
    if (env.GOOSE_PLUGIN_ROOT || env.GOOSE_SESSION) return "goose";

    // Gemini CLI: env flag, or its payload shape (no working_dir, but carries
    // session/cwd-style fields). Treat anything Gemini-flagged as gemini.
    if (env.GEMINI_CLI || env.GEMINI_API_KEY || env.GEMINI_SESSION_ID)
        return "gemini";
    if (hookInput && (hookInput.geminiVersion || hookInput.gemini_version))
        return "gemini";

    // Claude Code / Codex both consume top-level additionalContext.
    return "claude";
}

async function readStdin() {
    return new Promise((res) => {
        let data = "";
        process.stdin.setEncoding("utf8");
        process.stdin.on("data", (chunk) => (data += chunk));
        process.stdin.on("end", () => res(data.trim()));
        process.stdin.on("error", () => res(""));
        setTimeout(() => res(data.trim()), 1000);
    });
}

// ---------------------------------------------------------------------------
// Main
// ---------------------------------------------------------------------------
(async () => {
    try {
        const stdinText = await readStdin();
        let hookInput = {};
        try {
            hookInput = stdinText ? JSON.parse(stdinText) : {};
        } catch {}

        const caller = detectCaller(hookInput);

        // Goose passes working_dir in the payload; prefer it over cwd().
        if (caller === "goose" && typeof hookInput.working_dir === "string") {
            try {
                process.chdir(hookInput.working_dir);
            } catch {}
        }

        const wing = detectWing();
        const codeRoot = detectCodeRoot();
        const seedQuery = argv[2] ?? env.WENDMEM_SEED ?? "";

        stderr.write(
            `[wendmem-hook] caller=${caller} wing=${wing} codeRoot=${codeRoot} seed="${seedQuery}"\n`,
        );

        // Goose does not feed SessionStart hook stdout back into the model,
        // so context injection is the recipe's job. Don't even call WakeUp
        // here — the recipe instructs the agent to call it directly.
        if (caller === "goose") {
            stderr.write(
                `[wendmem-hook] (goose) recipe owns WakeUp injection; hook is a no-op.\n`,
            );
            stdout.write("{}");
            process.exit(0);
        }

        const context = await callWakeUp(wing, seedQuery || undefined);

        if (!context) {
            stderr.write(
                "[wendmem-hook] No response from wendmem (server not running?)\n",
            );
            stdout.write("{}");
            process.exit(0);
        }

        const injectedContext = clampContext(
            [buildOperationalContext(wing, codeRoot), context].join("\n\n"),
        );

        // Output shape per agent:
        //   Gemini CLI  → systemMessage (+ hookSpecificOutput for newer builds)
        //   Claude/Codex→ additionalContext
        if (caller === "gemini") {
            stdout.write(
                JSON.stringify({
                    systemMessage: injectedContext,
                    additionalContext: injectedContext,
                    hookSpecificOutput: {
                        hookEventName: "SessionStart",
                        additionalContext: injectedContext,
                    },
                }),
            );
        } else {
            stdout.write(
                JSON.stringify({ additionalContext: injectedContext }),
            );
        }
        process.exit(0);
    } catch (err) {
        stderr.write(`[wendmem-hook] Error: ${err.message}\n`);
        stdout.write("{}");
        process.exit(0);
    }
})();

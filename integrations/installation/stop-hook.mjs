#!/usr/bin/env node
/**
 * stop-hook.mjs — Wendmem Distill reminder
 *
 * Called on Stop (Claude Code / Codex) and SessionEnd (Gemini CLI).
 * Injects a non-blocking reminder to file knowledge if the session did
 * non-trivial work.
 *
 * Output contract (portable, never blocks):
 *   - exit 0 with JSON on stdout
 *   - Claude Code / Codex: { additionalContext }
 *   - Gemini CLI:          { systemMessage }
 * We do NOT block the agent (no decision:"block", no exit 2) so the same
 * script behaves the same across agents.
 */

import { readFileSync, existsSync, readdirSync } from "fs";
import { spawn } from "child_process";
import { join, basename, dirname, resolve, relative, isAbsolute } from "path";
import { env, cwd, stdout, stderr, stdin } from "process";

// Default code-root convention. Override per-project via:
//   - WENDMEM_CODE_ROOT env var
//   - .wendmem-code-root marker file (read upward from cwd)
const DEFAULT_CODE_ROOT = "C:\\dev";

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

function detectCaller(hookInput) {
    // Goose (Open Plugins) is the only one with `working_dir` in the payload;
    // the `event` field collides with Gemini ("SessionStart"/"Stop"), so don't
    // key off event name here.
    if (hookInput && typeof hookInput.working_dir === "string") return "goose";
    if (env.GOOSE_PLUGIN_ROOT || env.GOOSE_SESSION) return "goose";
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
        stdin.setEncoding("utf8");
        stdin.on("data", (chunk) => (data += chunk));
        stdin.on("end", () => res(data.trim()));
        stdin.on("error", () => res(""));
        setTimeout(() => res(data.trim()), 500);
    });
}

(async () => {
    try {
        const stdinText = await readStdin();
        let hookInput = {};
        try {
            hookInput = stdinText ? JSON.parse(stdinText) : {};
        } catch {}

        // Avoid infinite loop: stop_hook_active is set by Claude Code
        // when the Stop hook is active to prevent recursion.
        if (hookInput.stop_hook_active === true) {
            stdout.write("{}");
            process.exit(0);
        }

        const caller = detectCaller(hookInput);

        // Goose passes working_dir in the payload; prefer it over cwd().
        if (caller === "goose" && typeof hookInput.working_dir === "string") {
            try {
                process.chdir(hookInput.working_dir);
            } catch {}
        }

        const wing = detectWing();
        const codeRoot = detectCodeRoot();

        // Session-end transcript mining (non-blocking)
        const transcriptPath =
            hookInput.transcript_path || env.WENDMEM_TRANSCRIPT_PATH;
        if (transcriptPath && existsSync(transcriptPath)) {
            try {
                const transcriptContent = readFileSync(transcriptPath, "utf8");
                // Guard: skip if transcript contains employer-sensitive content
                if (transcriptContent.includes("NW-WMS")) {
                    stderr.write(
                        `[wendmem-stop] Skipping mine-conversation: employer-sensitive (NW-WMS) content detected\n`,
                    );
                } else {
                    const child = spawn(
                        "wendmem",
                        ["mine-conversation", transcriptPath, "--wing", wing],
                        {
                            detached: true,
                            stdio: "ignore",
                            windowsHide: true,
                        },
                    );
                    child.unref();
                    stderr.write(
                        `[wendmem-stop] Spawned mine-conversation for ${transcriptPath} wing=${wing}\n`,
                    );
                }
            } catch (err) {
                stderr.write(
                    `[wendmem-stop] mine-conversation spawn failed: ${err.message}\n`,
                );
            }
        }

        const reminder = [
            "",
            "─── Wendmem session-end reminder ───────────────────────",
            `Wing: ${wing}`,
            `Effective code root: ${codeRoot}`,
            "",
            "If this session involved decisions, new code, problem-solving,",
            "or insights worth remembering:",
            "",
            `  1. RecordEpisode(wing: "${wing}", ...) — if a non-trivial task was attempted`,
            `  2. Distill(wing: "${wing}", sessionSummary: "<one paragraph>")`,
            "  3. WikiWrite if Distill returns a relevant scaffold",
            "",
            "If the session was trivial (simple questions, no new knowledge):",
            "  → Skip all of the above.",
            "─────────────────────────────────────────────────────────────",
        ].join("\n");

        if (caller === "gemini") {
            stderr.write(
                `[wendmem-stop] Distill reminder (gemini) wing=${wing}\n`,
            );
            stdout.write(
                JSON.stringify({
                    systemMessage: reminder,
                    additionalContext: reminder,
                    hookSpecificOutput: {
                        hookEventName: "SessionEnd",
                        additionalContext: reminder,
                    },
                }),
            );
        } else if (caller === "goose") {
            // Goose does not feed hook stdout back to the model. The reminder
            // lives in the recipe instructions instead; here we only log.
            stderr.write(
                `[wendmem-stop] (goose) session ended, wing=${wing}. ` +
                    `RecordEpisode → Distill → WikiWrite if non-trivial.\n`,
            );
            stdout.write("{}");
        } else {
            // Claude Code / Codex: top-level additionalContext.
            stderr.write(
                `[wendmem-stop] Distill reminder (claude/codex) wing=${wing}\n`,
            );
            stdout.write(JSON.stringify({ additionalContext: reminder }));
        }

        process.exit(0);
    } catch (err) {
        stderr.write(`[wendmem-stop] Error: ${err.message}\n`);
        stdout.write("{}");
        process.exit(0);
    }
})();

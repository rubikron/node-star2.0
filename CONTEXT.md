# Nodestar Context

## Project

**Name:** Nodestar

**What it is:** A Windows companion app for SOLIDWORKS. Nodestar runs as its own WPF process, launches or attaches to a SOLIDWORKS instance, keeps its own UI docked beside the CAD window, and is intended to relay user prompts to an external LLM that can decide what actions to perform in SOLIDWORKS.

## This file
- Important context for LLMs working on this project to coordinate together.
- Keep it updated with any important changes.
- Read it after new commits are pulled, to keep up with changes from collaborators.

## Current architecture

- `src/App`
  - WPF desktop app and main user-facing shell.
  - Provides the chat-style UI, connection status pill, settings dialog, and window layout behavior.
  - On startup it tries to find a SOLIDWORKS launch target, connects through `SwBridge`, and resizes the SOLIDWORKS window to the remaining desktop area.

- `src/SwBridge`
  - Local bridge to SOLIDWORKS COM interop.
  - `Connection/SwConnector.cs` launches or attaches to SOLIDWORKS through the Running Object Table (ROT), tracks process state, and can move or resize the SOLIDWORKS window.
  - `Tools/` contains LLM-callable operations. Macro tools are the most complete part right now.

- `src/LlmOrchestrator`
  - Early HTTP client layer for talking to an OpenAI-compatible `/v1/chat/completions` endpoint.
  - `LlmClient` sends the conversation history.
  - `LlmSettings` persists base URL, API key, and model to `%AppData%\\Nodestar\\settings.json`.
  - This layer exists, but its end-to-end behavior is still untested and minimal.

## Implemented behavior

- Nodestar opens as a separate window from SOLIDWORKS.
- The app uses a custom WPF shell with:
  - chat transcript area
  - prompt entry box
  - settings dialog
  - connection status pill
  - reset-window-position control
- Closing Nodestar does not automatically close SOLIDWORKS. The user is warned before exit if SOLIDWORKS is still open.
- `SwConnector` can:
  - attach to an already-running SOLIDWORKS instance found in the ROT
  - launch SOLIDWORKS from either an `.exe` or `.lnk`
  - detect process exit
  - disconnect or detach
  - resize the SOLIDWORKS main window
- `SwBridge` now includes unified session-state gathering for LLM context:
  - `Tools/Session/SwStateCollector.cs` is the single collector for active document, open documents, selection, and active configuration
  - `Models/SwState.cs` defines the compact snapshot contract plus diff/patch payload types
  - `Tools/Session/GetSwStateTool.cs` returns either a full compact JSON snapshot or a compact field-level patch against the previous snapshot
  - document identities prefer normalized full paths and fall back to runtime session tokens for unsaved documents

## Macro system

- Macro files are plain-text `.swb` files stored in a macros directory.
- They must be encoded in ANSI/Windows-1252 for SOLIDWORKS to read them.
- The implemented macro tools are:
  - `write_macro`
  - `run_macro`
  - `list_macros`
  - `delete_macro`
- Macro conventions:
  - entry point must be `Sub main()`
  - `.swb` macros are treated as single-module code files
  - metadata is stored in header comments such as `@name`, `@description`, `@author`, and `@created`
- See `docs/sw-bridge-api.md` for the header format and current tool contract direction.

## Manual testing

- `tests/SwBridge.ManualTest/Program.cs` is a real integration harness, not a unit test.
- It currently:
  - creates a temporary macros directory
  - writes several sample macros
  - launches or connects to SOLIDWORKS
  - runs the macros
  - captures an initial `get_sw_state` full snapshot and later patch snapshots between macro runs
  - deletes them afterward
- This is the best reference for the bridge's currently working flow.

## Automated testing

- `tests/SwBridge.Tests` now contains unit coverage for:
  - empty state patches
  - active-document changes
  - open-document add/update/remove patches
  - selection replacement patches
  - unsaved document fallback identity stability
  - full snapshot JSON round-trip parsing

## Known gaps

- `src/LlmOrchestrator` is not yet wired into a robust tool-calling loop.
- Existing automated tests are still light; most confidence currently comes from the manual integration test.

## Platform assumptions

- Windows-only.
- Built for SOLIDWORKS desktop automation through COM interop.
- Interop assemblies are stored in `interops/`.
- The app expects SOLIDWORKS to be discoverable either through a desktop or Start menu shortcut, or a standard install path.
- Software versions used in this project:
  - SOLIDWORKS 2026
  - NET10.0
  - xUnit.net v3

## What an LLM should understand quickly

- Nodestar is not the CAD process. It is a separate companion app beside SOLIDWORKS.
- `SwBridge` is the local execution layer that touches SOLIDWORKS.
- `LlmOrchestrator` is the remote-LLM transport layer.
- The macro toolchain is currently the most complete execution path.
- The WPF UI is already focused on the intended product shape: a sidecar AI assistant for SOLIDWORKS.
- After large changes, run a minimal test build of the App (which builds SwBridge and LlmOrchestrator) to check for errors.

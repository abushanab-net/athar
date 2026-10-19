# Getting started with agents

Athar gives your AI agents the team's knowledge: the wiki, the memories and the skills. Agents can also save what they learn while you work. Setting it up takes about five minutes.

## 1. Open Agents

Go to **Agents** in the side menu. It has a tab for each supported client and shows when each one last connected.

## 2. Connect your client

- **Claude apps** (claude.ai, Desktop, mobile): add a custom connector with the address and client ID shown on the tab. Sign in with your Athar account; no token is needed.
- **Claude Code, Codex and VS Code**: open **Quick setup** and run the one command for your system. Paste your access token when asked. It sets up every one of these agents it finds on your computer: the Athar plugin for Claude Code (with the athar-brain skill and the capture hooks), Codex and VS Code.
- Then sign in once in each: `/mcp` in Claude Code, `codex mcp login athar` in Codex, and the athar server in VS Code. Running setup again is safe.

## 3. Choose what to capture

Coding sessions are captured only for repositories you opt in. Open **My sessions**, add your repositories under **Capture settings** and save. You can pause all capture there at any time.

## 4. Use it

- Ask your agent about the team's conventions, decisions or runbooks: it searches Athar first.
- When a session ends, Athar suggests memories from it. Personal ones are saved for you (you can undo them on My sessions); the others wait in **Review › Inbox**.
- In a Claude app or VS Code, say "Save this chat to Athar" to keep a chat's decisions.

## Good to know

- Agents act with your permissions, and each space's AI policy decides what they may do there.
- Only you can read your transcripts, and they're deleted after 30 days.
- Redaction removes common secrets, but not all of them: keep secrets out of your prompts.

The full guide is under **Help** in the app.

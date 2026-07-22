# Project conventions

Rules for anyone — human or agent — writing code and commits in this
repository. They are binding: follow them without being asked again.

## 1. Language policy

The project targets Russian-speaking players, so there are two distinct
audiences and they do not share a language.

| What | Language | Why |
|---|---|---|
| Commit messages | **English** | project history, read by tooling and other developers |
| Code comments | **English** | same |
| Identifiers, file names, branches | **English** | same |
| `CLAUDE.md`, code-level docs | **English** | same |
| UI strings in `DowModManager.cs` | **Russian** | shown to the player |
| Console output of `*.ps1` (`Write-Host`, `Write-Log`) | **Russian** | shown to the player |
| `README.md` | **Russian** | end-user documentation |

Never translate a user-facing string to English "for consistency". The split
above is deliberate: the tool speaks Russian, the codebase speaks English.

## 2. Commit messages

Format:

```
<area>: <imperative summary, <= 72 chars, no trailing period>

<body: what changed and WHY. Wrap at 76 columns. Blank line between
paragraphs. Explain the reasoning a future reader will not be able to
reconstruct from the diff alone.>

Co-Authored-By: <agent name> <noreply@anthropic.com>
```

`<area>` is the module directory or component, lowercase:

`ui-unstretch`, `camera-zoom`, `fog-distance`, `widescreen`, `app`,
`launcher`, `docs`, `repo`.

Rules:

- Summary in the **imperative mood**: "add", "fix", "remove" — not "added",
  "adds", "fixing".
- No issue-tracker prefixes, no emoji, no Conventional-Commits `feat:`/`fix:`
  tags. The `<area>:` prefix above is the only prefix.
- The body is mandatory for anything non-trivial. State the root cause for a
  bug fix and the reason for a design choice.
- Describe **what was verified** and, just as importantly, what was **not**.
  Much of this project cannot be tested without the game installed; say so
  explicitly rather than implying a change is proven.
- One logical change per commit. Do not mix a refactor with a fix.

Example:

```
launcher: refuse to switch to a locale that is not installed

The engine crashes at startup when [lang:X] points at a locale that has
no content. Set-GameLanguage wrote [lang:english] unconditionally, so
disabling Russian on an install whose English locale had been replaced by
the localisation pack produced a crash on launch.

Verified on a synthetic game folder; not verified against a real install.
```

## 3. Code comments

- English, sentence case, no trailing period on single-line comments.
- Comment **why**, not what. The code already says what it does.
- Document non-obvious binary layouts, magic offsets and engine quirks — this
  codebase is full of them and they are unguessable from the code alone.
  State where a constant came from.
- Keep the existing block-header style at the top of each `*.ps1`: purpose,
  mechanism, usage examples.
- Do not leave commented-out code. Delete it; git remembers.

## 4. Encoding

- Every `*.ps1` file **must** be saved as **UTF-8 with BOM**. Windows
  PowerShell 5.1 reads a BOM-less file as ANSI and mangles Cyrillic in
  user-facing strings, which turns into a parse error.
- Verify after creating a file: first three bytes must be `EF BB BF`.

## 5. Committer identity

Take the author identity from git configuration — never invent, guess, or
hardcode it:

```powershell
git config user.email
git config user.name
```

**If `user.email` is empty, stop.** Do not commit, do not fall back to any
address found in history, session context, or a hosting account. Ask the
developer to set it first:

```powershell
git config --global user.email "you@example.com"
git config --global user.name  "Your Name"
```

The account e-mail of whatever assistant or IDE is being used is **not** the
committer identity and must never end up in a commit.

Use a single spelling of the author name across the whole history; two
spellings of the same person split the contributor statistics.

Agent-assisted commits carry a `Co-Authored-By:` trailer with the agent's own
name and `noreply@anthropic.com`. Keep the name identical across commits.

## 6. Before committing

1. `*.ps1` — parse check:
   `[System.Management.Automation.Language.Parser]::ParseFile(...)`
2. `app/DowModManager.cs` — build and run the self-test:
   `powershell -File app\Build-App.ps1` then `.\DoW-ModManager.exe --selftest`
3. Confirm no build artefacts are staged (`DoW-ModManager.exe` is ignored).
4. Re-read the diff and make sure the message explains the *why*.

## 7. History

Do not rewrite published history casually. Rewriting changes every commit
hash, breaks existing clones, and requires a force push. On a public
repository the old objects stay reachable by hash on the host until it
garbage-collects them, so a rewrite is **not** a way to make content private.
Rewrite only on an explicit request, and take a backup ref first.

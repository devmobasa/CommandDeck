# Contributing to CommandDeck

This guide is for people who change CommandDeck. Read
[the architecture guide](docs/architecture.md) first. It maps the projects and
the process boundaries.

## Set up

- Install the .NET SDK pinned in `global.json`. The
  [installation guide](docs/install.md#install-the-pinned-sdk) shows an exact-pin
  installer.
- `Directory.Packages.props` and the `packages.lock.json` files pin the packages.
  Change versions there, not in project files.
- On Arch, install the native build and validation tools:

```bash
sudo pacman -S --needed base-devel clang zlib sqlite icu shellcheck bubblewrap wl-clipboard jq
```

## Build and test

```bash
dotnet restore CommandDeck.slnx --locked-mode
./scripts/build.sh
./scripts/test.sh
dotnet format CommandDeck.slnx --verify-no-changes --no-restore
```

Backend tests are executable specifications, not `dotnet test` projects:

```bash
dotnet run --project tests/CommandDeck.Specs -c Release -- --list
dotnet run --project tests/CommandDeck.Specs -c Release -- --filter "query parser"
dotnet run --project tests/CommandDeck.Specs -c Release -- --category Core
```

Choose the smallest gate that covers your change. The
[testing guide](docs/testing.md) documents prerequisites and reports:

| Change | Gate |
|---|---|
| Documentation | Verify source claims, commands, and local links; `git diff --check` |
| Backend | Relevant executable specs, then `./scripts/test.sh` as needed |
| QML or focus behavior | `dotnet scripts/validate.cs desktop` |
| Extension protocol, SDK, or host | `dotnet scripts/validate.cs extensions` |
| Native AOT or native lifetime | `dotnet scripts/validate.cs aot` |
| Backend, extensions, and Native AOT together | `dotnet scripts/validate.cs linux` |
| Broad changes | `dotnet scripts/validate.cs all` |

`partial` permits explicit skips for unavailable desktop tools. `desktop` requires
Omarchy, Quickshell, Qt 6 lint/event tools, and no skipped backend fixtures.
Installed Wayland, Polkit, upgrade, and rollback acceptance need their own
environments; offscreen suites do not establish those outcomes.

## Continuous integration

The GitHub workflow runs the `linux` profile and a separate QML job. The Linux
profile runs the common repository checks once, followed by extension isolation
and Native AOT checks. It observes hosted latency while enforcing other quality
budgets; use the default local quality gate for enforced latency on an appropriate
machine.

Automatic validation runs on pushes to `main` and on PRs ready for review,
including PRs targeting another feature branch. Draft PRs defer validation until
they become ready; manual dispatch remains available. Verify the finished batch
locally before pushing once. Do not push intermediate commits to use hosted CI
as a test loop; reproduce hosted failures locally before the next batch push.

## Releases

The release workflow runs when you push a tag that starts with `v`. The tag
must match the app version, `v` plus `VersionPrefix` and `VersionSuffix` from
`src/CommandDeck.App/CommandDeck.App.csproj`, for example `v0.1.0-preview.1`.
It builds `linux-x64` and `linux-arm64` with `./scripts/publish-aot.sh`,
packages each with `scripts/package-release.cs`, and creates the GitHub release
with the archives and their `.sha256` files. A tag with a `-` suffix becomes a
prerelease. Write the release notes first, in `docs/releases/<tag>.md` (for
example `docs/releases/v0.1.0-preview.1.md`): they open the release page, above
the install steps that the workflow adds, and a tag without them fails. Run the
workflow manually for a dry run: it uploads the archives as workflow artifacts
and creates no release.

To build the same archive locally:

```bash
./scripts/publish-aot.sh linux-x64
dotnet scripts/package-release.cs linux-x64 v0.1.0-preview.1
```

The archive and its `.sha256` file are written to `artifacts/release/`. The
archive's `install.sh` is `scripts/install.sh`, the same installer that
`./scripts/reinstall-all.sh` runs on a local publish. File times in the archive
come from `SOURCE_DATE_EPOCH` (0 when unset); the workflow sets it to the commit
time, so the same commit gives the same archive.

## Generated references

After changing protocol or filter contracts:

```bash
./scripts/generate-protocol-contract.sh
```

After changing configuration options, capability descriptions, or quicklink
validation messages and reserved aliases:

```bash
./scripts/generate-reference-docs.sh
```

Include generated changes with the owning source change. Existing specs check
the protocol Markdown, QML constants, configuration schema, option inventory,
and capability table against their C# owners.

## Gallery screenshots

The README and [gallery](docs/gallery.md) images are rendered from the shared
QML with demonstration data. After a visible UI change, re-render the affected
scenes and review the images before committing them:

```bash
dotnet scripts/capture-gallery.cs desktop search
```

The [capture notes](docs/screenshots/README.md) list each scene's images and
explain how the offscreen renders are isolated.

## Code style

- Keep a handwritten source file near 500 lines. The maximum is 600 lines.
  Split a file by responsibility before it grows past that limit.
  A spec enforces the limit. Generated files are exempt.
- Write a method as a list of short steps, and put a blank line between steps.
  A block (`if`, loop, `using`, `try`) or a statement that needs more than one
  line is a step of its own. Short one-line statements (up to about 100
  characters) that follow each other share one step with no blank line between
  them, even when each one calls a different method:

  ```csharp
  ValidateIndexing( options.Indexing );
  ValidateRanking( options.Search?.Ranking );
  ValidateAi( options.Ai );
  ```

  A run of one-line calls to the same method, such as several `Equal( … )`,
  stays together even when a line is longer. Argument guards such as
  `ArgumentNullException.ThrowIfNull( options );` are their own step. A `return`
  or `throw` has a blank line before it. A variable
  stays directly above the `if` or loop that uses it.
- Assign or deserialize a value first. Then add a separate, braced null check
  that throws. Keep the assignment and its check together, followed by a blank line.
- When an expression needs more than one line, move it to a small private method
  with a clear name. The calling method then reads as one named call per step:

  ```csharp
  var preferences = await LoadPreferencesAsync( cancellationToken )
      .ConfigureAwait( false );

  var filterAliases = await LoadFilterAliasesAsync( cancellationToken )
      .ConfigureAwait( false );
  ```

- When a condition combines several checks or calculations, move it to a small method
  that says what it checks, for example `if ( IsSourceFull( counts, source ) )`.
  A method named `Is…`, `Has…`, `Can…`, or `Should…` only checks; it never changes
  state. A method that adds, removes, or reads input is named for what it does,
  for example `TryAddUniqueId`.
  A method that returns `bool` says which way it answers (`IsExpired`, not
  `CheckExpiry`). A method longer than about 60 lines usually needs named steps.
  Do the same for a long LINQ chain: `var ordered = OrderByPromotion( entries );`.
- Prefer `if`/`else` to the `?:` operator. A short `?:` that fits on one line is
  acceptable, for example in an argument. Do not nest `?:`, and do not split it
  over lines.
- Use patterns instead of chained comparisons of one value:
  `contribution is { Kind: RankingContributionKinds.Frecency, Value: > 0 }`,
  `kind is ActionKinds.Run or ActionKinds.Open`, `value is not null`.
- Keep arguments and parameters on one line when they fit in 160 characters
  (the `max_line_length` in `.editorconfig`). Wrap them only when the line is longer.
- Put each call in a chain on its own line, `.ConfigureAwait( false )` too:

  ```csharp
  var groups = remaining
      .GroupBy( static value => value.SourceId )
      .OrderBy( group => positions.GetValueOrDefault( group.Key, SourcePositions.Default ) )
      .ToArray( );
  ```

- Do not repeat a string or number literal that has a meaning, such as an id, a
  key, or a limit. Give it a `const` name in a static class. Put shared names in
  `CommandDeck.Core` or `CommandDeck.ExtensionProtocol`, and names that one
  project uses in that project.
- Name a constant by what the value means at the place it is used, with its unit
  (`TokenBytes`, `ConnectTimeoutSeconds`, `MaximumPreviewMiB`). Never reuse a
  constant only because its value matches; two meanings get two names.
  A literal with no meaning of its own, such as `0`, `1`, or `+ 2`, stays a
  literal, and so does a token in the one table that defines it.
- Constants are for values our own code must agree on: ids, keys, our own CLI
  flags and wire tokens, executable names, and paths. The flags of another
  program (`systemctl --user`, `bwrap --ro-bind`, `hyprctl -j`) stay literals in
  its argument list, so the list reads like the command line it builds. When a
  framework value is not a `const`, such as `Uri.UriSchemeHttps`, add a `const`
  instead of turning a pattern into chained comparisons.
- Delete unused parameters, variables, properties, fields, and methods. Update
  the callers. A property that is serialized, or that QML reads, is in use.
- In specs, assert wire and stored values as literals, for example
  `Metadata["path"]`, so that an accidental change to a constant fails a spec.
- `.editorconfig` holds these rules for the formatter and the IDE. Keep this
  spacing when you format, and read the resulting diff.
- Write new automation scripts as C# .NET file-based apps (`.cs`). The existing
  shell scripts stay supported.

## Keyboard-first, mouse-complete UI

Every surface must work from the keyboard alone, and every control must also
work with the mouse.

- Put focus in the search field on searchable surfaces. Put it in the first
  editable field in forms, and on **Cancel** in destructive confirmations.
  The main task needs no click first.
- Keep a single search field ready after clicks, copy, regeneration, pagination,
  refresh, and asynchronous replacement. Typing from the results must reach the
  search field. Editing shortcuts and input methods must keep working.
- Respect deliberate Tab and Shift+Tab navigation and arrow keys in button
  groups. Enter and Space activate the focused control. An asynchronous update
  keeps that control focused. A mouse click must not take focus from search
  without a reason.
- Scope input to the active surface. Never take input from another editor or dialog.
- Keep every control reachable in a logical tab order, with visible focus.
  Support arrow keys in lists, consistent keyboard hints, and Escape to clear or close.
- Trap focus in modal dialogs. When a dialog closes, return focus to the control
  that opened it.
- Test open, type, navigate, activate, dismiss, and reopen with real key events.
  Assert the first and the restored focus without correcting focus in test setup.
  Also test a mouse click followed by immediate typing, pagination, replacement,
  and Back.
- When you change shared focus logic, find and test every surface that uses it.
  Name any surface that you did not test.

## Tests for changes

Add tests for every new behavior and every bug fix. A regression test must
reproduce the failure that a user sees, through the closest existing public
seam. It must fail without the fix. If no credible seam exists, explain why in
the pull request. Do not add shallow assertions instead.

Keep UI and security regression coverage intact. Keep the rejection and
containment fixtures for security boundaries.

## Changes and reports

Keep fixes focused and preserve unrelated work.

In a pull request, explain the problem, the resulting behavior, and checks you
actually ran. Name skipped prerequisites and unverified desktop paths. Avoid
personal paths, private data, credentials, and generated build reports in commits.

For a bug report, include the app version, relevant dependency versions,
reproduction steps, and sanitized `commanddeck doctor` output. Review output
before attaching it; paths and local configuration may be personal. Report
vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

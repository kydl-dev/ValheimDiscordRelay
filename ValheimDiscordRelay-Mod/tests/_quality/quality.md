# Test quality ValheimDiscordRelay - PASS

Run 2026-10-31 19:38:44. Thresholds: coverage >= 80%, mutation score >= 60% (FAIL) / 80% (WARN). Explained in E:\Projects\Valheim\Claude\testing\TEST_QUALITY.md.

| File | Lines covered | Mutation score | Killed | Survived | Not covered | Switched off in the code |
|------|---------------|----------------|--------|----------|-------------|--------------------------|
| ModLog.cs | 97.6% (81/83) | 84.3% | 86 | 5 | 11 | 6 |
| Tunable.cs | 100% (43/43) | 100% | 18 | 0 | 0 | 20 |
| **all** | **98.4%** | **86.7%** | | | | |

"Switched off in the code" = mutants disabled by a `// Stryker disable` comment (equivalent or game-only, each with its reason in the code): check them in a review.

## Mutants no test noticed
Each line is a change to the code that every unit test still passed with. Fix: add / sharpen a test that fails for this change. If the change
cannot alter behaviour (equivalent mutant), mark it in the code: `// Stryker disable once <Mutator> : <why>`. Game-only code: wrap it in
`// Stryker disable all : <why>` ... `// Stryker restore all` and cover it with an in-game test.

| File:line | Status | Mutation | Becomes | Code |
|-----------|--------|----------|---------|------|
| ModLog.cs:108 | NoCoverage | Arithmetic mutation | `now + e.Key` | `if (OkSeen.TryGetValue(what, out e) && (now - e.Key) / (double)Stopwatch.Frequency < CheckOkSeconds) { OkSeen[...` |
| ModLog.cs:108 | NoCoverage | Arithmetic mutation | `e.Value - 1` | `if (OkSeen.TryGetValue(what, out e) && (now - e.Key) / (double)Stopwatch.Frequency < CheckOkSeconds) { OkSeen[...` |
| ModLog.cs:108 | NoCoverage | Statement mutation | `;` | `if (OkSeen.TryGetValue(what, out e) && (now - e.Key) / (double)Stopwatch.Frequency < CheckOkSeconds) { OkSeen[...` |
| ModLog.cs:108 | NoCoverage | Arithmetic mutation | `(now - e.Key) * (double)Stopwatch.Frequency` | `if (OkSeen.TryGetValue(what, out e) && (now - e.Key) / (double)Stopwatch.Frequency < CheckOkSeconds) { OkSeen[...` |
| ModLog.cs:108 | Survived | Logical mutation | `OkSeen.TryGetValue(what, out e) \|\| (now - e.Key) / (double)Stopwatch.Frequency < CheckOkSeconds` | `if (OkSeen.TryGetValue(what, out e) && (now - e.Key) / (double)Stopwatch.Frequency < CheckOkSeconds) { OkSeen[...` |
| ModLog.cs:108 | NoCoverage | Equality mutation | `(now - e.Key) / (double)Stopwatch.Frequency > CheckOkSeconds` | `if (OkSeen.TryGetValue(what, out e) && (now - e.Key) / (double)Stopwatch.Frequency < CheckOkSeconds) { OkSeen[...` |
| ModLog.cs:108 | NoCoverage | Equality mutation | `(now - e.Key) / (double)Stopwatch.Frequency <= CheckOkSeconds` | `if (OkSeen.TryGetValue(what, out e) && (now - e.Key) / (double)Stopwatch.Frequency < CheckOkSeconds) { OkSeen[...` |
| ModLog.cs:109 | NoCoverage | Statement mutation | `;` | `if (OkSeen.Count > 500) OkSeen.Clear();` |
| ModLog.cs:109 | Survived | Negate expression | `!(OkSeen.Count > 500)` | `if (OkSeen.Count > 500) OkSeen.Clear();` |
| ModLog.cs:109 | Survived | Equality mutation | `OkSeen.Count < 500` | `if (OkSeen.Count > 500) OkSeen.Clear();` |
| ModLog.cs:111 | NoCoverage | String mutation | `""` | `Info("CHECK ok: " + text + (e.Value > 0 ? " [" + e.Value + " more of this check ok in the last " + CheckOkSeco...` |
| ModLog.cs:111 | NoCoverage | String mutation | `""` | `Info("CHECK ok: " + text + (e.Value > 0 ? " [" + e.Value + " more of this check ok in the last " + CheckOkSeco...` |
| ModLog.cs:111 | NoCoverage | String mutation | `""` | `Info("CHECK ok: " + text + (e.Value > 0 ? " [" + e.Value + " more of this check ok in the last " + CheckOkSeco...` |
| ModLog.cs:111 | Survived | Conditional (false) mutation | `(false?" [" + e.Value + " more of this check ok in the last " + CheckOkSeconds.ToString("0") + " s not written]" :"")` | `Info("CHECK ok: " + text + (e.Value > 0 ? " [" + e.Value + " more of this check ok in the last " + CheckOkSeco...` |
| ModLog.cs:111 | Survived | Equality mutation | `e.Value < 0` | `Info("CHECK ok: " + text + (e.Value > 0 ? " [" + e.Value + " more of this check ok in the last " + CheckOkSeco...` |
| ModLog.cs:111 | NoCoverage | String mutation | `""` | `Info("CHECK ok: " + text + (e.Value > 0 ? " [" + e.Value + " more of this check ok in the last " + CheckOkSeco...` |

Reports: `coverage\index.html`, `stryker\reports\mutation-report.html` (in E:\Projects\Valheim\ValheimDiscordRelay\ValheimDiscordRelay-Tests\_quality).

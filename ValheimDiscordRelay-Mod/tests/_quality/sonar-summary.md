# SonarQube ValheimDiscordRelay - OK (Medium/High impact issues: 0; Sonar server gate: ERROR)

Run 2026-11-04 17:35:20, http://localhost:9000/dashboard?id=ValheimDiscordRelay

| Measure | Value |
|---|---|
| branch_coverage | 2.0 |
| bugs | 0 |
| code_smells | 3 |
| coverage | 1.8 |
| duplicated_lines_density | 0.8 |
| line_coverage | 1.7 |
| ncloc | 17654 |
| reliability_rating | 1.0 |
| security_hotspots | 0 |
| security_rating | 2.0 |
| sqale_rating | 1.0 |
| vulnerabilities | 7 |

## Open issues (10), most severe first

- [MINOR VULNERABILITY] ValheimDiscordRelay-Mod/src/Shared/EnglishNames.cs :45 - Pass a timeout to limit the execution time. (csharpsquid:S6444)
- [MINOR VULNERABILITY] ValheimDiscordRelay-Mod/src/Features/PlayerDeaths/Infrastructure/AttackerInfo.cs :18 - Pass a timeout to limit the execution time. (csharpsquid:S6444)
- [MINOR VULNERABILITY] ValheimDiscordRelay-Mod/src/Features/PlayerDeaths/Infrastructure/DeathQuotes.cs :124 - Pass a timeout to limit the execution time. (csharpsquid:S6444)
- [MINOR VULNERABILITY] ValheimDiscordRelay-Mod/src/Shared/EnglishNames.cs :42 - Pass a timeout to limit the execution time. (csharpsquid:S6444)
- [MINOR VULNERABILITY] ValheimDiscordRelay-Mod/src/Features/PlayerDeaths/Infrastructure/AttackerInfo.cs :16 - Pass a timeout to limit the execution time. (csharpsquid:S6444)
- [MINOR VULNERABILITY] ValheimDiscordRelay-Mod/src/Features/BossDeath/Infrastructure/BossQuotes.cs :49 - Pass a timeout to limit the execution time. (csharpsquid:S6444)
- [MINOR VULNERABILITY] ValheimDiscordRelay-Mod/src/Features/BossDeath/Domain/BossCatalog.cs :37 - Pass a timeout to limit the execution time. (csharpsquid:S6444)
- [INFO CODE_SMELL] ValheimDiscordRelay-Mod/tests/Unit/BossCatalogTests.cs :43 - Call independent Assert statements from inside an Assert.Multiple (external_roslyn:NUnit2045)
- [INFO CODE_SMELL] ValheimDiscordRelay-Mod/tests/Unit/ViewerMathTests.cs :176 - Use Assert.That(<collection>, Has.Count.EqualTo(<value>) (external_roslyn:NUnit2046)
- [INFO CODE_SMELL] ValheimDiscordRelay-Mod/tests/Unit/BossCatalogTests.cs :55 - Call independent Assert statements from inside an Assert.Multiple (external_roslyn:NUnit2045)

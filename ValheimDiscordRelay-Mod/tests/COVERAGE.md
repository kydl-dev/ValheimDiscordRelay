# ValheimDiscordRelay - test coverage

**Automated** = covered by a test that ran green; **Manual** = needs a person; **Gap** = not covered. Never mark an untested behaviour as covered.

| Feature / requirement | Unit | Integration | TestLab | Manual | Status |
|---|---|---|---|---|---|
| Loads with exact KydlCore, no errors | - | ManifestTests, ArchitectureRulesTests | startup-clean | - | Automated |
| Config layout, server-only settings | TunableTests | SettingsTests, ServerOnlySettingsTests | startup-clean | - | Automated |
| Eight feature switches, EnableAll | FunctionsTests | - | feature-toggle | - | Automated |
| KY hub tabs | - | - | hub-page | hub-look | Automated + Manual |
| Screenshot kinds, webhook per kind, F6 buttons | - | - | screenshot-modes, screenshot-kinds, hub-screenshot-button | discord-look | Automated + Manual |
| Chat names, name fallback | DiscordNameFallbackTests | - | discord-live | discord-look | Automated |
| Text limits | TextLimitTests | - | - | - | Automated |
| Report death causes | - | ReportDeathCauseTests | - | - | Automated |
| Harmony patch targets | - | PatchTargetsTests | - | - | Automated |
| Clip upload | - | - | discord-live | - | Automated |
| Death videos, boss videos, death camera | - | - | - | - | Gap |
| Server and player notifications, weekly report | - | - | - | - | Gap |
| Multiplayer (version check, synced settings) | - | - | - | 2-player test | Gap |
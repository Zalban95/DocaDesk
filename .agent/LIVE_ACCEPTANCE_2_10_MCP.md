# Live acceptance — DOCA 2.10.0 MCP addendum

Date: 2026-09-12  
Host: `https://al-office-desk.tail08f157.ts.net:4242/` (`server.version` **2.10.0**)  
Device: `dev_ee82362ec12f` / `DocaDesk-live-4242`  
Client: WinUI Debug x64 with `--mcp`

## Prep

Paired device lacked `mcp:self` (pre-2.10 pair). Added via admin  
`PATCH /api/v1/devices/:id { scopes: […, "mcp:self"] }` → `GET /mcp/self` became reachable.

## Results

| # | Check | Result |
|---|---|---|
| 1 | Start with no host entry → pending offer; `GET /mcp/self` still 404 | **OK** — offer `mo_mty41xxir19u` |
| 2 | Accept offer → `GET /mcp/self` 200, origin client | **OK** — id `portal`, `origin.kind=client`, `originLabel=DocaDesk-live-4242` |
| 3 | Stale host URL → restart → PATCH back to live `BoundUrl` | **OK** |
| 4 | Host `mcp` start/stop actions | **Partial** — start OK (host `running`, tools reachable). After stop, local listener still answered HTTP 200 (push stop not confirmed). Host-start again OK. |
| 5 | `PATCH /mcp/self` with `command`/`transport`/`autostart` | **OK** — transport stayed `http`; ignored fields did not stick |
| 6 | Tools + bearer | **OK** — no bearer → **404**; with host `Authorization` → `initialize` / `tools/list` / `list_windows` (windows include DocaDesk) |

## Notes

- Bearer is **enforced** after host record shows `Authorization`.
- Label offered is machine name (`PORTAL`); server id became `portal`.
- DocaDesk left running after the run (`--mcp`).

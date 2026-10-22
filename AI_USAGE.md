# AI usage

## Tools

| Tool | Used for |
|---|---|
| **Claude Code** (Anthropic, in VS Code) | Reading the assignment PDFs, profiling the data files, planning, writing most of the code, tests and docs, and running builds, tests, Docker and headless screenshots. |
| **ng-openapi-gen** (not AI, a generator) | The typed Angular API client, generated from the OpenAPI contract. |

I used AI because the assignment expects it and it is fast for boilerplate (DTOs, controllers, Angular pages). I kept the rules and the data decisions under my control: I set the rules, and I checked them against the real files and tests.

## How I worked with it

1. I had it **profile the real data first** (Python scripts over the CSVs) before writing any rules. This found the real issues: `deu`, `Yes`, `AGO;N`, `21/02/2024`, the `(draft)` title, NZL after leaving, the partial vote, and unquoted commas in the history file.
2. I had the expected numbers **predicted by hand** from that profile. For example, rev2 should open exactly 8 conflicts. Then I asserted them in a test against the real files.
3. I made it write tests named after each requirement ID, and run them against a real PostgreSQL.

## Where I rejected or fixed its output

- **CSV parsing.** The first version used a standard CSV parser. A test showed that one unterminated quote swallowed the rest of the file, which breaks ING-07. I replaced it with line-by-line parsing.
- **Expected counts.** The first guess was 12 invalid rows and 9 conflicts. Checking the data by hand gave 11 and 8 (`deu` is normalised, not invalid; the FRA contradiction becomes *new* in rev2, not a conflict). The tests use the correct numbers.
- **Error format.** Enums inside ProblemDetails came out as numbers, while the rest of the API used strings. The concurrency test caught it, and it was fixed in the HTTP JSON options.
- **OpenAPI generation** tried to connect to the database during the build. I fixed it so migrations are skipped in that mode.
- **Generated client.** It had duplicate `text/plain` variants and optional-everything models. I made the API JSON-only and marked DTO fields as required, so the client is strictly typed.
- **UI.** A screenshot showed the user switcher on the wrong user. Fixed.
- **Re-opening conflicts.** I changed the rule so a pair of values a human already decided is not reopened when an older file is uploaded again. The first version would have reopened it.
- **Log readability.** Logging scopes printed as `Dictionary…`. I switched to message-template scopes so `CorrelationId:…` is visible in every line.
- **Azure sign-in in Docker.** The first version asked for the device-code sign-in inside a web request. The request timed out and the sign-in was lost. The token cache was not saved either, because a container has no keyring. I moved the sign-in to startup, added a file token cache on a volume, and warmed up a token for every Azure resource at startup.
- **Real PDF results.** On the real scans, empty table lines came back as `:unselected:` marks and were read as countries. The validator caught it (unknown code and totals mismatch), and I fixed the parser and added a test.

I can explain and change any part of the code.

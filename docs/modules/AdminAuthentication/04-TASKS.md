# 04-TASKS — Doctheca Admin Authentication Tasks

| ID | Task | Acceptance result |
|------|------|----------|
| AUTH-01 | Add Identity authentication client, explicit Token validator, and configuration model | Login/refresh Tokens complete full validation before Cookies are set |
| AUTH-02 | Add login, refresh, logout, session endpoints | HTTP contract matches 02-SPEC |
| AUTH-03 | Configure JwtBearer Cookie fallback and the `DocthecaAdmin` policy | `/admin/*` satisfies the 401/403 matrix |
| AUTH-04 | Apply the admin policy to the existing browser admin route group | All admin read/write endpoints are protected |
| AUTH-05 | Remove the caller-less Quaestura (formerly QuestionBank) internal API | No `/internal/question-bank/*`, no import status write-back |
| AUTH-06 | Remove Quaestura (formerly QuestionBank) service key policy and configuration | No `QuestionBankKey` in `start.sh` or `appsettings.json` |
| AUTH-07 | Add frontend login, session, refresh, and logout logic | The frontend never reads or stores Tokens |
| AUTH-08 | Update Docker/Consul/startup configuration documentation | Identity shared configuration comes from Consul; single image and static hosting preserved |
| AUTH-09 | Add backend authentication and internal interface tests | Covers the required scenarios in 05-TESTS |
| AUTH-10 | Run backend tests, frontend build, and feasible Identity integration verification | Judged by actual command output |

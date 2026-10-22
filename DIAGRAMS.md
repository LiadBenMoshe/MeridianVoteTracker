# תרשימי המערכת – Meridian Vote Tracker

> התרשימים כתובים ב-Mermaid. הם מוצגים אוטומטית ב-GitHub, ב-GitLab, ובתצוגה המקדימה של VS Code (צריך להתקין את התוסף *Markdown Preview Mermaid Support*).
> הטקסט בתוך התרשימים באנגלית, כדי שיתאים לשמות בקוד ובמסכים.

## תוכן

1. ארכיטקטורה: מה מדבר עם מה
2. קליטת קובץ CSV: רצף הפעולות
3. סיווג שורה: תרשים החלטה
4. החלטה על קונפליקט: שני אנליסטים במקביל
5. יצירת דירוג וחישוב ברקע
6. בונוס: חילוץ מדף סרוק
7. מחזורי חיים (State)
8. מודל הנתונים (ERD)

---

## 1. ארכיטקטורה

שלושה רכיבים רצים ב-Docker:
- פרונט Angular, שמוגש דרך nginx;
- API ב-ASP.NET Core;
- Workers ברקע, שרצים באותו תהליך כמו ה-API.

כל המשאבים של Azure מחוברים דרך זהות Entra, בלי מפתחות.

```mermaid
flowchart LR
    U["Analyst / Admin<br/>(browser)"]

    subgraph Docker["docker compose"]
        W["web<br/>Angular 20 + nginx<br/>generated API client"]
        subgraph API["api (ASP.NET Core 8)"]
            C["Controllers<br/>Auth X-User → roles<br/>ProblemDetails · CorrelationId"]
            CORE["Meridian.Core<br/>validation · classification<br/>scoring · PDF checks"]
            WK["Background workers<br/>ImportWorker · SnapshotWorker<br/>ExtractionWorker"]
        end
        PG[("PostgreSQL<br/>local container<br/>or Azure")]
    end

    subgraph Azure["Azure (Entra ID identity, no keys)"]
        BLOB[("Blob Storage<br/>original CSV / PDF")]
        DI["AI Document Intelligence<br/>prebuilt-layout"]
        AI["Application Insights<br/>logs + requests"]
        AZPG[("Azure Database<br/>for PostgreSQL")]
    end

    U -->|HTTPS| W
    W -->|"/api + X-User + X-Correlation-Id"| C
    C --> CORE
    C -->|"jobs = table rows"| PG
    WK -->|"claim job FOR UPDATE SKIP LOCKED"| PG
    WK --> CORE
    C -->|upload| BLOB
    WK -->|read file| BLOB
    WK -->|analyze PDF| DI
    C -.->|telemetry| AI
    WK -.->|telemetry| AI
    PG -.->|"switch in .env"| AZPG
```

---

## 2. קליטת קובץ CSV (ING-01 עד ING-15)

הבקשה חוזרת מיד, והעיבוד ממשיך ב-worker. הוא עובד במנות של 250 הצבעות, וכל מנה היא טרנזקציה אחת.

```mermaid
sequenceDiagram
    autonumber
    actor Dana as Dana (Analyst)
    participant UI as Angular
    participant API as ImportsController
    participant Blob as Azure Blob
    participant DB as PostgreSQL
    participant W as ImportWorker
    participant Core as FileAnalyzer + VoteReconciler

    Dana->>UI: drop CSV file
    UI->>API: POST /api/imports (multipart)
    API->>Blob: save original file
    API->>DB: INSERT import (status = Queued)
    API-->>UI: 202 Accepted + import id
    Note over UI: ING-01: returns immediately

    loop every 1.5s (survives refresh, ING-02)
        UI->>API: GET /api/imports/{id}
        API->>DB: read status + processedRows / totalRows
        API-->>UI: progress
    end

    W->>DB: claim job (Queued → Processing)
    W->>Blob: read file
    W->>Core: parse lines, validate, group by vote id, in-file checks
    Note over Core: ING-03/04/05: Invalid · Duplicate · Contradiction

    loop each chunk of 250 votes (one transaction)
        W->>DB: load approved votes, ballots, known conflicts
        W->>Core: compare with approved data
        Core-->>W: New / Unchanged / Conflict per row
        W->>DB: INSERT new votes + ballots (COPY)
        W->>DB: INSERT conflicts (never UPDATE approved, ING-08)
        W->>DB: INSERT vote_events + import_rows (report)
        W->>DB: update processedRows
    end
    W->>DB: status = Completed / CompletedWithIssues
    UI->>API: GET /api/imports/{id}/report
    API-->>UI: counts, per vote, rejected rows, normalisation (ING-14/15)
```

---

## 3. סיווג שורה (ING-06)

כל שורה מקבלת **בדיוק סיווג אחד**. שלוש הבדיקות הראשונות נעשות בתוך הקובץ. ההשוואה לנתון המאושר נעשית רק אחריהן.

```mermaid
flowchart TD
    A([CSV line]) --> B{"Valid structure?<br/>8 columns, quotes OK"}
    B -- no --> INV[["INVALID<br/>row no. + reason + original line"]]
    B -- yes --> C{"Required fields, date format,<br/>ballot Y/N/A/X, known code,<br/>member on vote date?"}
    C -- no --> INV
    C -- yes --> D{"Same vote has different<br/>session / date / title<br/>in this file?"}
    D -- yes --> CON1[["CONTRADICTION<br/>whole vote, nothing chosen"]]
    D -- no --> E{"Same country appears<br/>with different ballots<br/>in this vote?"}
    E -- yes --> CON2[["CONTRADICTION<br/>neither value stored"]]
    E -- no --> F{"Repeat of a row<br/>already seen?"}
    F -- yes --> DUP[["DUPLICATE<br/>ignored, counted"]]
    F -- no --> G{"Approved value exists<br/>for vote + country?"}
    G -- no --> NEW[["NEW<br/>stored as approved<br/>(also completes partial votes, ING-10)"]]
    G -- yes --> H{"Same value?"}
    H -- yes --> UNC[["UNCHANGED"]]
    H -- no --> I{"Already pending, or a human<br/>already decided this pair?"}
    I -- yes --> CF0[["CONFLICT<br/>not reopened (ING-12)"]]
    I -- no --> CF1[["CONFLICT<br/>new conflict for a human<br/>approved value untouched (ING-08)"]]
```

> פרטי ההצבעה (כותרת, תאריך, מושב) עוברים את אותה השוואה. שינוי בהם פותח **קונפליקט על ההצבעה עצמה** (CON-03).

---

## 4. החלטה על קונפליקט: שני אנליסטים במקביל (CON-04, CON-05)

ההחלטה היא `UPDATE` מותנה אחד. רק בקשה אחת יכולה "לנצח". השנייה מקבלת 409 עם המצב העדכני.

```mermaid
sequenceDiagram
    autonumber
    actor Dana
    actor Omar
    participant API as ConflictsController
    participant DB as PostgreSQL

    par same moment
        Dana->>API: POST /conflicts/7/decision (Accept)
    and
        Omar->>API: POST /conflicts/7/decision (Reject)
    end

    API->>DB: BEGIN + lock approved ballot (FOR UPDATE)
    API->>DB: UPDATE conflicts SET status='Accepted' WHERE id=7 AND status='Pending'
    DB-->>API: 1 row (Dana wins)
    API->>DB: UPDATE country_votes (BRA: Y → A, source = rev2, approved by dana)
    API->>DB: INSERT vote_event (ConflictAccepted, old Y, new A, comment)
    API->>DB: COMMIT
    API-->>Dana: 200 OK (decision)

    API->>DB: UPDATE conflicts ... WHERE id=7 AND status='Pending'
    DB-->>API: 0 rows (already decided)
    API->>DB: INSERT vote_event (DecisionNotApplied, omar)
    API-->>Omar: 409 "Already decided" + current state
    Note over Omar: UI shows a clear banner, not a generic error
```

---

## 5. יצירת דירוג (SNP-01 עד SNP-05)

הקלט מוקפא ברגע היצירה, לכן דירוג שנשמר לא משתנה (SNP-04).

```mermaid
sequenceDiagram
    autonumber
    actor Dana
    actor Lena as Lena (Admin)
    participant API as SnapshotsController
    participant DB as PostgreSQL
    participant W as SnapshotWorker

    Dana->>API: POST /snapshots (date range / session / list)
    API->>DB: resolve vote ids
    alt some vote has no position
        API-->>Dana: 422 + list of votes (POS-04, never skipped silently)
    else all have position + weight
        API->>DB: INSERT snapshot (Queued)
        API->>DB: freeze snapshot_votes (position, weight)
        API-->>Dana: 202 Accepted
    end

    W->>DB: claim snapshot (Running)
    W->>DB: freeze snapshot_ballots (approved ballots now)
    W->>W: score every country (only YES/NO counted)
    W->>DB: INSERT snapshot_scores + rank
    W->>DB: status = Completed

    Lena->>API: POST /snapshots/{id}/publish (2024-Q1)
    API->>DB: unique index: one published per quarter
    API-->>Lena: 200, or 409 if the quarter is taken
```

**חישוב הציון לכל מדינה:**

```mermaid
flowchart LR
    V["Vote in snapshot<br/>position + weight"] --> Q{"Ballot of the<br/>country?"}
    Q -- "none (not a member)" --> X1["excluded"]
    Q -- "ABSTAIN / ABSENT" --> X2["not counted"]
    Q -- "YES / NO" --> CNT["counted += weight"]
    CNT --> M{"ballot = position?"}
    M -- yes --> MAT["matched += weight"]
    M -- no --> N0["matched += 0"]
    MAT & N0 --> S["score = 100 × matched ÷ counted<br/>(n/a if counted = 0)"]
```

---

## 6. בונוס: חילוץ מדף סרוק (PDF-01 עד PDF-06)

פלט ה-AI הוא קלט לא אמין. הוא נבדק, עובר לבדיקה של אדם, ורק אחרי אישור נכנס לאותו מנגנון קליטה כמו CSV.

```mermaid
sequenceDiagram
    autonumber
    actor Dana
    participant API as ExtractionsController
    participant Blob as Azure Blob
    participant W as ExtractionWorker
    participant DI as Azure Document Intelligence
    participant Core as SheetParser + ExtractionValidator
    participant IW as ImportWorker

    Dana->>API: upload scanned PDF
    API->>Blob: save PDF
    API-->>Dana: 202 (status Queued, PDF-01)
    W->>DI: analyze (prebuilt-layout) + X-Correlation-Id
    alt temporary error (5xx / 429 / network)
        W->>W: retry up to 3 times with backoff (PDF-06)
    else document rejected (4xx)
        W-->>Dana: Failed + clear message + Retry button
    end
    DI-->>W: tables, words, confidence
    W->>Core: parse header, rows, marks, printed totals
    Core->>Core: checks: unknown code, duplicate country,<br/>low confidence, totals mismatch, missing members (PDF-03)
    W-->>Dana: NeedsReview, issues highlighted (PDF-04)
    Dana->>API: edit / confirm rows, save & re-check
    Dana->>API: Approve (blocked while issues remain)
    API->>IW: new import from the extraction
    IW->>IW: same rules as CSV: New / Unchanged / Conflict (PDF-05)
```

---

## 7. מחזורי חיים (State)

**קליטה:**
```mermaid
stateDiagram-v2
    [*] --> Queued: upload (202)
    Queued --> Processing: worker claims
    Processing --> Completed: no issues
    Processing --> CompletedWithIssues: invalid / contradiction / conflict rows
    Processing --> Failed: missing header, or interrupted by restart
    Completed --> [*]
    CompletedWithIssues --> [*]
    Failed --> [*]
```

**קונפליקט:** החלטה היא סופית, וטריגר במסד חוסם שינוי שלה.
```mermaid
stateDiagram-v2
    [*] --> Pending: import finds a difference
    Pending --> Accepted: Accept (incoming becomes approved)
    Pending --> Rejected: Reject (approved value kept)
    Accepted --> [*]
    Rejected --> [*]
```

**דירוג:**
```mermaid
stateDiagram-v2
    [*] --> Queued: create (all votes have positions)
    Queued --> Running: worker claims
    Running --> Completed: scores saved (frozen)
    Running --> Failed
    Running --> Queued: service restart (re-run is safe)
    Completed --> Published: admin, one per quarter
    Published --> [*]
```

**חילוץ PDF:**
```mermaid
stateDiagram-v2
    [*] --> Queued: upload PDF
    Queued --> Running: worker claims
    Running --> NeedsReview: extracted + checked
    Running --> Queued: temporary error (retry, max 3)
    Running --> Failed: rejected file / too many attempts
    Failed --> Queued: user clicks Retry
    NeedsReview --> NeedsReview: analyst edits
    NeedsReview --> Approved: no issues left → becomes an import
    NeedsReview --> Rejected
    Approved --> [*]
    Rejected --> [*]
```

---

## 8. מודל הנתונים (ERD)

- **`country_votes`** מחזיקה את הערך **המאושר הנוכחי**.
- **`vote_events`** מחזיקה את **כל ההיסטוריה** (append-only).
- **טבלאות ה-`snapshot_*`** מחזיקות עותק **קפוא** של כל דירוג.

```mermaid
erDiagram
    MEMBER_STATES ||--o{ MEMBERSHIPS : "has periods"
    VOTES ||--o{ COUNTRY_VOTES : "approved ballots"
    MEMBER_STATES ||--o{ COUNTRY_VOTES : ""
    VOTES ||--o{ VOTE_EVENTS : "timeline (append-only)"
    VOTES ||--o{ CONFLICTS : ""
    IMPORTS ||--o{ IMPORT_ROWS : "outcome of every row"
    IMPORTS ||--o{ IMPORT_VOTES : "per-vote outcome"
    IMPORTS ||--o{ CONFLICTS : "incoming value from"
    IMPORTS ||--o{ COUNTRY_VOTES : "source of value"
    EXTRACTIONS |o--o| IMPORTS : "approved → import"
    SNAPSHOTS ||--o{ SNAPSHOT_VOTES : "frozen position + weight"
    SNAPSHOTS ||--o{ SNAPSHOT_BALLOTS : "frozen ballots"
    SNAPSHOTS ||--o{ SNAPSHOT_SCORES : "score + rank"

    VOTES {
        string id PK "MA/2024/003"
        int session
        date vote_date
        string title
        char position "Y / N / null"
        int weight "1-5 / null"
    }
    COUNTRY_VOTES {
        string vote_id PK
        char country_code PK
        char ballot "Y N A X"
        long source_import_id
        long source_conflict_id
        string approved_by
    }
    CONFLICTS {
        long id PK
        string kind "CountryVote / VoteDetail"
        string field "ballot / title / date / session"
        string approved_value
        string incoming_value
        string status "Pending / Accepted / Rejected"
        string decided_by
        string comment
    }
    VOTE_EVENTS {
        long id PK
        string type
        string old_value
        string new_value
        string actor
        long import_id
        long conflict_id
    }
    IMPORTS {
        long id PK
        string file_name
        string blob_path
        string uploaded_by
        string status
        int processed_rows
        int total_rows
    }
    SNAPSHOTS {
        long id PK
        string name
        string status
        string published_quarter "unique"
        json rules
    }
```

**שמירה על תקינות ברמת מסד הנתונים:**
- **check constraints:** ערך בחירה Y/N/A/X, משקל 1–5, פורמט רבעון.
- **unique index חלקי:** קונפליקט ממתין אחד לכל שינוי; דירוג מפורסם אחד לכל רבעון.
- **triggers:**
  - מונעים UPDATE ו-DELETE בהיסטוריה, בדוחות ובדירוגים;
  - מונעים מחיקת ערך מאושר;
  - מונעים שינוי של קונפליקט שהוכרע.

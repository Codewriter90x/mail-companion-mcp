PRAGMA foreign_keys=OFF;
BEGIN TRANSACTION;
CREATE TABLE events (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT, followup_id TEXT NOT NULL REFERENCES followups(id),
                    request_id TEXT NOT NULL UNIQUE, fingerprint TEXT NOT NULL, action TEXT NOT NULL,
                    created_at TEXT NOT NULL, changes TEXT NOT NULL, note TEXT NOT NULL, response TEXT NOT NULL);
INSERT INTO "events" VALUES(1,'d89087a7-d0b3-4fde-a5bc-716a9ef3c5af','python-create','711b42090ebf299a30b0f4739a5b62494caa70e42f8aec036d27acae794f10c5','create','2026-10-18T10:00:00+00:00','{"title": "Città Rignano", "expected_outcome": "Collegamento verificato", "next_review_at": "2026-10-20T08:00:00+00:00", "participants": ["A", "B"], "waiting_for": "", "email_references": []}','Prova sintetica','{"followup": {"id": "d89087a7-d0b3-4fde-a5bc-716a9ef3c5af", "title": "Città Rignano", "expected_outcome": "Collegamento verificato", "participants": ["A", "B"], "waiting_for": "", "status": "open", "next_review_at": "2026-10-20T08:00:00+00:00", "email_references": [], "revision": 1, "created_at": "2026-10-18T10:00:00+00:00", "updated_at": "2026-10-18T10:00:00+00:00", "closed_at": null, "closure_reason": null, "overdue": false, "next_review_local": "2026-10-20T10:00:00+02:00"}, "replayed": false}');
CREATE TABLE followups (
                    id TEXT PRIMARY KEY, title TEXT NOT NULL, expected_outcome TEXT NOT NULL,
                    participants TEXT NOT NULL, waiting_for TEXT NOT NULL, status TEXT NOT NULL
                    CHECK(status IN ('open','waiting','needs_review','closed')),
                    next_review_at TEXT NOT NULL, email_references TEXT NOT NULL,
                    revision INTEGER NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
                    closed_at TEXT, closure_reason TEXT);
INSERT INTO "followups" VALUES('d89087a7-d0b3-4fde-a5bc-716a9ef3c5af','Città Rignano','Collegamento verificato','["A", "B"]','','open','2026-10-20T08:00:00+00:00','[]',1,'2026-10-18T10:00:00+00:00','2026-10-18T10:00:00+00:00',NULL,NULL);
CREATE INDEX followups_due ON followups(status, next_review_at, id);
CREATE INDEX followups_history ON events(followup_id, sequence);
DELETE FROM "sqlite_sequence";
INSERT INTO "sqlite_sequence" VALUES('events',1);
COMMIT;
PRAGMA user_version=1;

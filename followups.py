"""Registro personale offline: soli metadati, transazioni SQLite e storico."""
from contextlib import contextmanager
from datetime import datetime, timedelta, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import sqlite3
import stat
from urllib.parse import quote
import uuid
from zoneinfo import ZoneInfo

from config import MailError

STATUSES = ('open', 'waiting', 'needs_review', 'closed')


def text(value, name, maximum, required=False):
    if not isinstance(value, str) or len(value) > maximum or '\x00' in value:
        raise MailError(f'{name}: testo non valido o troppo lungo (max {maximum}).')
    value = value.strip()
    if required and not value:
        raise MailError(f'{name}: campo obbligatorio.')
    return value


def timestamp(value):
    try:
        parsed = datetime.fromisoformat(value.replace('Z', '+00:00'))
        if parsed.tzinfo is None or parsed.utcoffset() is None:
            raise ValueError()
        return parsed.astimezone(timezone.utc).isoformat(timespec='seconds')
    except (TypeError, ValueError, AttributeError, OverflowError):
        raise MailError('Data non valida: usare ISO 8601 con offset esplicito, es. 2026-10-23T10:00:00+02:00. Chiarire date ambigue prima di salvare.') from None


def participants(values):
    if not isinstance(values, list) or len(values) > 20:
        raise MailError('Partecipanti: massimo 20 nomi.')
    return list(dict.fromkeys(text(v, 'Partecipante', 200, True) for v in values))


def references(values):
    if not isinstance(values, list) or len(values) > 30:
        raise MailError('Riferimenti: massimo 30 messaggi.')
    result = []
    for value in values:
        if not isinstance(value, dict) or set(value) - {'account_id','folder','message_uid','uidvalidity','message_id'}:
            raise MailError('Riferimento email non valido. Non salvare corpi o allegati nel registro.')
        ref = {k: text(value.get(k, ''), k, 512, True) for k in ('account_id','folder','message_uid','uidvalidity')}
        for key in ('message_uid','uidvalidity'):
            if not re.fullmatch(r'[1-9][0-9]{0,9}', ref[key]) or int(ref[key]) > 4294967295:
                raise MailError('UID/UIDVALIDITY del riferimento non valido.')
        ref['message_id'] = text(value.get('message_id', ''), 'Message-ID', 998)
        if ref not in result:
            result.append(ref)
    return result


class FollowupStore:
    def __init__(self, path=None, now=None):
        self.path = Path(path or os.environ.get('ARUBA_MAIL_DB_PATH') or Path(__file__).with_name('data') / 'followups.sqlite3').expanduser().absolute()
        self.now = now or (lambda: datetime.now(timezone.utc))

    def clock(self):
        return self.now().astimezone(timezone.utc).isoformat(timespec='seconds')

    @contextmanager
    def connection(self, write=False):
        db = None
        try:
            if not self.path.exists() and not write:
                yield None
                return
            if write:
                self.path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
                try:
                    fd = os.open(self.path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
                    os.close(fd)
                except FileExistsError:
                    pass
            if os.name == 'posix' and stat.S_IMODE(self.path.stat().st_mode) & 0o077:
                raise MailError('Database non privato: impostare permessi 600 sul file SQLite.')
            if write:
                db = sqlite3.connect(self.path, timeout=5, isolation_level=None)
            else:
                db = sqlite3.connect('file:' + quote(str(self.path), safe='/') + '?mode=ro', uri=True, timeout=5, isolation_level=None)
            db.row_factory = sqlite3.Row
            db.execute('PRAGMA foreign_keys=ON')
            if write:
                db.execute('BEGIN IMMEDIATE')
                version = db.execute('PRAGMA user_version').fetchone()[0]
                if version not in (0, 1):
                    raise MailError('Versione database non supportata: non avviare una versione precedente del bridge su questo registro.')
                db.execute('''CREATE TABLE IF NOT EXISTS followups (
                    id TEXT PRIMARY KEY, title TEXT NOT NULL, expected_outcome TEXT NOT NULL,
                    participants TEXT NOT NULL, waiting_for TEXT NOT NULL, status TEXT NOT NULL
                    CHECK(status IN ('open','waiting','needs_review','closed')),
                    next_review_at TEXT NOT NULL, email_references TEXT NOT NULL,
                    revision INTEGER NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
                    closed_at TEXT, closure_reason TEXT)''')
                db.execute('''CREATE TABLE IF NOT EXISTS events (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT, followup_id TEXT NOT NULL REFERENCES followups(id),
                    request_id TEXT NOT NULL UNIQUE, fingerprint TEXT NOT NULL, action TEXT NOT NULL,
                    created_at TEXT NOT NULL, changes TEXT NOT NULL, note TEXT NOT NULL, response TEXT NOT NULL)''')
                db.execute('CREATE INDEX IF NOT EXISTS followups_due ON followups(status, next_review_at, id)')
                db.execute('CREATE INDEX IF NOT EXISTS followups_history ON events(followup_id, sequence)')
                db.execute('PRAGMA user_version=1')
                # Keep an empty, valid schema even when the first mutation is rejected.
                db.execute('COMMIT')
                db.execute('BEGIN IMMEDIATE')
            elif db.execute('PRAGMA user_version').fetchone()[0] != 1:
                raise MailError('Database non inizializzato o versione non supportata.')
            yield db
            if write:
                db.execute('COMMIT')
        except (sqlite3.Error, OSError):
            raise MailError('Registro locale non disponibile: verificare volume persistente, permessi e spazio. Nessuna email modificata. Ritentare con lo stesso request_id.') from None
        finally:
            if db:
                if db.in_transaction:
                    db.rollback()
                db.close()

    def view(self, row):
        result = dict(row)
        for key in ('participants', 'email_references'):
            result[key] = json.loads(result[key])
        result['overdue'] = result['status'] != 'closed' and result['next_review_at'] <= self.clock()
        result['next_review_local'] = datetime.fromisoformat(result['next_review_at']).astimezone(ZoneInfo('Europe/Rome')).isoformat()
        return result

    def require(self, db, followup_id):
        followup_id = text(followup_id, 'ID questione', 100, True)
        row = db.execute('SELECT * FROM followups WHERE id=?', (followup_id,)).fetchone() if db else None
        if row is None:
            raise MailError('Questione non trovata. Usare list_followups.')
        return row

    def mutation(self, action, request_id, payload, operation):
        request_id = text(request_id, 'request_id', 100, True)
        digest = hashlib.sha256(json.dumps({'action': action, 'payload': payload}, sort_keys=True, ensure_ascii=False).encode()).hexdigest()
        with self.connection(write=True) as db:
            previous = db.execute('SELECT fingerprint,response FROM events WHERE request_id=?', (request_id,)).fetchone()
            if previous:
                if previous['fingerprint'] != digest:
                    raise MailError('request_id già usato con parametri diversi. Non ripetere o modificare silenziosamente una richiesta precedente.')
                return {**json.loads(previous['response']), 'replayed': True}
            followup_id, changes, note = operation(db)
            result = {'followup': self.view(self.require(db, followup_id)), 'replayed': False}
            db.execute('INSERT INTO events(followup_id,request_id,fingerprint,action,created_at,changes,note,response) VALUES(?,?,?,?,?,?,?,?)',
                       (followup_id, request_id, digest, action, self.clock(), json.dumps(changes, ensure_ascii=False), note, json.dumps(result, ensure_ascii=False)))
            return result

    def create(self, title, expected_outcome, next_review_at, request_id, people=None, waiting_for='', email_references=None, note=''):
        payload = dict(title=text(title,'Titolo',200,True), expected_outcome=text(expected_outcome,'Risultato atteso',2000,True),
                       next_review_at=timestamp(next_review_at), participants=participants(people or []), waiting_for=text(waiting_for,'In attesa di',300),
                       email_references=references(email_references or []), note=text(note,'Nota',2000))
        def operation(db):
            identity = str(uuid.uuid4())
            now = self.clock()
            db.execute('''INSERT INTO followups(id,title,expected_outcome,participants,waiting_for,status,next_review_at,email_references,revision,created_at,updated_at)
                        VALUES(?,?,?,?,?,?,?,?,1,?,?)''',
                       (identity,payload['title'],payload['expected_outcome'],json.dumps(payload['participants']),payload['waiting_for'],
                        'waiting' if payload['waiting_for'] else 'open', payload['next_review_at'],json.dumps(payload['email_references']),now,now))
            return identity, {k:v for k,v in payload.items() if k!='note'}, payload['note']
        return self.mutation('create', request_id, payload, operation)

    def change(self, followup_id, expected_revision, request_id, action='update', **fields):
        if not isinstance(expected_revision, int) or isinstance(expected_revision,bool) or expected_revision < 1:
            raise MailError('expected_revision deve essere la revisione corrente restituita dal registro.')
        note = text(fields.pop('note', ''), 'Nota', 2000)
        clean = {}
        for key,value in fields.items():
            if value is None:
                continue
            if key in ('title','expected_outcome','waiting_for','closure_reason'):
                maximum = {'title':200,'expected_outcome':2000,'waiting_for':300,'closure_reason':2000}[key]
                clean[key] = text(value,key,maximum,key != 'waiting_for')
            elif key == 'next_review_at':
                clean[key] = timestamp(value)
            elif key == 'participants':
                clean[key] = participants(value)
            elif key == 'email_references':
                clean[key] = references(value)
            elif key == 'status' and value in STATUSES:
                clean[key] = value
            else:
                raise MailError('Campo o stato della questione non valido.')
        if action not in ('update','snooze','close'):
            raise MailError('Operazione non supportata.')
        if action == 'close' and not clean.get('closure_reason'):
            raise MailError('Indicare il motivo della chiusura.')
        if action != 'close' and ('closure_reason' in clean or clean.get('status') == 'closed'):
            raise MailError('Per chiudere usare close_followup con motivo esplicito.')
        if not clean and not note:
            raise MailError('Nessun aggiornamento specificato.')
        payload = dict(id=followup_id, expected_revision=expected_revision, fields=clean, note=note)
        def operation(db):
            current = self.require(db, followup_id)
            if current['revision'] != expected_revision:
                raise MailError('Revisione cambiata: rileggere la questione prima di modificarla. Nessun aggiornamento sovrascritto.')
            if current['status'] == 'closed':
                raise MailError('Questione già chiusa: nessuna modifica applicata.')
            updates = dict(clean)
            if action == 'close':
                updates.update(status='closed',closed_at=self.clock())
            if action == 'snooze' and ('next_review_at' not in updates or updates['next_review_at'] <= self.clock()):
                raise MailError('Il prossimo controllo deve essere nel futuro.')
            updates.update(updated_at=self.clock(), revision=current['revision']+1)
            stored = {k:json.dumps(v,ensure_ascii=False) if k in ('participants','email_references') else v for k,v in updates.items()}
            db.execute('UPDATE followups SET ' + ','.join(k+'=?' for k in stored) + ' WHERE id=?', [*stored.values(), followup_id])
            changes = {k:{'before':json.loads(current[k]) if k in ('participants','email_references') else current[k], 'after':v} for k,v in updates.items() if k not in ('revision','updated_at')}
            return followup_id, changes, note
        return self.mutation(action, request_id, payload, operation)

    def list(self, status='active', due='all', days=7, limit=20, offset=0):
        if status not in (*STATUSES,'active','all') or due not in ('all','overdue','upcoming') or not 1 <= days <= 365 or not 1 <= limit <= 50 or not 0 <= offset <= 10000:
            raise MailError('Filtri non validi: limit 1–50, offset 0–10000, days 1–365.')
        where, args = [], []
        if status == 'active':
            where.append("status != 'closed'")
        elif status != 'all':
            where.append('status=?'); args.append(status)
        if due != 'all':
            where.append("status != 'closed'")
            now = self.clock()
            if due == 'overdue':
                where.append('next_review_at<=?'); args.append(now)
            else:
                where.append('next_review_at>? AND next_review_at<=?')
                args.extend((now,(self.now().astimezone(timezone.utc)+timedelta(days=days)).isoformat(timespec='seconds')))
        clause = ' WHERE ' + ' AND '.join(where) if where else ''
        with self.connection() as db:
            if not db:
                rows, total = [], 0
            else:
                total = db.execute('SELECT count(*) FROM followups'+clause,args).fetchone()[0]
                rows = db.execute('SELECT * FROM followups'+clause+' ORDER BY next_review_at,id LIMIT ? OFFSET ?',[*args,limit,offset]).fetchall()
            return {'followups':[self.view(row) for row in rows], 'total':total, 'offset':offset, 'has_more':offset+limit<total, 'checked_at':self.clock(), 'notifications_enabled':False}

    def get(self, followup_id, history_limit=20, history_offset=0):
        if not 1 <= history_limit <= 50 or not 0 <= history_offset <= 10000:
            raise MailError('Paginazione storico non valida.')
        with self.connection() as db:
            row = self.require(db, followup_id)
            total = db.execute('SELECT count(*) FROM events WHERE followup_id=?',(followup_id,)).fetchone()[0]
            events = db.execute('SELECT sequence,action,created_at,changes,note FROM events WHERE followup_id=? ORDER BY sequence DESC LIMIT ? OFFSET ?', (followup_id,history_limit,history_offset)).fetchall()
            return {'followup':self.view(row),'history':[{**dict(e),'changes':json.loads(e['changes'])} for e in events], 'history_total':total,'history_has_more':history_offset+history_limit<total}

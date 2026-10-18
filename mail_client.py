"""IMAP su richiesta: TLS, UID/UIDVALIDITY, fetch selettivi, nessuna cache."""
import base64
from contextlib import contextmanager
from datetime import date
from email import policy
from email.parser import BytesParser
from html.parser import HTMLParser
import imaplib
import quopri
import re
import ssl

from config import MailError, account

TIMEOUT = 20
MAX_MESSAGE = 5 * 1024 * 1024
MAX_TEXT = 128 * 1024
MAX_HEADERS = 16384
MAX_RESULTS = 30


class BoundedIMAP(imaplib.IMAP4_SSL):
    def read(self, size):
        if size > MAX_TEXT + MAX_HEADERS:
            raise MailError('Il server propone un literal IMAP oltre il limite; download interrotto.')
        return super().read(size)


def quote(value):
    if not isinstance(value, str) or not value or len(value) > 512 or any(ord(c) < 32 or ord(c) == 127 for c in value):
        raise MailError('Parametro IMAP non valido.')
    return '"' + value.replace('\\', '\\\\').replace('"', '\\"') + '"'


def encode_folder(name):
    quote(name)
    out, buf = [], []
    def flush():
        if buf:
            out.append('&' + base64.b64encode(''.join(buf).encode('utf-16-be')).decode().rstrip('=').replace('/', ',') + '-')
            buf.clear()
    for c in name:
        if 32 <= ord(c) <= 126:
            flush()
            out.append('&-' if c == '&' else c)
        else:
            buf.append(c)
    flush()
    return ''.join(out)


def decode_folder(value):
    def sub(m):
        if not m[1]:
            return '&'
        encoded = m[1].replace(',', '/')
        return base64.b64decode(encoded + '=' * (-len(encoded) % 4)).decode('utf-16-be')
    return re.sub(r'&([^-]*)-', sub, value)


def ok(response):
    status, data = response
    if status != 'OK':
        raise MailError('Operazione IMAP rifiutata dal server; nessun dettaglio sensibile restituito.')
    return data


@contextmanager
def connect(account_id, folder=None, write=False):
    cfg = account(account_id)
    client = None
    try:
        client = BoundedIMAP(cfg['imap_host'], cfg.get('imap_port', 993), ssl_context=ssl.create_default_context(), timeout=TIMEOUT)
        client.debug = 0
        client.sock.settimeout(TIMEOUT)
        ok(client.login(cfg['username'], cfg['password']))
        validity = None
        if folder is not None:
            ok(client.select(quote(encode_folder(folder)), readonly=not write))
            _, data = client.response('UIDVALIDITY')
            if not data or not data[0] or not data[0].isdigit():
                raise MailError('UIDVALIDITY non disponibile: operazione interrotta.')
            validity = data[0].decode()
        yield client, validity
    except MailError:
        raise
    except (imaplib.IMAP4.error, OSError, ValueError, UnicodeError):
        raise MailError('Connessione o operazione IMAP fallita. Verificare configurazione, TLS, accesso IMAP e rete; nessuna credenziale mostrata.') from None
    finally:
        if client:
            try:
                client.logout()  # Non CLOSE: nessun expunge implicito.
            except Exception:
                pass


def validate_identity(uid, expected, actual):
    if not isinstance(uid, str) or not re.fullmatch(r'[1-9][0-9]{0,9}', uid) or int(uid) > 4294967295:
        raise MailError('UID non valido: usare quello restituito da list_emails.')
    if str(expected) != actual:
        raise MailError('UIDVALIDITY cambiato: rileggere l’elenco prima di operare.')


def fetch(client, uid, fields):
    data = ok(client.uid('FETCH', uid, fields))
    # Include unsolicited responses only if they match the requested UID.
    for item in data or []:
        meta = item[0] if isinstance(item, tuple) else item
        if isinstance(meta, bytes) and re.search(rb'\bUID ' + uid.encode() + rb'\b', meta):
            return meta, item[1] if isinstance(item, tuple) else b''
    raise MailError('Messaggio non disponibile: potrebbe essere stato spostato o rimosso.')


def headers(client, uid):
    meta, raw = fetch(client, uid, f'(UID FLAGS RFC822.SIZE BODY.PEEK[HEADER.FIELDS (FROM TO CC SUBJECT DATE MESSAGE-ID REFERENCES REPLY-TO)]<0.{MAX_HEADERS}>)')
    if len(raw) >= MAX_HEADERS:
        raise MailError('Intestazioni troppo grandi per questa prima versione.')
    parsed = BytesParser(policy=policy.default).parsebytes(raw, headersonly=True)
    size = re.search(rb'RFC822.SIZE (\d+)', meta)
    if size is None:
        raise MailError('Dimensione messaggio non disponibile.')
    flags = re.search(rb'FLAGS \(([^)]*)\)', meta)
    result = {k: str(parsed.get(k, ''))[:4000] for k in ('from', 'to', 'cc', 'subject', 'date')}
    result['message_id'] = str(parsed.get('message-id', ''))[:998]
    result.update(message_uid=uid, read=bool(flags and b'\\Seen' in flags[1].split()), size_bytes=int(size[1]))
    return result


def list_emails(account_id, folder='INBOX', unread_only=False, limit=10, offset=0, sender=None, subject=None, since=None, before=None):
    if not 1 <= limit <= MAX_RESULTS or not 0 <= offset <= 10000:
        raise MailError('Limite consentito: 1–30 messaggi; offset: 0–10000.')
    criteria = ['UNSEEN' if unread_only else 'ALL']
    utf8 = False
    for key, value in (('FROM', sender), ('SUBJECT', subject)):
        if value:
            criteria.extend((key, quote(value)))
            utf8 |= not value.isascii()
    for key, value in (('SINCE', since), ('BEFORE', before)):
        if value:
            try:
                d = date.fromisoformat(value)
            except ValueError:
                raise MailError('Data non valida: usare YYYY-MM-DD.') from None
            months = ['Jan','Feb','Mar','Apr','May','Jun','Jul','Aug','Sep','Oct','Nov','Dec']
            criteria.extend((key, f'{d.day:02d}-{months[d.month-1]}-{d.year}'))
    with connect(account_id, folder) as (client, validity):
        try:
            encoded = [c.encode('utf-8') if utf8 else c for c in criteria]
            data = ok(client.uid('SEARCH', 'CHARSET', 'UTF-8', *encoded)) if utf8 else ok(client.uid('SEARCH', None, *criteria))
        except imaplib.IMAP4.error:
            raise MailError('Ricerca non supportata dal server (possibile charset UTF-8 non disponibile).') from None
        uids = (data[0] or b'').split()
        selected = list(reversed(uids))[offset:offset + limit]
        rows = []
        for uid in selected:
            row = headers(client, uid.decode())
            row.update(account_id=account_id, folder=folder, uidvalidity=validity)
            rows.append(row)
        return {'emails': rows, 'matched': len(uids), 'offset': offset, 'has_more': offset + limit < len(uids), 'order': 'UID descending'}


# BODYSTRUCTURE is a bounded nested IMAP list; quoted strings may contain escapes.
def parse_structure(raw):
    if len(raw) > 65536:
        raise MailError('Struttura MIME troppo complessa.')
    tokens = re.findall(rb'"(?:\\.|[^"\\])*"|[()]|[^\s()]+', raw)
    pos = 0
    def parse(depth=0):
        nonlocal pos
        if depth > 30 or pos >= len(tokens):
            raise MailError('Struttura MIME non supportata.')
        token = tokens[pos]
        pos += 1
        if token == b'(':
            out = []
            while pos < len(tokens) and tokens[pos] != b')':
                out.append(parse(depth + 1))
            if pos >= len(tokens):
                raise MailError('Struttura MIME incompleta.')
            pos += 1
            return out
        if token == b')' or token.startswith(b'{'):
            raise MailError('Struttura MIME con literal non supportata in questa versione.')
        if token.startswith(b'"'):
            return re.sub(rb'\\(.)', rb'\1', token[1:-1]).decode('utf-8', 'replace')
        if token.upper() == b'NIL':
            return None
        return int(token) if token.isdigit() else token.decode('ascii', 'replace')
    result = parse()
    if pos != len(tokens):
        raise MailError('Struttura MIME non valida.')
    return result


def parts(node, prefix=''):
    if not isinstance(node, list) or not node:
        raise MailError('Struttura MIME non valida.')
    if isinstance(node[0], list):
        index = 0
        children = []
        count = next((i for i, item in enumerate(node) if not isinstance(item, list)), len(node))
        disposition = node[count+2] if len(node) > count+2 else None
        if isinstance(disposition, list) and str(disposition[0]).lower() == 'attachment':
            return [{'section': prefix or '1', 'mime_type': 'multipart/' + str(node[count]).lower(), 'encoding': '', 'size_bytes': 0, 'charset': 'utf-8', 'filename': None, 'attachment': True}]
        while index < len(node) and isinstance(node[index], list):
            index += 1
            children.extend(parts(node[index-1], f'{prefix}.{index}' if prefix else str(index)))
        return children
    if len(node) < 7 or not isinstance(node[6], int):
        raise MailError('Parte MIME non supportata.')
    mime = f'{node[0]}/{node[1]}'.lower()
    disposition_index = 9 if mime.startswith('text/') else 11 if mime == 'message/rfc822' else 8
    disposition = node[disposition_index] if len(node) > disposition_index else None
    params = node[2] if isinstance(node[2], list) else []
    def mapping(items):
        return {str(items[i]).lower(): items[i+1] for i in range(0, len(items)-1, 2)}
    params = mapping(params)
    disp_params = mapping(disposition[1]) if isinstance(disposition, list) and len(disposition) > 1 and isinstance(disposition[1], list) else {}
    attachment = (isinstance(disposition, list) and str(disposition[0]).lower() == 'attachment') or 'filename' in disp_params or 'name' in params
    return [{'section': prefix or '1', 'mime_type': mime, 'encoding': str(node[5]), 'size_bytes': node[6], 'charset': params.get('charset') or 'utf-8', 'filename': disp_params.get('filename') or params.get('name'), 'attachment': attachment}]


class PlainHTML(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.text = []
        self.hidden = 0
    def handle_starttag(self, tag, attrs):
        if tag in ('script', 'style'):
            self.hidden += 1
        if tag in ('br', 'p', 'div', 'li', 'tr'):
            self.text.append('\n')
    def handle_endtag(self, tag):
        if tag in ('script', 'style'):
            self.hidden = max(0, self.hidden - 1)
    def handle_data(self, data):
        if not self.hidden:
            self.text.append(data)


def read_email(account_id, folder, message_uid, uidvalidity):
    with connect(account_id, folder) as (client, validity):
        validate_identity(message_uid, uidvalidity, validity)
        result = headers(client, message_uid)
        if result['size_bytes'] > MAX_MESSAGE:
            raise MailError('Messaggio oltre il limite di 5 MiB: corpo non scaricato.')
        meta, _ = fetch(client, message_uid, '(UID BODYSTRUCTURE)')
        match = re.search(rb'\bBODYSTRUCTURE\s+(\(.*\))\s*\)$', meta, re.S)
        if not match:
            raise MailError('Struttura MIME non disponibile; nessun fetch completo eseguito.')
        leaves = parts(parse_structure(match[1]))
        candidates = [p for p in leaves if not p['attachment'] and p['mime_type'] in ('text/plain', 'text/html')]
        # Prefer plain text. Do not download embedded messages, images or attachments.
        chosen = [p for p in candidates if p['mime_type'] == 'text/plain'] or candidates
        if sum(p['size_bytes'] for p in chosen) > MAX_TEXT or len(chosen) > 20:
            raise MailError('Testo oltre il limite di 128 KiB: corpo non scaricato.')
        texts = []
        for p in chosen:
            _, raw = fetch(client, message_uid, f'(UID BODY.PEEK[{p["section"]}]<0.{p["size_bytes"] + 1}>)')
            if len(raw) != p['size_bytes']:
                raise MailError('Dimensione parte MIME inattesa; lettura interrotta.')
            encoding = p['encoding'].lower()
            if encoding == 'base64':
                try:
                    raw = base64.b64decode(re.sub(rb'\s', b'', raw), validate=True)
                except ValueError:
                    raise MailError('Codifica Base64 non valida.') from None
            elif encoding == 'quoted-printable':
                raw = quopri.decodestring(raw)
            elif encoding not in ('7bit', '8bit', 'binary'):
                raise MailError('Codifica MIME non supportata.')
            try:
                text = raw.decode(p['charset'], 'replace')
            except LookupError:
                text = raw.decode('utf-8', 'replace')
            if p['mime_type'] == 'text/html':
                parser = PlainHTML()
                parser.feed(text)
                text = ''.join(parser.text)
            texts.append(text)
        result.update(account_id=account_id, folder=folder, uidvalidity=validity, text='\n'.join(texts)[:MAX_TEXT], attachments=[{k:p[k] for k in ('section','mime_type','size_bytes','filename')} for p in leaves if p['attachment'] or not p['mime_type'].startswith('text/')], untrusted_content=True)
        return result


def mark_as_read(account_id, folder, message_uid, uidvalidity):
    with connect(account_id, folder, write=True) as (client, validity):
        validate_identity(message_uid, uidvalidity, validity)
        headers(client, message_uid)  # Ensure UID exists before STORE.
        ok(client.uid('STORE', message_uid, '+FLAGS.SILENT', '(\\Seen)'))
        result = headers(client, message_uid)
        if not result['read']:
            raise MailError('Il server non ha confermato il flag letto; rileggere lo stato prima di riprovare.')
        return {'account_id': account_id, 'folder': folder, 'message_uid': message_uid, 'uidvalidity': validity, 'read': True}


def list_folders(account_id):
    with connect(account_id) as (client, _):
        rows = []
        for raw in ok(client.list()) or []:
            if raw is None:
                continue
            if isinstance(raw, tuple):
                name = raw[1].decode('ascii')
                raw = raw[0]
            else:
                name = None
            match = re.match(rb'^\(([^)]*)\) (?:"(?:\\.|[^"\\])*"|NIL) (.*)$', raw)
            if not match:
                raise MailError('Formato cartelle non supportato dal server.')
            if name is None:
                name = match[2].decode('ascii')
                if name.startswith('"') and name.endswith('"'):
                    name = re.sub(r'\\(.)', r'\1', name[1:-1])
            rows.append({'name': decode_folder(name), 'selectable': b'\\Noselect' not in match[1].split()})
        return {'account_id': account_id, 'folders': rows}

"""Configurazione locale: mai inviare questi dati ai tool o ai log."""
import json
import os
import stat
from pathlib import Path

CONFIG_PATH = Path(__file__).with_name('accounts.local.json')

class MailError(Exception):
    pass


def accounts():
    if not CONFIG_PATH.exists():
        raise MailError('Configurazione assente: compilare accounts.local.json localmente.')
    if os.name == 'posix' and stat.S_IMODE(CONFIG_PATH.stat().st_mode) & 0o077:
        raise MailError('Proteggere accounts.local.json con chmod 600 prima di procedere.')
    try:
        raw = json.loads(CONFIG_PATH.read_text())
        result = {}
        for item in raw['accounts']:
            account_id = item['id']
            if not isinstance(account_id, str) or not account_id or account_id in result:
                raise ValueError()
            if not all(isinstance(item.get(k), str) and item[k].strip() for k in ('email', 'username', 'password', 'imap_host')):
                raise ValueError()
            if not isinstance(item.get('imap_port', 993), int) or not 1 <= item.get('imap_port', 993) <= 65535:
                raise ValueError()
            result[account_id] = item
        if not result:
            raise ValueError()
        return result
    except (ValueError, KeyError, TypeError, OSError):
        raise MailError('Configurazione incompleta o non valida: controllare accounts.local.json senza incollare credenziali in chat.') from None


def account(account_id):
    result = accounts()
    if account_id not in result:
        raise MailError('Casella non configurata. Usare list_accounts.')
    return result[account_id]

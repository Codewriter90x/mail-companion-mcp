"""Offline tests: all data lives in temporary directories, never real mail."""
import asyncio
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client
from config import MailError
from followups import FollowupStore

NOW = datetime(2026, 10, 18, 10, tzinfo=timezone.utc)
REF = {'account_id':'test','folder':'INBOX','message_uid':'42','uidvalidity':'77','message_id':'<test@example.invalid>'}


class FollowupTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.path = Path(self.tmp.name)/'private'/'followups.sqlite3'
        self.store = FollowupStore(self.path,now=lambda: NOW)

    def create(self, request='create-1', due='2026-10-20T10:00:00+02:00', **kwargs):
        return self.store.create('Questione di prova', 'Conferma esito verifica', due, request, **kwargs)

    def test_failed_first_mutation_leaves_valid_empty_register(self):
        with self.assertRaises(MailError):
            self.store.change('missing',1,'missing-update',note='No record')
        self.assertEqual(self.store.list()['total'],0)
        self.assertEqual(self.create()['followup']['revision'],1)

    def test_list_empty_does_not_create_database(self):
        self.assertEqual(self.store.list()['total'],0)
        self.assertFalse(self.path.exists())

    def test_multiple_parties_no_assignee_and_reopen_process(self):
        created = self.create(people=['A','B','C','D'], email_references=[REF])['followup']
        self.assertEqual(created['waiting_for'],'')
        self.assertEqual(created['status'],'open')
        self.assertEqual(created['next_review_at'],'2026-10-20T08:00:00+00:00')
        self.assertEqual(created['email_references'],[REF])
        with patch.dict(os.environ, {'ARUBA_MAIL_DB_PATH':str(self.path)}):
            output = subprocess.check_output([sys.executable,'-c', 'from followups import FollowupStore; print(FollowupStore().list()["total"])'], cwd=Path(__file__).parent,text=True)
        self.assertEqual(output.strip(),'1')
        if os.name == 'posix':
            self.assertEqual(self.path.stat().st_mode & 0o777,0o600)
            self.assertEqual(self.path.parent.stat().st_mode & 0o777,0o700)

    def test_identical_retry_does_not_duplicate(self):
        first = self.create()
        second = self.create()
        self.assertTrue(second['replayed'])
        self.assertEqual(first['followup']['id'],second['followup']['id'])
        self.assertEqual(self.store.list()['total'],1)
        self.assertEqual(self.store.get(first['followup']['id'])['history_total'],1)

    def test_reused_request_with_changed_payload_rejected(self):
        self.create()
        with self.assertRaises(MailError):
            self.create(due='2026-11-01T10:00:00+01:00')
        self.assertEqual(self.store.list()['total'],1)

    def test_repeated_simultaneous_create_is_one(self):
        def create(_):
            return FollowupStore(self.path,now=lambda: NOW).create('Concurrent','Esito','2026-10-20T10:00:00+02:00','same-request')
        with ThreadPoolExecutor(max_workers=4) as pool:
            rows = list(pool.map(create,range(4)))
        self.assertEqual(len({r['followup']['id'] for r in rows}),1)
        self.assertEqual(self.store.list()['total'],1)

    def test_reject_ambiguous_naive_and_invalid_date(self):
        for due in ('domani','2026-10-25T02:30:00','2026-02-30T10:00:00+01:00','2026-10-20'):
            with self.subTest(due=due),self.assertRaises(MailError):
                self.create(due=due)
        self.assertFalse(self.path.exists())

    def test_dst_explicit_offsets_distinguish_times(self):
        first = self.create('summer',due='2026-10-25T02:30:00+02:00')['followup']
        second = self.create('winter',due='2026-10-25T02:30:00+01:00')['followup']
        self.assertNotEqual(first['next_review_at'],second['next_review_at'])
        self.assertEqual(first['next_review_local'],'2026-10-25T02:30:00+02:00')
        self.assertEqual(second['next_review_local'],'2026-10-25T02:30:00+01:00')

    def test_update_note_not_new_reply_does_not_reset_date(self):
        row = self.create()['followup']
        changed = self.store.change(row['id'],1,'note',note='Risposta arrivata: stanno verificando',status='waiting',waiting_for='Team da chiarire')['followup']
        self.assertEqual(changed['next_review_at'],row['next_review_at'])
        self.assertNotEqual(changed['status'],'closed')
        history = self.store.get(row['id'])
        self.assertEqual(history['history'][0]['note'],'Risposta arrivata: stanno verificando')
        self.assertEqual(history['history_total'],2)

    def test_snooze_retains_old_new_dates_and_retry(self):
        row = self.create()['followup']
        result = self.store.change(row['id'],1,'snooze',action='snooze',next_review_at='2026-11-01T11:00:00+01:00',note='Attendere test')
        replay = self.store.change(row['id'],1,'snooze',action='snooze',next_review_at='2026-11-01T11:00:00+01:00',note='Attendere test')
        self.assertTrue(replay['replayed'])
        event = self.store.get(row['id'])['history'][0]
        self.assertEqual(event['changes']['next_review_at']['before'],row['next_review_at'])
        self.assertEqual(event['changes']['next_review_at']['after'],result['followup']['next_review_at'])

    def test_stale_revision_no_overwrite_or_history(self):
        row = self.create()['followup']
        self.store.change(row['id'],1,'good',note='Aggiornamento A')
        with self.assertRaises(MailError):
            self.store.change(row['id'],1,'stale',title='Sovrascrittura')
        detail = self.store.get(row['id'])
        self.assertEqual(detail['followup']['title'],row['title'])
        self.assertEqual(detail['history_total'],2)
        self.assertEqual(detail['followup']['revision'],2)

    def test_close_requires_reason_excludes_due_preserves_history(self):
        row = self.create(due='2026-10-17T10:00:00+02:00')['followup']
        self.assertEqual(self.store.list(due='overdue')['total'],1)
        with self.assertRaises(MailError):
            self.store.change(row['id'],1,'bad-close',action='close',closure_reason=' ')
        result = self.store.change(row['id'],1,'close',action='close',closure_reason='Esito verificato')['followup']
        self.assertEqual(result['status'],'closed')
        self.assertFalse(result['overdue'])
        self.assertEqual(result['next_review_at'],row['next_review_at'])
        self.assertEqual(self.store.list()['total'],0)
        self.assertEqual(self.store.list(status='all',due='overdue')['total'],0)
        self.assertEqual(self.store.list(status='closed')['total'],1)
        self.assertEqual(self.store.get(row['id'])['history_total'],2)

    def test_cannot_close_through_generic_update(self):
        row=self.create()['followup']
        with self.assertRaises(MailError):
            self.store.change(row['id'],1,'close-bypass',status='closed')
        self.assertEqual(self.store.get(row['id'])['followup']['revision'],1)

    def test_due_order_pagination_and_history_pagination(self):
        self.create('old',due='2026-10-17T00:00:00+00:00')
        row=self.create('today',due='2026-10-18T10:00:00+00:00')['followup']
        self.create('soon',due='2026-10-19T00:00:00+00:00')
        self.create('late',due='2026-12-01T00:00:00+00:00')
        self.assertEqual(self.store.list(due='overdue')['total'],2)
        self.assertEqual(self.store.list(due='upcoming',days=2)['total'],1)
        page=self.store.list(limit=1,offset=1)
        self.assertEqual(page['followups'][0]['id'],row['id'])
        self.assertTrue(page['has_more'])
        self.store.change(row['id'],1,'note',note='Verifica')
        self.assertTrue(self.store.get(row['id'],history_limit=1)['history_has_more'])
        self.assertEqual(self.store.get(row['id'],history_limit=1,history_offset=1)['history'][0]['action'],'create')

    def test_closed_issue_immutable(self):
        row=self.create()['followup']
        self.store.change(row['id'],1,'close',action='close',closure_reason='Risolto')
        with self.assertRaises(MailError):
            self.store.change(row['id'],2,'update-closed',note='Non autorizzato')

    def test_bad_reference_and_oversized_inputs(self):
        for refs in ([{**REF,'body':'non salvare'}],[{**REF,'message_uid':'1:*'}]):
            with self.assertRaises(MailError):
                self.create(email_references=refs)
        with self.assertRaises(MailError):
            self.create(people=['x']*21)
        with self.assertRaises(MailError):
            self.store.create('x'*201,'esito','2026-10-20T00:00:00Z','oversize')

    def test_sql_text_is_data(self):
        title="'); DROP TABLE followups;--"
        row=self.store.create(title,'esito','2026-10-20T00:00:00Z','sql')['followup']
        self.assertEqual(self.store.get(row['id'])['followup']['title'],title)
        self.assertEqual(self.store.list()['total'],1)

    def test_explicit_waiting_can_be_cleared(self):
        row=self.create(waiting_for='A')['followup']
        self.assertEqual(row['status'],'waiting')
        row=self.store.change(row['id'],1,'clear',waiting_for='',participants=[],status='needs_review')['followup']
        self.assertEqual(row['waiting_for'],'')
        self.assertEqual(row['status'],'needs_review')

    def test_snooze_past_and_missing_record(self):
        row=self.create()['followup']
        with self.assertRaises(MailError):
            self.store.change(row['id'],1,'past',action='snooze',next_review_at='2026-10-17T10:00:00Z')
        with self.assertRaises(MailError):
            self.store.get('missing')
        self.assertEqual(self.store.get(row['id'])['history_total'],1)

    def test_future_database_rejected(self):
        self.create()
        with sqlite3.connect(self.path) as db:
            db.execute('PRAGMA user_version=999')
        with self.assertRaises(MailError):
            self.store.list()
        with self.assertRaises(MailError):
            self.create('future')

    def test_insecure_permissions_rejected(self):
        self.create()
        if os.name == 'posix':
            self.path.chmod(0o644)
            with self.assertRaises(MailError):
                self.store.list()

    def test_mark_seen_independent_of_register(self):
        import server
        row=self.create()['followup']
        with patch('server.mail_client.mark_as_read',return_value={'read':True}) as mark:
            self.assertTrue(server.mark_as_read('test','INBOX','42','77')['read'])
            mark.assert_called_once()
        self.assertEqual(self.store.get(row['id'])['followup'],row)

    def test_register_operations_do_not_access_imap(self):
        with patch('mail_client.connect',side_effect=AssertionError('No IMAP')):
            row=self.create()['followup']
            self.store.change(row['id'],1,'note',note='Prova offline')
            self.store.list()
            self.store.change(row['id'],2,'close',action='close',closure_reason='Prova finita')


async def protocol_test():
    with tempfile.TemporaryDirectory() as directory:
        path=Path(directory)/'test.sqlite3'
        params=StdioServerParameters(command=sys.executable,args=[str(Path(__file__).with_name('server.py'))],env={**os.environ,'ARUBA_MAIL_DB_PATH':str(path)})
        async with stdio_client(params) as (r,w):
            async with ClientSession(r,w) as session:
                await session.initialize()
                tools=(await session.list_tools()).tools
                assert len(tools)==12
                for tool in tools:
                    if tool.name.endswith('followup') or tool.name=='list_followups':
                        assert tool.annotations.readOnlyHint == (tool.name in ('get_followup','list_followups'))
                async def call(name,args):
                    result=await session.call_tool(name,args)
                    assert not result.isError,result
                    return result.structuredContent or json.loads(result.content[0].text)
                row=(await call('create_followup',{'title':'Questione sintetica','expected_outcome':'Esito test','next_review_at':'2099-10-20T10:00:00+02:00','request_id':'mcp-create','participants':['A','B','C','D']}))['followup']
                assert row['waiting_for']==''
                row=(await call('update_followup',{'followup_id':row['id'],'expected_revision':1,'request_id':'mcp-update','note':'Risposta interlocutoria'}))['followup']
                assert row['status']=='open'
                await call('snooze_followup',{'followup_id':row['id'],'expected_revision':2,'request_id':'mcp-snooze','next_review_at':'2099-10-23T10:00:00+02:00'})
        # Start a fresh server process: same database, persisted history.
        async with stdio_client(params) as (r,w):
            async with ClientSession(r,w) as session:
                await session.initialize()
                result=await session.call_tool('get_followup',{'followup_id':row['id']})
                data=result.structuredContent or json.loads(result.content[0].text)
                assert data['followup']['revision']==3 and data['history_total']==3
                closed=await session.call_tool('close_followup',{'followup_id':row['id'],'expected_revision':3,'request_id':'mcp-close','closure_reason':'Esito test verificato'})
                data=closed.structuredContent or json.loads(closed.content[0].text)
                assert data['followup']['status']=='closed'
        print('PASS MCP follow-up: create/update/snooze/restart/get/close con persistenza reale, nessuna connessione IMAP')


if __name__=='__main__':
    result=unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(FollowupTests))
    if not result.wasSuccessful():
        sys.exit(1)
    asyncio.run(protocol_test())

import asyncio
import base64
from contextlib import contextmanager
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client
import mail_client as mc
from config import MailError

HEADER = b'From: Anna <anna@example.invalid>\r\nTo: me@example.invalid\r\nSubject: =?utf-8?b?Q2lhbyDDqA==?=\r\n\r\n'
TEXT = 'Ciao è una prova'.encode()
ENCODED = base64.b64encode(TEXT)
STRUCTURE = b'(("TEXT" "PLAIN" ("CHARSET" "UTF-8") NIL NIL "BASE64" ' + str(len(ENCODED)).encode() + b' 1 NIL NIL NIL)("APPLICATION" "PDF" ("NAME" "test.pdf") NIL NIL "BASE64" 2000 NIL ("ATTACHMENT" ("FILENAME" "test.pdf")) NIL) "MIXED" ("BOUNDARY" "b") NIL NIL)'


class FakeIMAP:
    def __init__(self):
        self.calls = []
        self.seen = False
        self.size = 3000
    def uid(self, op, *args):
        self.calls.append((op, args))
        if op == 'SEARCH':
            return 'OK', [b'41 42']
        if op == 'STORE':
            self.seen = True
            return 'OK', [b'']
        uid, fields = args
        flag = b'\\Seen' if self.seen else b''
        meta = b'1 (UID ' + uid.encode() + b' FLAGS (' + flag + b') RFC822.SIZE ' + str(self.size).encode()
        if 'BODYSTRUCTURE' in fields:
            return 'OK', [meta + b' BODYSTRUCTURE ' + STRUCTURE + b')']
        if 'HEADER.FIELDS' in fields:
            return 'OK', [(meta + b' BODY[HEADER.FIELDS] {100}', HEADER), b')']
        if 'BODY.PEEK[1]' in fields:
            return 'OK', [(meta + b' BODY[1] {20}', ENCODED), b')']
        raise AssertionError('Fetch di parte non autorizzata: ' + fields)


class MailTests(unittest.TestCase):
    def setUp(self):
        self.imap = FakeIMAP()
        self.modes = []
        @contextmanager
        def connect(account_id, folder=None, write=False):
            self.modes.append(write)
            yield self.imap, '77'
        self.mock = patch.object(mc, 'connect', connect)
        self.mock.start()
        self.addCleanup(self.mock.stop)
    def test_listing_only_headers(self):
        out = mc.list_emails('test', unread_only=True, limit=1)
        self.assertEqual(out['emails'][0]['message_uid'], '42')
        self.assertEqual(out['emails'][0]['uidvalidity'], '77')
        self.assertFalse(out['emails'][0]['read'])
        self.assertTrue(out['has_more'])
        self.assertIn('UNSEEN', self.imap.calls[0][1])
        self.assertTrue(all('HEADER.FIELDS' in args[1] for op,args in self.imap.calls if op == 'FETCH'))
    def test_read_base64_does_not_fetch_attachment_or_write(self):
        out = mc.read_email('test', 'INBOX', '42', '77')
        self.assertEqual(out['text'], TEXT.decode())
        self.assertEqual(out['attachments'][0]['filename'], 'test.pdf')
        self.assertEqual(self.modes, [False])
        self.assertFalse(self.imap.seen)
    def test_stale_validity_never_stores(self):
        with self.assertRaises(MailError):
            mc.mark_as_read('test', 'INBOX', '42', '76')
        self.assertEqual(self.imap.calls, [])
    def test_mark_seen_and_verify(self):
        self.assertTrue(mc.mark_as_read('test', 'INBOX', '42', '77')['read'])
        stores = [(op,args) for op,args in self.imap.calls if op == 'STORE']
        self.assertEqual(stores, [('STORE', ('42', '+FLAGS.SILENT', '(\\Seen)'))])
        self.assertEqual(self.modes, [True])
    def test_oversize_never_fetches_body(self):
        self.imap.size = mc.MAX_MESSAGE + 1
        with self.assertRaises(MailError):
            mc.read_email('test', 'INBOX', '42', '77')
        self.assertEqual(len(self.imap.calls), 1)
    def test_injection_and_folder_names(self):
        for uid in ('1:*', '0', '42\r\nSTORE', '4294967296'):
            with self.assertRaises(MailError):
                mc.mark_as_read('test', 'INBOX', uid, '77')
        with self.assertRaises(MailError):
            mc.quote('INBOX\r\nLOGOUT')
        for name in ('INBOX', 'Clienti & Fornitori', 'Città/日本', 'A"B'):
            self.assertEqual(mc.decode_folder(mc.encode_folder(name)), name)
    def test_literal_bound_before_read(self):
        obj = object.__new__(mc.BoundedIMAP)
        with self.assertRaises(MailError):
            obj.read(999999999)
    def test_html(self):
        html = mc.PlainHTML()
        html.feed('<p>Ciao &amp; grazie</p><script>segreto</script>')
        self.assertIn('Ciao & grazie', ''.join(html.text))
        self.assertNotIn('segreto', ''.join(html.text))
    def test_quoted_printable_html_read(self):
        text = b'<p>Citt=C3=A0 &amp; grazie</p>'
        structure = b'("TEXT" "HTML" ("CHARSET" "UTF-8") NIL NIL "QUOTED-PRINTABLE" ' + str(len(text)).encode() + b' 1 NIL NIL)'
        with patch(__name__ + '.ENCODED', text), patch(__name__ + '.STRUCTURE', structure):
            out = mc.read_email('test', 'INBOX', '42', '77')
        self.assertIn('Città & grazie', out['text'])
    def test_attached_multipart_excluded(self):
        structure = b'(("TEXT" "PLAIN" NIL NIL NIL "7BIT" 4 1) "MIXED" NIL ("ATTACHMENT" NIL))'
        out = mc.parts(mc.parse_structure(structure))
        self.assertTrue(out[0]['attachment'])
        self.assertEqual(len(out), 1)
    def test_connection_tls_readonly_and_logout(self):
        from unittest.mock import MagicMock
        fake = MagicMock()
        fake.login.return_value = ('OK', [b''])
        fake.select.return_value = ('OK', [b'1'])
        fake.response.return_value = ('UIDVALIDITY', [b'77'])
        self.mock.stop()
        with patch.object(mc, 'account', return_value={'imap_host':'example.invalid', 'username':'user', 'password':'SECRET'}), patch.object(mc, 'BoundedIMAP', return_value=fake) as factory:
            with mc.connect('test', 'INBOX') as (_, validity):
                self.assertEqual(validity, '77')
            self.assertTrue(factory.call_args.kwargs['ssl_context'].check_hostname)
            fake.select.assert_called_once_with('"INBOX"', readonly=True)
            fake.logout.assert_called_once()
            fake.close.assert_not_called()


async def protocol_test():
    params = StdioServerParameters(command=sys.executable, args=[str(Path(__file__).with_name('server.py'))])
    async with stdio_client(params) as (read, write):
        async with ClientSession(read, write) as session:
            await session.initialize()
            tools = (await session.list_tools()).tools
            assert {t.name for t in tools} == {'list_accounts','list_folders','list_emails','search_emails','read_email','mark_as_read','create_followup','list_followups','get_followup','update_followup','snooze_followup','close_followup'}
            assert next(t for t in tools if t.name == 'mark_as_read').annotations.readOnlyHint is False
            # Call only a nonexistent account, so this test cannot contact Aruba.
            result = await session.call_tool('list_emails', {'account_id': '__nonexistent_test__'})
            data = result.structuredContent or json.loads(result.content[0].text)
            assert 'error' in data
    print('PASS MCP stdio: 12 tool, nessun invio/spostamento, errore configurazione sicuro')


if __name__ == '__main__':
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(MailTests)
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    if not result.wasSuccessful():
        sys.exit(1)
    asyncio.run(protocol_test())

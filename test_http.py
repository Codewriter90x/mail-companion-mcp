"""HTTP protocol/auth tests: no real credentials or mailbox connections."""
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from starlette.testclient import TestClient
from http_transport import load_token, MAX_REQUEST
from server import http_app


class HTTPTests(unittest.TestCase):
    def test_http_auth_limits_and_discovery(self):
        token = 'test-only-not-a-real-secret-' + 'x' * 32
        app = http_app(token=token)
        with tempfile.TemporaryDirectory() as directory, patch.dict('os.environ', {'ARUBA_MAIL_DB_PATH':str(Path(directory)/'http.sqlite3')}), TestClient(app, base_url='http://localhost:8000') as client:
            request = {'jsonrpc':'2.0','id':1,'method':'initialize','params':{'protocolVersion':'2025-06-18','capabilities':{},'clientInfo':{'name':'test','version':'1'}}}
            self.assertEqual(client.post('/mcp', json=request).status_code, 401)
            self.assertEqual(client.get('/mcp').status_code, 401)
            headers = {'Authorization':'Bearer wrong', 'Accept':'application/json, text/event-stream'}
            self.assertEqual(client.post('/mcp',json=request, headers=headers).status_code, 401)
            headers['Authorization'] = 'Bearer ' + token
            init = client.post('/mcp',json=request,headers=headers)
            self.assertEqual(init.status_code,200,init.text)
            self.assertIn('serverInfo', init.json()['result'])
            headers['MCP-Protocol-Version'] = '2025-06-18'
            tools = client.post('/mcp',json={'jsonrpc':'2.0','id':2,'method':'tools/list'},headers=headers)
            self.assertEqual(tools.status_code,200,tools.text)
            self.assertEqual(len(tools.json()['result']['tools']),12)
            def call(name, arguments):
                response = client.post('/mcp',json={'jsonrpc':'2.0','id':5,'method':'tools/call','params':{'name':name,'arguments':arguments}},headers=headers)
                self.assertEqual(response.status_code,200,response.text)
                result = response.json()['result']
                self.assertFalse(result.get('isError',False),result)
                return result.get('structuredContent') or json.loads(result['content'][0]['text'])
            created = call('create_followup',{'title':'Test HTTP','expected_outcome':'Esito','next_review_at':'2099-10-20T10:00:00+02:00','request_id':'http-create'})
            self.assertEqual(call('list_followups',{})['total'],1)
            identity=created['followup']['id']
            detail=call('get_followup',{'followup_id':identity})
            self.assertEqual(detail['history_total'],1)
            self.assertTrue(call('create_followup',{'title':'Test HTTP','expected_outcome':'Esito','next_review_at':'2099-10-20T10:00:00+02:00','request_id':'http-create'})['replayed'])
            call('close_followup',{'followup_id':identity,'expected_revision':1,'request_id':'http-close','closure_reason':'Test finito'})
            self.assertEqual(call('list_followups',{})['total'],0)
            # Invalid arguments are checked before any config or mailbox access.
            result = client.post('/mcp',json={'jsonrpc':'2.0','id':3,'method':'tools/call','params':{'name':'read_email','arguments':{}}},headers=headers)
            self.assertTrue(result.json()['result']['isError'])
            too_big = client.post('/mcp',content=b'x'*(MAX_REQUEST+1),headers=headers)
            self.assertEqual(too_big.status_code,413)
            bad_host = client.post('/mcp',json=request,headers={**headers,'Host':'untrusted.invalid:8000'})
            self.assertEqual(bad_host.status_code,421)

    def test_token_file_required_and_permissions(self):
        with patch.dict('os.environ',{},clear=True):
            with self.assertRaises(ValueError):
                load_token()
        with tempfile.TemporaryDirectory() as directory:
            file = Path(directory)/'mcp.token'
            file.write_text('x'*64)
            file.chmod(0o644)
            with patch.dict('os.environ',{'ARUBA_MAIL_MCP_TOKEN_FILE':str(file)}):
                with self.assertRaises(ValueError):
                    load_token()
                file.chmod(0o600)
                self.assertEqual(load_token(),'x'*64)

if __name__ == '__main__':
    unittest.main(verbosity=2)

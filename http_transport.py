"""Private Streamable HTTP: manually configured Bearer token, no OAuth server."""
import hmac
import os
from pathlib import Path
import stat

from starlette.responses import JSONResponse

MAX_REQUEST = 64 * 1024


def load_token():
    path = os.environ.get('ARUBA_MAIL_MCP_TOKEN_FILE', '')
    if not path:
        raise ValueError('HTTP richiede ARUBA_MAIL_MCP_TOKEN_FILE con un token locale protetto.')
    file = Path(path)
    if os.name == 'posix' and stat.S_IMODE(file.stat().st_mode) & 0o077:
        raise ValueError('Il file token deve avere permessi 600.')
    token = file.read_text().strip()
    if not 32 <= len(token) <= 512 or not token.isascii() or any(c.isspace() for c in token):
        raise ValueError('Token non valido: usare almeno 32 caratteri casuali ASCII senza spazi.')
    return token


class PrivateHTTP:
    def __init__(self, app, token):
        self.app = app
        self.expected = ('Bearer ' + token).encode('ascii')

    async def __call__(self, scope, receive, send):
        if scope['type'] != 'http':
            return await self.app(scope, receive, send)
        headers = scope.get('headers', [])
        authorization = [v for k,v in headers if k.lower() == b'authorization']
        if len(authorization) != 1 or not hmac.compare_digest(authorization[0], self.expected):
            return await JSONResponse({'error': 'Autenticazione richiesta'}, status_code=401, headers={'WWW-Authenticate': 'Bearer'})(scope, receive, send)
        lengths = [v for k,v in headers if k.lower() == b'content-length']
        if lengths:
            if len(lengths) != 1 or not lengths[0].isdigit():
                return await JSONResponse({'error': 'Content-Length non valido'}, status_code=400)(scope, receive, send)
            if int(lengths[0]) > MAX_REQUEST:
                return await JSONResponse({'error': 'Richiesta troppo grande'}, status_code=413)(scope, receive, send)
        body = bytearray()
        if scope['method'] in ('POST', 'PUT', 'PATCH'):
            while True:
                event = await receive()
                if event['type'] == 'http.disconnect':
                    return
                body.extend(event.get('body', b''))
                if len(body) > MAX_REQUEST:
                    return await JSONResponse({'error': 'Richiesta troppo grande'}, status_code=413)(scope, receive, send)
                if not event.get('more_body', False):
                    break
            delivered = False
            async def replay():
                nonlocal delivered
                if not delivered:
                    delivered = True
                    return {'type': 'http.request', 'body': bytes(body), 'more_body': False}
                return await receive()
            return await self.app(scope, replay, send)
        return await self.app(scope, receive, send)

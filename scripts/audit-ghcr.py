#!/usr/bin/env python3
"""Read-only audit of every tagged GHCR manifest, attestation, config and layer.
Uses an ephemeral Actions token. Reports locations/categories only, never values.
Base-image files are inventoried too; no archive is extracted or executed.
"""
import base64
import hashlib
import io
import json
import os
from pathlib import Path
import re
import tarfile
import tempfile
import urllib.parse
import urllib.request

PACKAGE = 'codewriter90x/mail-companion-mcp'
REGISTRY = 'https://ghcr.io'
MAX_BLOB = 256 * 1024 * 1024
MAX_FILE = 64 * 1024 * 1024
PATTERNS = {
    'private_key_material': re.compile(rb'-----BEGIN (?:[A-Z ]+ )?PRIVATE KEY-----\s+[A-Za-z0-9+/=\r\n]{64,}-----END'),
    'provider_credential': re.compile(rb'\b(?:gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{30,}|sk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{24,}|AKIA[0-9A-Z]{16}|xox[baprs]-[A-Za-z0-9-]{20,})'),
    'developer_home_path': re.compile(rb'/(?:Users|home)/(?!app(?:/|\b)|ubuntu(?:/|\b)|runner(?:/|\b))[A-Za-z0-9_.-]+/'),
    'credential_url': re.compile(rb'https?://[^\s/<>"\x27:]+:[^\s/<>"\x27@]+@'),
}
SENSITIVE = re.compile(r'(?:^|/)(?:accounts[^/]*\.local\.json|connection\.local\.json|apple-reminders\.local\.json|[^/]+\.(?:token|secret|sqlite3?|db|pfx|p12)|\.env(?:\.[^/]*)?|\.git)(?:/|$)', re.I)
ACCEPT = ', '.join(['application/vnd.oci.image.index.v1+json', 'application/vnd.oci.image.manifest.v1+json', 'application/vnd.docker.distribution.manifest.list.v2+json', 'application/vnd.docker.distribution.manifest.v2+json'])


def digest_ok(digest):
    if not re.fullmatch(r'sha256:[0-9a-f]{64}', digest):
        raise ValueError('Unsupported digest')
    return digest


class SafeRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        redirect = super().redirect_request(req, fp, code, msg, headers, newurl)
        if redirect and urllib.parse.urlsplit(req.full_url).netloc != urllib.parse.urlsplit(newurl).netloc:
            redirect.remove_header('Authorization')
        return redirect


class Audit:
    def __init__(self, token):
        self.token = token
        self.seen = set()
        self.findings = []
        self.files = set()
        self.app_files = set()
        self.configs = []
        self.architectures = set()
        self.bytes = 0
        self.layers = 0
        self.manifests = 0

    def request(self, path, accept=None):
        headers = {'Authorization': 'Bearer ' + self.token, 'User-Agent': 'mail-companion-image-audit'}
        if accept:
            headers['Accept'] = accept
        return urllib.request.build_opener(SafeRedirect()).open(urllib.request.Request(REGISTRY + path, headers=headers), timeout=120)

    def blob(self, digest):
        digest_ok(digest)
        tmp = tempfile.TemporaryFile()
        sha = hashlib.sha256()
        total = 0
        with self.request('/v2/' + PACKAGE + '/blobs/' + digest) as response:
            while chunk := response.read(1024 * 1024):
                total += len(chunk)
                if total > MAX_BLOB:
                    raise ValueError('Blob exceeds audit bound')
                sha.update(chunk)
                tmp.write(chunk)
        if sha.hexdigest() != digest.split(':')[1]:
            raise ValueError('Blob digest mismatch')
        self.bytes += total
        tmp.seek(0)
        return tmp

    def scan(self, data, location):
        # Native binaries may store strings as UTF-16; scan both representations.
        for category, pattern in PATTERNS.items():
            if pattern.search(data) or pattern.search(data.replace(b'\0', b'')):
                self.findings.append({'category': category, 'location': location})

    def layer(self, digest, media_type):
        if digest in self.seen:
            return
        self.seen.add(digest)
        self.layers += 1
        with self.blob(digest) as blob:
            if 'in-toto' in media_type or media_type.endswith('+json'):
                data = blob.read(MAX_FILE + 1)
                if len(data) > MAX_FILE:
                    raise ValueError('Attestation exceeds audit bound')
                self.scan(data, 'attestation/' + digest)
                return
            # Stream the complete tar, including files later deleted in newer layers.
            with tarfile.open(fileobj=blob, mode='r|*') as archive:
                for member in archive:
                    name = member.name.removeprefix('./').lstrip('/')
                    if member.isfile():
                        self.files.add(name)
                        if name.startswith('app/'):
                            self.app_files.add(name)
                        if SENSITIVE.search(name):
                            self.findings.append({'category': 'sensitive_file', 'location': name})
                        if member.size > MAX_FILE:
                            raise ValueError('File exceeds audit bound: ' + name)
                        data = archive.extractfile(member).read()
                        self.scan(data, name)
                        if name == 'app/accounts.example.json':
                            config = json.loads(data)
                            if any(account.get(k) for account in config.get('accounts', []) for k in ['password', 'username', 'email']):
                                self.findings.append({'category': 'nonempty_example_credentials', 'location': name})

    def manifest(self, reference, platform=None):
        if reference in self.seen:
            return
        self.seen.add(reference)
        if reference.startswith('sha256:'):
            digest_ok(reference)
        elif not re.fullmatch(r'[A-Za-z0-9_.-]+', reference):
            raise ValueError('Invalid tag')
        with self.request('/v2/' + PACKAGE + '/manifests/' + reference, ACCEPT) as response:
            data = response.read(4 * 1024 * 1024 + 1)
        if len(data) > 4 * 1024 * 1024:
            raise ValueError('Manifest exceeds audit bound')
        if reference.startswith('sha256:') and hashlib.sha256(data).hexdigest() != reference.split(':')[1]:
            raise ValueError('Manifest digest mismatch')
        value = json.loads(data)
        self.manifests += 1
        self.scan(data, 'manifest/' + reference)
        for child in value.get('manifests', []):
            self.manifest(child['digest'], child.get('platform'))
        config = value.get('config')
        if config:
            with self.blob(config['digest']) as blob:
                data = blob.read(4 * 1024 * 1024 + 1)
            if len(data) > 4 * 1024 * 1024:
                raise ValueError('Config exceeds audit bound')
            self.scan(data, 'config/' + reference)
            info = json.loads(data)
            if info.get('architecture') in ['amd64', 'arm64']:
                self.architectures.add(info['architecture'])
                runtime = info.get('config', {})
                env = runtime.get('Env', [])
                unexpected = [e.split('=', 1)[0] for e in env if re.search(r'(?i)(password|passwd|secret|token|api_?key)', e.split('=', 1)[0])]
                if unexpected:
                    self.findings.append({'category': 'credential_environment', 'location': 'config/' + reference, 'keys': unexpected})
                self.configs.append({'architecture': info['architecture'], 'user': runtime.get('User'), 'environment_keys': [e.split('=', 1)[0] for e in env], 'labels': list(runtime.get('Labels', {})), 'history_entries': len(info.get('history', []))})
        for layer in value.get('layers', []):
            self.layer(layer['digest'], layer.get('mediaType', ''))

    def run(self):
        # Follow explicit tag pages, so immutable older published tags are checked too.
        tags = []
        path = '/v2/' + PACKAGE + '/tags/list?n=100'
        while path:
            with self.request(path) as response:
                tags.extend(json.load(response).get('tags') or [])
                link = response.headers.get('Link', '')
            match = re.search(r'<([^>]+)>;\s*rel="next"', link)
            path = None
            if match:
                target = urllib.parse.urlsplit(urllib.parse.urljoin(REGISTRY, match.group(1)))
                if target.netloc != 'ghcr.io' or not target.path.startswith('/v2/' + PACKAGE + '/tags/list'):
                    raise ValueError('Unexpected registry pagination link')
                path = target.path + ('?' + target.query if target.query else '')
        if not tags:
            raise ValueError('No tags to audit')
        for tag in sorted(set(tags)):
            self.manifest(tag)
        if self.architectures != {'amd64', 'arm64'}:
            raise ValueError('Both runtime architectures must be audited')
        return {'package': 'ghcr.io/' + PACKAGE, 'tags': sorted(set(tags)), 'architectures': sorted(self.architectures), 'manifest_count': self.manifests, 'unique_layers_and_attestations': self.layers, 'unique_files_in_all_layers': len(self.files), 'compressed_bytes_downloaded': self.bytes, 'app_files': sorted(self.app_files), 'runtime_configs': self.configs, 'findings': self.findings, 'checks': ['all tagged versions and referenced architecture/attestation manifests', 'every tar layer including subsequently deleted files', 'manifest and config metadata, history, environment', 'sensitive configuration/database/token paths', 'private keys, provider credentials, credential URLs, developer home paths', 'blank example credentials'], 'limitations': ['Pattern detection cannot prove absence of every possible secret.', 'Standard OS and runtime package author identities are vendor metadata.', 'Only versions reachable from current registry tags are enumerated.']}


def main():
    username = os.environ['GHCR_AUDIT_USER']
    password = os.environ['GHCR_AUDIT_TOKEN']
    basic = base64.b64encode((username + ':' + password).encode()).decode()
    scope = urllib.parse.urlencode({'service': 'ghcr.io', 'scope': 'repository:' + PACKAGE + ':pull'})
    request = urllib.request.Request(REGISTRY + '/token?' + scope, headers={'Authorization': 'Basic ' + basic})
    with urllib.request.urlopen(request, timeout=30) as response:
        token = json.load(response)['token']
    result = Audit(token).run()
    output = Path(os.environ.get('GHCR_AUDIT_REPORT', 'image-audit.json'))
    output.write_text(json.dumps(result, indent=2))
    print(json.dumps({k: v for k, v in result.items() if k not in ['app_files', 'runtime_configs']}, indent=2))
    if result['findings']:
        raise SystemExit('Image requires manual review; no visibility change performed.')


if __name__ == '__main__':
    main()

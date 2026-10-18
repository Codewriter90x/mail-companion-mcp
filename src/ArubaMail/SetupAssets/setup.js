'use strict';
const $ = id => document.getElementById(id);
let access = location.hash.slice(1);
if (access) history.replaceState(null, '', location.pathname);
let state = null;
let editing = null;
let busy = false;
function status(message, error = false) {
  $('status').textContent = message;
  $('status').classList.toggle('error', error);
  $('status').hidden = false;
}
async function api(path, data) {
  const response = await fetch('/setup/api/' + path, {
    method: data === undefined ? 'GET' : 'POST',
    headers: { Authorization: 'Bearer ' + access, ...(data === undefined ? {} : { 'Content-Type': 'application/json' }) },
    body: data === undefined ? undefined : JSON.stringify(data), cache: 'no-store', credentials: 'omit'
  });
  if (response.status === 401) { lock(); throw new Error('Accesso scaduto o codice non valido. Riapri il pannello dal comando di configurazione.'); }
  const value = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(response.status === 429 ? 'Troppe richieste: attendi un momento e riprova.' : value.error || 'Operazione non riuscita. Riprova.');
  return value;
}
async function action(run) {
  if (busy) return;
  busy = true;
  document.querySelectorAll('button').forEach(b => b.disabled = true);
  try { await run(); } catch (error) { status(error.message, true); }
  finally { busy = false; document.querySelectorAll('button').forEach(b => b.disabled = false); }
}
function lock() {
  access = ''; state = null; editing = null;
  $('password').value = ''; $('access-code').value = '';
  $('token-fallback').value = ''; $('token-fallback').hidden = true;
  $('account-form').reset(); $('account-list').replaceChildren();
  $('app').hidden = true; $('login').hidden = false;
  $('status').hidden = true;
}
function tabs(connection) {
  $('status').hidden = true;
  $('accounts-panel').hidden = connection; $('connection-panel').hidden = !connection;
  $('tab-accounts').setAttribute('aria-selected', String(!connection));
  $('tab-connection').setAttribute('aria-selected', String(connection));
}
function listAccounts() {
  $('account-list').replaceChildren(); $('account-count').textContent = state.accounts.length;
  for (const account of state.accounts) {
    const button = document.createElement('button'); button.type = 'button'; button.className = 'account-item';
    button.setAttribute('aria-current', String(editing === account.id));
    const title = document.createElement('span'); title.textContent = account.id;
    const email = document.createElement('small'); email.textContent = account.email;
    button.append(title, email); button.addEventListener('click', () => editAccount(account));
    $('account-list').append(button);
  }
  if (!state.accounts.length) { const p = document.createElement('p'); p.className = 'hint'; p.textContent = 'Non hai ancora aggiunto caselle.'; $('account-list').append(p); }
}
function editAccount(account = null) {
  editing = account?.id || null;
  $('account-form').reset();
  for (const [id, key] of [['account-id', 'id'], ['email', 'email'], ['username', 'username'], ['imap-host', 'imap_host'], ['imap-port', 'imap_port']])
    $(id).value = account?.[key] ?? (id === 'imap-port' ? 993 : '');
  $('account-id').disabled = !!account; $('password').value = ''; $('password').required = !account;
  $('saved-password').hidden = !account; $('delete-account').hidden = !account;
  $('account-title').textContent = account ? 'Configura ' + account.id : 'Aggiungi una casella';
  $('password-hint').textContent = account ? 'Lascia vuoto per conservare la password salvata.' : 'Inserisci la password della casella o quella applicativa.';
  listAccounts();
}
function accountInput() {
  return { id: $('account-id').value.trim(), email: $('email').value.trim(), username: $('username').value.trim(),
    password: $('password').value || null, imap_host: $('imap-host').value.trim(), imap_port: Number($('imap-port').value) };
}
function profileInput() {
  return { client: $('client').value, transport: $('transport').value, port: Number($('http-port').value), webui_location: $('webui-location').value };
}
function connectionFields(dirty = false) {
  const webui = $('client').value === 'open-webui';
  $('transport').querySelector('[value="stdio"]').disabled = webui;
  if (webui) $('transport').value = 'streamable-http';
  const http = $('transport').value === 'streamable-http';
  $('http-options').hidden = !http; $('webui-location-field').hidden = !webui;
  $('http-port-field').hidden = webui && $('webui-location').value === 'docker';
  $('transport-hint').textContent = http
    ? 'Il bridge espone un indirizzo MCP protetto da token Bearer. Mantienilo sul computer o su una rete Docker privata.'
    : 'Il client avvia il bridge in locale attraverso stdin/stdout. Non servono porte o token MCP.';
  if (dirty) { $('instructions').hidden = true; $('token-fallback').value = ''; $('token-fallback').hidden = true; }
}
function renderProfile() {
  $('client').value = state.profile.client; $('transport').value = state.profile.transport;
  $('http-port').value = state.profile.port; $('webui-location').value = state.profile.webui_location;
  connectionFields();
  const instructions = state.instructions;
  $('instructions').hidden = false; $('instruction-note').textContent = instructions.note;
  $('endpoint-block').hidden = state.profile.transport !== 'streamable-http';
  $('endpoint').textContent = instructions.endpoint;
  $('connect-command').textContent = instructions.connect;
  $('copy-command').textContent = state.profile.client === 'codex' ? 'Copia comando' : 'Copia istruzioni';
  $('command-label').textContent = state.profile.client === 'codex' ? 'Comando per Codex' : 'Configurazione nel client';
  $('docker-command').textContent = instructions.stdio_docker;
  $('http-command').textContent = instructions.start_http;
  $('token-result').textContent = ''; $('token-fallback').value = ''; $('token-fallback').hidden = true;
}
async function login() {
  state = await api('state'); $('login').hidden = true; $('app').hidden = false;
  $('access-code').value = ''; editAccount(state.accounts[0] || null); renderProfile();
}
$('login-form').addEventListener('submit', event => { event.preventDefault(); access = $('access-code').value.trim(); action(login); });
$('logout').addEventListener('click', lock);
$('reload').addEventListener('click', () => action(async () => { await login(); status('Configurazione ricaricata.'); }));
$('tab-accounts').addEventListener('click', () => tabs(false));
$('tab-connection').addEventListener('click', () => tabs(true));
$('new-account').addEventListener('click', () => { editAccount(); $('account-id').focus(); });
$('email').addEventListener('blur', () => { if (!$('username').value) $('username').value = $('email').value; });
$('account-form').addEventListener('submit', event => { event.preventDefault(); action(async () => {
  const id = $('account-id').value.trim(); state = await api('accounts/save', { revision: state.revision, account: accountInput() });
  editAccount(state.accounts.find(a => a.id === id)); status('Casella salvata sul computer. Password protetta e non mostrata.');
}); });
$('test-account').addEventListener('click', () => {
  if (!$('account-form').reportValidity()) return;
  action(async () => { status('Verifico connessione TLS e accesso IMAP…'); const result = await api('accounts/test', accountInput()); status(result.message); });
});
$('delete-account').addEventListener('click', () => {
  if (!editing || !confirm('Rimuovere questa casella dalla configurazione? Le email sul server e le questioni nel registro resteranno disponibili.')) return;
  action(async () => { state = await api('accounts/delete', { revision: state.revision, id: editing }); editAccount(state.accounts[0] || null); status('Casella rimossa dalla configurazione. Nessuna email eliminata.'); });
});
$('client').addEventListener('change', () => { $('transport').value = $('client').value === 'open-webui' ? 'streamable-http' : 'stdio'; connectionFields(true); });
for (const id of ['transport', 'http-port', 'webui-location']) $(id).addEventListener('change', () => connectionFields(true));
$('connection-form').addEventListener('submit', event => { event.preventDefault(); action(async () => {
  state = await api('profile', { revision: state.profile_revision, profile: profileInput() }); renderProfile(); status('Collegamento salvato. Segui i passi qui sotto per attivarlo nel client.');
}); });
$('copy-command').addEventListener('click', () => action(async () => {
  try { await navigator.clipboard.writeText(state.instructions.connect); status('Comando copiato.'); }
  catch { status('Seleziona e copia il comando mostrato sopra.'); }
}));
$('copy-token').addEventListener('click', () => action(async () => {
  const value = await api('token', {});
  try { await navigator.clipboard.writeText(value.token); $('token-result').textContent = 'Token copiato: incollalo nel campo Bearer del client. Non condividerlo in chat.'; }
  catch { $('token-fallback').value = value.token; $('token-fallback').hidden = false; $('token-fallback').type = 'text'; $('token-fallback').select(); $('token-result').textContent = 'Copia il token selezionato e incollalo nel campo Bearer del client.'; }
}));
window.addEventListener('pagehide', lock);
window.addEventListener('hashchange', () => {
  const incoming = location.hash.slice(1);
  if (incoming) {
    access = incoming;
    history.replaceState(null, '', location.pathname);
    action(login);
  }
});
if (access) action(login);

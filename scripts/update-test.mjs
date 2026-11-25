#!/usr/bin/env node
/**
 * The real update, end to end, on one machine (wiki AUN-APP-UPDATES §13): an installed test build N finds N+1 on a
 * local server, checks its signature and hash, installs it as it closes, and starts as N+1. Keys are made for the run.
 * The app is closed, and Restart now pressed, from outside as a person does (scripts/update-person.ps1).
 *
 *   update-test.mjs --app viewer|studio --exe <installed exe> --package <N+1 nupkg> --from <N> --to <N+1>
 *                   --packages <Velopack's package folder> --package-name <its file name there>
 *                   [--case ok|restart|tampered|bad-signature|foreign-host|planted|swapped|yanked|working|twice|older]
 *
 * `ok` (as it closes) and `restart` (Restart now) must end on N+1. Every other case must stay on N through a second start:
 *  - tampered, bad-signature, foreign-host: the download is refused and the reason reported;
 *  - planted: a good N+1 package is already in the package folder, but this run verified none;
 *  - swapped: the staged file is replaced (same size) before the app closes;
 *  - yanked: after staging, a newer key list yanks N+1; the app learns it on a check and then closes;
 *  - working (Studio): the update is ready while an encode runs: Restart now does nothing, and closing installs nothing;
 *  - older: the manifest of a newer release was checked, its package was refused and the app ended; started again, it is
 *    offered N+1, which is older than what it knows of: what it knew was kept;
 *  - twice: the app is open a second time, through a junction: closing the one installs nothing and leaves the other running.
 */
import { createHash } from 'node:crypto';
import { createServer } from 'node:http';
import { spawn, spawnSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, readFileSync, symlinkSync, writeFileSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { generateKey, signBytes, buildKeyList } from './app-release.mjs';

const a = {};
for (let i = 2; i < process.argv.length; i += 2) a[process.argv[i].slice(2)] = process.argv[i + 1];
const kase = a.case ?? 'ok';
const REPO = 'AlUmmahNowDesktop';
const baseOf = (version) => `https://github.com/Al-Ummah-Now/${REPO}/releases/download/v${version}/`;
const tag = `v${a.to}`;
const later = '99.0.0';
const dir = mkdtempSync(join(tmpdir(), 'aun-update-test-'));
const log = join(dir, 'log.txt');
const state = join(dir, 'updates.json');
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const lines = () => (existsSync(log) ? readFileSync(log, 'utf8').trim().split('\n') : []);
const ready = (l) => l.startsWith(`Available Ready ${a.to}`);

const [root, release, stranger] = [await generateKey(), await generateKey(), await generateKey()];
const serial = Math.floor(Date.now() / 1000);
const list = Buffer.from(JSON.stringify(await buildKeyList({ repo: REPO, serial, keys: [release.publicKey] })));
const yankedList = Buffer.from(JSON.stringify(await buildKeyList({ repo: REPO, serial: serial + 1, keys: [release.publicKey], yanked: [a.to] })));
const stagedFile = join(a.packages, a['package-name']);
const pkg = readFileSync(a.package);
const manifestOf = (version) => Buffer.from(
	JSON.stringify({
		schema: 1,
		repo: REPO,
		tag: `v${version}`,
		version,
		channel: 'stable',
		publishedAt: new Date().toISOString().replace(/\.\d{3}Z$/, 'Z'),
		notes: { en: 'Test.', ar: 'اختبار.' },
		artifacts: [{ app: a.app, platform: 'windows', arch: 'x64', kind: 'velopack-full', url: `${baseOf(version)}update.nupkg`, sha256: createHash('sha256').update(pkg).digest('hex'), size: pkg.byteLength }],
		stores: [],
		yanked: [],
	}),
);
const served = Buffer.from(pkg);
const changed = Buffer.from(pkg);
changed[changed.length >> 1] ^= 1;
if (kase === 'tampered') served[served.length >> 1] ^= 1;
const manifests = new Map();
for (const version of [a.to, later]) {
	const manifest = manifestOf(version);
	manifests.set(version, { manifest, sig: await signBytes(manifest, [kase === 'bad-signature' || kase === 'planted' ? stranger.privateKey : release.privateKey]) });
}
const asset = kase === 'foreign-host' ? 'https://downloads.example.com/update.nupkg' : 'https://release-assets.githubusercontent.com/update.nupkg';
const configWith = async (keys, version = a.to) =>
	JSON.stringify({
		update: { enabled: true, status: 'recommended', minimumVersion: '0.0.0', latestVersion: version, storeUrl: null, channel: 'stable', publishedAt: null, notes: null, message: null, action: 'install', url: null, manifestUrl: `${baseOf(version)}app-release.json`, tag: `v${version}`, signed: true, available: true, checkHours: 6, keys: { list: keys.toString(), sig: await signBytes(keys, [root.privateKey]) } },
		config: {},
	});
let config = await configWith(list, kase === 'older' ? later : a.to);
const calls = [];
let reported = null;
let asked = null;
const server = createServer((req, res) => {
	const path = req.url.split('?')[0];
	calls.push(path);
	const ok = (body) => (res.writeHead(200, { 'Content-Length': Buffer.byteLength(body) }), res.end(body));
	if (path.endsWith('/api/v1/app-config')) return (asked = new URL(req.url, 'http://x').searchParams), ok(config);
	if (path.endsWith('/api/v1/app-config/update-failure')) {
		let body = '';
		req.on('data', (d) => (body += d));
		req.on('end', () => ((reported = JSON.parse(body)), res.writeHead(202), res.end()));
		return;
	}
	for (const [version, { manifest, sig }] of manifests) {
		const gh = `/github.com${new URL(baseOf(version)).pathname}`;
		if (path === `${gh}app-release.json`) return ok(manifest);
		if (path === `${gh}app-release.json.sig`) return ok(sig);
		// The later release's package is not the one its manifest lists
		if (path === `${gh}update.nupkg` && version === later) return ok(changed);
		if (path === `${gh}update.nupkg`) return res.writeHead(302, { Location: asset }), res.end();
	}
	if (path === '/release-assets.githubusercontent.com/update.nupkg' || path === '/downloads.example.com/update.nupkg') return ok(served);
	res.writeHead(404);
	res.end();
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const env = {
	...process.env,
	// The demo catalog and accounts: the app itself asks nothing of a server
	AUN_DEMO: '1',
	AUN_LANG: 'en',
	AUN_UPDATE_TEST_ROOT: root.publicKey,
	AUN_UPDATE_TEST_SERVER: `http://127.0.0.1:${server.address().port}`,
	AUN_UPDATE_TEST_LOG: log,
	AUN_UPDATE_TEST_STATE: state,
	...(kase === 'yanked' ? { AUN_UPDATE_TEST_RECHECK: '1' } : {}),
	// Studio's demo queue: one encode done, one running (after 5 s)
	...(kase === 'working' ? { AUN_SCENE: 'active', AUN_UPDATE_TEST_CHECK_MS: '15000' } : {}),
};
// Each case starts as a new install, with a state of its own: a yank is remembered for good, so an earlier case's would count here
if (kase === 'planted') {
	mkdirSync(a.packages, { recursive: true });
	writeFileSync(stagedFile, pkg);
}
const start = (more = {}, exe = a.exe) => spawn(exe, [], { env: { ...env, ...more }, stdio: 'inherit' });
const exited = (p) => new Promise((r) => (p.exitCode !== null || p.signalCode !== null ? r() : p.once('exit', r)));
function fail(why) {
	console.error(`::error::update test (${a.app}, ${kase}): ${why}\nlog:\n${lines().join('\n')}\ncalls:\n${calls.join('\n')}\nreported: ${JSON.stringify(reported)}`);
	process.exit(1);
}
async function until(what, ms, test) {
	const end = Date.now() + ms;
	while (Date.now() < end) {
		if (test()) return;
		await sleep(500);
	}
	fail(`timed out: ${what}`);
}
/** What a person does with the app's window */
function person(p, action) {
	const script = join(dirname(fileURLToPath(import.meta.url)), 'update-person.ps1');
	const done = spawnSync('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', script, '-Id', String(p.pid), '-Action', action], { encoding: 'utf8' });
	const said = (done.stdout ?? '').trim();
	console.log(`person: ${said}`);
	if (done.status !== 0) fail(`${action}: ${said} ${done.stderr}`);
	return said;
}
/** Its close button is pressed, and it ends: an app that hangs is not one that was not updated */
async function closes(p) {
	person(p, 'close');
	await ends(p);
}
async function ends(p) {
	if ((await Promise.race([exited(p), sleep(60_000).then(() => 'late')])) === 'late') fail('the app did not close');
}
/** Through the updater's time and another start, the app is still N */
async function unchanged() {
	await sleep(10_000);
	const before = lines().length;
	const second = start();
	const started = () => lines().slice(before).find((l) => l.startsWith('started '));
	await until('the app starts again', 60_000, started);
	second.kill();
	await exited(second);
	if (started() !== `started ${a.from} self=true`) fail('the version changed');
}

const first = start();
await until('the app starts', 60_000, () => lines().some((l) => l.startsWith('started ')));
if (lines()[0] !== `started ${a.from} self=true`) fail(`the installed app is not a self-updating ${a.from}`);
const staged = () => until('the update is staged', 180_000, () => lines().some(ready));
if (kase === 'ok') {
	await staged();
	await closes(first);
	// Velopack's updater runs after the exit; starting again before it's done would race it
	await sleep(25_000);
	const before = lines().length;
	const second = start();
	const started = () => lines().slice(before).find((l) => l.startsWith('started '));
	await until('the app starts again', 60_000, started);
	second.kill();
	await exited(second);
	if (started() !== `started ${a.to} self=true`) fail(`after the update: ${started()}`);
	if (reported) fail('a good update was reported as failed');
	if (asked.get('source') !== 'direct' || asked.get('app') !== a.app || asked.get('version') === null) fail(`asked as ${asked}`);
} else if (kase === 'restart') {
	await staged();
	if (person(first, 'press') !== "pressed 'Restart now'") fail('Restart now was not pressed');
	await ends(first);
	// The updater itself starts the new version
	await until('the new version starts', 90_000, () => lines().some((l) => l === `started ${a.to} self=true`));
	if (reported) fail('a good update was reported as failed');
	// It runs on: nothing is newer than it, and nothing is staged again
	const before = lines().length;
	await sleep(10_000);
	if (lines().slice(before).some(ready)) fail('it wants to update again');
} else if (kase === 'swapped' || kase === 'yanked') {
	await staged();
	if (kase === 'swapped') {
		if (!existsSync(stagedFile)) fail(`nothing staged at ${stagedFile}`);
		const other = Buffer.from(pkg);
		other[other.length >> 1] ^= 1;
		writeFileSync(stagedFile, other);
	} else config = await configWith(yankedList);
	if (kase === 'yanked') {
		await until('it checks again', 60_000, () => lines().some((l) => l.startsWith('rechecked ')));
		if (!lines().includes('rechecked Hidden')) fail('the yank did not cancel the staged update');
	}
	await closes(first);
	if (kase === 'yanked' && reported?.reason !== 'yanked') fail(`reported ${reported?.reason}, expected yanked`);
	await unchanged();
} else if (kase === 'older') {
	await until('the later release is refused', 180_000, () => reported !== null);
	if (reported.reason !== 'hash' || reported.tag !== `v${later}`) fail(`reported ${JSON.stringify(reported)}`);
	if (!readFileSync(state, 'utf8').includes(later)) fail('what it learned was not kept');
	first.kill();
	await exited(first);
	reported = null;
	config = await configWith(list);
	const before = lines().length;
	const second = start();
	await until('the older release is refused', 180_000, () => reported !== null);
	if (reported.reason !== 'below_floor' || reported.tag !== tag) fail(`reported ${JSON.stringify(reported)}`);
	if (lines().slice(before).some((l) => l.includes('Ready'))) fail('staged an older release');
	second.kill();
	await exited(second);
	await unchanged();
} else if (kase === 'working') {
	await staged();
	if (!lines().some((l) => l === `Available Ready ${a.to} busy`)) fail('the encode is not seen as work');
	if (person(first, 'press') !== "'Restart now' cannot be pressed") fail('Restart now was offered');
	// The app closes with the work unfinished
	await closes(first);
	await unchanged();
} else if (kase === 'twice') {
	await staged();
	// It asks nothing: only its being open counts. Its path is another one than the first's
	const install = dirname(dirname(a.exe));
	const junction = join(dir, 'junction');
	symlinkSync(install, junction, 'junction');
	const other = start({ AUN_UPDATE_TEST_LOG: join(dir, 'other.txt'), AUN_UPDATE_TEST_STATE: join(dir, 'other.json'), AUN_UPDATE_TEST_CHECK_MS: '3600000' }, join(junction, 'current', basename(a.exe)));
	await until('the first one sees the other', 30_000, () => lines().some((l) => l === `Available Ready ${a.to} busy`));
	await closes(first);
	// Through the updater's time: it ends every process of the folder
	await sleep(15_000);
	if (other.exitCode !== null || other.signalCode !== null) fail('the other window was ended');
	other.kill();
	await exited(other);
	await unchanged();
} else {
	const reason = { tampered: 'hash', 'bad-signature': 'unknown_key', planted: 'unknown_key', 'foreign-host': 'host' }[kase];
	if (!reason) fail('no such case');
	await until('the refusal is reported', 180_000, () => reported !== null);
	await sleep(3_000);
	first.kill();
	await exited(first);
	if (reported.reason !== reason) fail(`reported ${reported.reason}, expected ${reason}`);
	if (reported.app !== a.app || reported.tag !== tag || reported.version !== a.from) fail(`reported ${JSON.stringify(reported)}`);
	if (lines().some(ready)) fail('staged a refused update');
	if (kase === 'foreign-host' && calls.some((c) => c.startsWith('/downloads.example.com'))) fail('asked the foreign host');
	await unchanged();
}
if (!existsSync(state)) fail('the state was not kept');
console.log(`update test (${a.app}, ${kase}) passed:\n${lines().join('\n')}`);
server.close();
process.exit(0);

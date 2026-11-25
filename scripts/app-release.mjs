#!/usr/bin/env node
/**
 * App release manifests and update keys (wiki AUN-APP-UPDATES §3.1, §3.5, §9). Plain Node 22, no
 * dependencies, so every app repo's release workflow can copy this one file and run it.
 *
 *   keygen --out <file> [--root]
 *       Make an ed25519 key. Writes the private key (base64 PKCS#8) to <file> (mode 600) and prints
 *       only the public key and its id. Release keys become the repo secret UPDATE_SIGNING_KEY; a
 *       root key (--root) never leaves the owner's machine.
 *   build --repo <repo> --tag <tag> --channel stable|beta --notes-en <file> --notes-ar <file>
 *         --artifacts <spec.json> [--version <v>] [--stores <stores.json>] [--yanked v1,v2]
 *         [--published-at <iso>] [--private] --out <app-release.json>
 *       Write app-release.json. spec.json: [{ "app", "platform", "arch"?, "kind", "file" }]; each
 *       file is hashed (SHA-256) and listed under this repo's release for the tag (or, with
 *       --private, the private admin bucket). The version is the tag's (prefixes v, admin-v,
 *       admin-windows-v; a trailing -N is the 4th part: v1.2.3-4 → 1.2.3.4); --version must match it.
 *   sign --in <file> [--key-env NAME]... [--require]
 *       Write <file>.sig with one signature per key read from the named environment variables
 *       (default UPDATE_SIGNING_KEY). With no key set: prints a ::notice:: and writes nothing,
 *       unless --require.
 *   verify --in <file> --pub <base64>[,<base64>]
 *       Check <file>.sig against the given public keys. Exit 1 if no signature is valid.
 *   keys-build --repo <repo> --serial <n> --pub <base64>[,...] [--revoked id,...] [--yanked v,...] --out <file>
 *       Write update-keys.json (the owner then signs it with the root key: sign --key-env ROOT_KEY).
 *
 * Exit codes: 0 done, 1 refused (verification failed), 2 bad usage or input.
 */
import { createHash, webcrypto } from 'node:crypto';
import { readFileSync, writeFileSync, chmodSync, existsSync } from 'node:fs';
import { basename } from 'node:path';
import { pathToFileURL } from 'node:url';

const { subtle } = webcrypto;
export const REPOS = ['AlUmmahNowDesktop', 'AlUmmahNowTools', 'AlUmmahNowApple', 'AlUmmahNowAndroid'];
const APPS = ['viewer', 'studio', 'tools', 'admin'];
const PLATFORMS = ['ios', 'ipados', 'tvos', 'macos', 'android', 'androidtv', 'firetv', 'windows', 'linux', 'tizen', 'webos', 'web'];
const KINDS = ['velopack-full', 'velopack-delta', 'sparkle-zip', 'msi', 'msix', 'zip', 'dmg', 'pkg', 'exe', 'deb', 'flatpak', 'tar', 'apk', 'appinstaller'];
const SOURCES = ['appstore', 'testflight', 'play', 'amazon', 'msstore', 'direct', 'managed', 'samsung'];
const ARCHES = ['x64', 'arm64', 'universal'];
const SEMVER = /^(\d{1,6})\.(\d{1,6})\.(\d{1,6})(?:\.(\d{1,6}))?(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$/;
const NOTES_MAX = 1500;

class UsageError extends Error {}

function args(argv) {
	const out = { _: [] };
	for (let i = 0; i < argv.length; i++) {
		const a = argv[i];
		if (!a.startsWith('--')) {
			out._.push(a);
			continue;
		}
		const key = a.slice(2);
		const next = argv[i + 1];
		const value = next === undefined || next.startsWith('--') ? true : (i++, next);
		if (key === 'key-env') (out[key] ??= []).push(value);
		else out[key] = value;
	}
	return out;
}

function need(a, key) {
	if (typeof a[key] !== 'string' || !a[key]) throw new UsageError(`--${key} is required`);
	return a[key];
}

const b64 = (bytes) => Buffer.from(bytes).toString('base64');
const list = (v) => (typeof v === 'string' && v ? v.split(',').map((s) => s.trim()).filter(Boolean) : []);

export async function keyIdOf(publicKeyBase64) {
	const digest = await subtle.digest('SHA-256', Buffer.from(publicKeyBase64, 'base64'));
	return Buffer.from(digest).toString('hex').slice(0, 8);
}

export async function generateKey() {
	const pair = await subtle.generateKey({ name: 'Ed25519' }, true, ['sign', 'verify']);
	const publicKey = b64(await subtle.exportKey('raw', pair.publicKey));
	const privateKey = b64(await subtle.exportKey('pkcs8', pair.privateKey));
	return { publicKey, privateKey, keyId: await keyIdOf(publicKey) };
}

async function publicOf(privateKeyBase64) {
	const priv = await subtle.importKey('pkcs8', Buffer.from(privateKeyBase64, 'base64'), { name: 'Ed25519' }, true, ['sign']);
	const jwk = await subtle.exportKey('jwk', priv);
	return Buffer.from(jwk.x, 'base64url').toString('base64');
}

/** The signature file for these bytes: one signature per private key. */
export async function signBytes(bytes, privateKeys) {
	const signatures = [];
	for (const privateKeyBase64 of privateKeys) {
		const priv = await subtle.importKey('pkcs8', Buffer.from(privateKeyBase64.trim(), 'base64'), { name: 'Ed25519' }, false, ['sign']);
		const sig = await subtle.sign({ name: 'Ed25519' }, priv, bytes);
		signatures.push({ keyId: await keyIdOf(await publicOf(privateKeyBase64.trim())), sig: b64(sig) });
	}
	return JSON.stringify({ signatures });
}

/** The key id of the first valid signature, or null. */
export async function verifyBytes(bytes, sigText, publicKeys) {
	let file;
	try {
		file = JSON.parse(sigText);
	} catch {
		return null;
	}
	if (!Array.isArray(file?.signatures)) return null;
	const byId = new Map();
	for (const k of publicKeys) byId.set(await keyIdOf(k), k);
	for (const s of file.signatures) {
		const k = byId.get(s?.keyId);
		if (!k || typeof s.sig !== 'string') continue;
		try {
			const pub = await subtle.importKey('raw', Buffer.from(k, 'base64'), { name: 'Ed25519' }, false, ['verify']);
			if (await subtle.verify({ name: 'Ed25519' }, pub, Buffer.from(s.sig, 'base64'), bytes)) return s.keyId;
		} catch {
			// not a valid signature
		}
	}
	return null;
}

/** Release tag prefixes, longest first (wiki AUN-APP-UPDATES §2.1; the same rule as shared-types parseAppReleaseTag). */
export const TAG_PREFIXES = ['admin-windows-v', 'admin-v', 'v'];
const TAG = /^(?:admin-windows-v|admin-v|v)[0-9A-Za-z.+-]{1,64}$/;

/**
 * A release tag's prefix and version, or null: v2026.11.22.2 → 2026.11.22.2; v1.2.3-preview.4 → 1.2.3-preview.4;
 * a trailing -N of one or two digits is the 4th part (Apple v1.2.3-4 → 1.2.3.4, admin-v1.2.3-4 → 1.2.3.4).
 */
export function parseReleaseTag(tag) {
	if (typeof tag !== 'string' || !TAG.test(tag)) return null;
	const prefix = TAG_PREFIXES.find((p) => tag.startsWith(p));
	const bare = tag.slice(prefix.length);
	const rebuild = /^(\d+\.\d+\.\d+)-(\d{1,2})$/.exec(bare);
	const version = rebuild ? `${rebuild[1]}.${rebuild[2]}` : bare;
	if (!SEMVER.test(version)) return null;
	return { prefix, version, adminOnly: prefix !== 'v' };
}

/** The version a tag releases, or null. */
export function versionFromTag(tag) {
	return parseReleaseTag(tag)?.version ?? null;
}

function readNotes(file, lang) {
	if (!existsSync(file)) throw new UsageError(`notes (${lang}) not found: ${file}`);
	const text = readFileSync(file, 'utf8').trim();
	if (!text) throw new UsageError(`notes (${lang}) are empty: ${file}`);
	if (text.length > NOTES_MAX) throw new UsageError(`notes (${lang}) are ${text.length} characters; at most ${NOTES_MAX}`);
	return text;
}

export function buildManifest(o) {
	if (!REPOS.includes(o.repo)) throw new UsageError(`unknown repo ${o.repo}`);
	const tag = parseReleaseTag(o.tag);
	if (!tag) throw new UsageError(`bad tag ${o.tag} (v…, admin-v… or admin-windows-v…)`);
	// The version is the tag's: the server refuses a manifest whose version differs from its tag
	const version = tag.version;
	if (o.version !== undefined && o.version !== version) throw new UsageError(`--version ${o.version} is not the tag's version ${version}`);
	if (!['stable', 'beta'].includes(o.channel)) throw new UsageError('--channel must be stable or beta');
	const prefix = o.private ? `r2://app-releases/${o.repo}/${o.tag}/` : `https://github.com/Al-Ummah-Now/${o.repo}/releases/download/${o.tag}/`;
	const artifacts = o.artifacts.map((a, i) => {
		if (!APPS.includes(a.app) || !PLATFORMS.includes(a.platform) || !KINDS.includes(a.kind)) throw new UsageError(`artifact ${i}: bad app, platform or kind`);
		if (a.arch !== undefined && !ARCHES.includes(a.arch)) throw new UsageError(`artifact ${i}: bad arch`);
		if (o.private && a.app !== 'admin') throw new UsageError(`artifact ${i}: only admin builds go to the private bucket`);
		if (tag.adminOnly && a.app !== 'admin') throw new UsageError(`artifact ${i}: an ${tag.prefix} tag releases only the Admin app`);
		const bytes = readFileSync(a.file);
		if (!bytes.byteLength) throw new UsageError(`artifact ${i}: empty file ${a.file}`);
		return {
			app: a.app,
			platform: a.platform,
			...(a.arch ? { arch: a.arch } : {}),
			kind: a.kind,
			url: prefix + encodeURIComponent(basename(a.file)),
			sha256: createHash('sha256').update(bytes).digest('hex'),
			size: bytes.byteLength,
		};
	});
	const stores = (o.stores ?? []).map((s, i) => {
		if (!APPS.includes(s.app) || !PLATFORMS.includes(s.platform) || !SOURCES.includes(s.source)) throw new UsageError(`store ${i}: bad app, platform or source`);
		if (tag.adminOnly && s.app !== 'admin') throw new UsageError(`store ${i}: an ${tag.prefix} tag releases only the Admin app`);
		return { app: s.app, platform: s.platform, source: s.source };
	});
	for (const v of o.yanked ?? []) if (!SEMVER.test(v)) throw new UsageError(`bad yanked version ${v}`);
	if (!artifacts.length && !stores.length) throw new UsageError('a release needs at least one artifact or store upload');
	return {
		schema: 1,
		repo: o.repo,
		tag: o.tag,
		version,
		channel: o.channel,
		publishedAt: o.publishedAt ?? new Date().toISOString().replace(/\.\d{3}Z$/, 'Z'),
		notes: { en: o.notesEn, ar: o.notesAr },
		artifacts,
		stores,
		yanked: o.yanked ?? [],
	};
}

export async function buildKeyList(o) {
	if (!REPOS.includes(o.repo)) throw new UsageError(`unknown repo ${o.repo}`);
	const serial = Number(o.serial);
	if (!Number.isInteger(serial) || serial < 1) throw new UsageError('--serial must be a whole number from 1');
	if (!o.keys.length || o.keys.length > 4) throw new UsageError('1 to 4 public keys');
	const keys = [];
	for (const k of o.keys) {
		if (!/^[A-Za-z0-9+/]{43}=$/.test(k)) throw new UsageError(`bad public key ${k}`);
		keys.push({ keyId: await keyIdOf(k), publicKey: k });
	}
	for (const id of o.revoked ?? []) if (!/^[0-9a-f]{8}$/.test(id)) throw new UsageError(`bad key id ${id}`);
	for (const v of o.yanked ?? []) if (!SEMVER.test(v)) throw new UsageError(`bad yanked version ${v}`);
	return { schema: 1, repo: o.repo, serial, keys, revoked: o.revoked ?? [], yanked: o.yanked ?? [] };
}

async function main(argv) {
	const a = args(argv);
	const cmd = a._[0];
	switch (cmd) {
		case 'keygen': {
			const out = need(a, 'out');
			if (existsSync(out)) throw new UsageError(`${out} exists; refusing to overwrite a key`);
			const k = await generateKey();
			writeFileSync(out, k.privateKey + '\n', { mode: 0o600 });
			chmodSync(out, 0o600);
			process.stdout.write(`${a.root ? 'root' : 'release'} key ${k.keyId}\npublic key: ${k.publicKey}\nprivate key written to ${out} (keep it secret${a.root ? ' and offline' : '; it becomes the UPDATE_SIGNING_KEY secret'})\n`);
			return 0;
		}
		case 'build': {
			const spec = JSON.parse(readFileSync(need(a, 'artifacts'), 'utf8'));
			const manifest = buildManifest({
				repo: need(a, 'repo'),
				tag: need(a, 'tag'),
				version: typeof a.version === 'string' ? a.version : undefined,
				channel: need(a, 'channel'),
				notesEn: readNotes(need(a, 'notes-en'), 'en'),
				notesAr: readNotes(need(a, 'notes-ar'), 'ar'),
				artifacts: Array.isArray(spec) ? spec : [],
				stores: typeof a.stores === 'string' ? JSON.parse(readFileSync(a.stores, 'utf8')) : [],
				yanked: list(a.yanked),
				publishedAt: typeof a['published-at'] === 'string' ? a['published-at'] : undefined,
				private: a.private === true,
			});
			writeFileSync(need(a, 'out'), JSON.stringify(manifest, null, 2) + '\n');
			process.stdout.write(`wrote ${a.out}: ${manifest.repo} ${manifest.version} (${manifest.channel}), ${manifest.artifacts.length} files, ${manifest.stores.length} store uploads\n`);
			return 0;
		}
		case 'sign': {
			const file = need(a, 'in');
			const names = a['key-env'] ?? ['UPDATE_SIGNING_KEY'];
			const keys = names.map((n) => process.env[n]).filter((v) => typeof v === 'string' && v.trim());
			if (!keys.length) {
				if (a.require) throw new UsageError(`no signing key in ${names.join(', ')}`);
				process.stdout.write(`::notice::${basename(file)} not signed: ${names.join(', ')} not set (apps offer this release to no downloaded build until the repo's keys exist)\n`);
				return 0;
			}
			writeFileSync(`${file}.sig`, (await signBytes(readFileSync(file), keys)) + '\n');
			process.stdout.write(`signed ${basename(file)} with ${keys.length} key(s)\n`);
			return 0;
		}
		case 'verify': {
			const file = need(a, 'in');
			const id = await verifyBytes(readFileSync(file), readFileSync(`${file}.sig`, 'utf8'), list(need(a, 'pub')));
			process.stdout.write(id ? `valid (key ${id})\n` : 'INVALID\n');
			return id ? 0 : 1;
		}
		case 'keys-build': {
			const listDoc = await buildKeyList({ repo: need(a, 'repo'), serial: need(a, 'serial'), keys: list(need(a, 'pub')), revoked: list(a.revoked), yanked: list(a.yanked) });
			writeFileSync(need(a, 'out'), JSON.stringify(listDoc, null, 2) + '\n');
			process.stdout.write(`wrote ${a.out}: serial ${listDoc.serial}, ${listDoc.keys.length} key(s), ${listDoc.revoked.length} revoked\n`);
			return 0;
		}
		default:
			throw new UsageError('usage: app-release.mjs keygen|build|sign|verify|keys-build (see the header)');
	}
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
	main(process.argv.slice(2)).then(
		(code) => process.exit(code),
		(error) => {
			process.stderr.write(`${error instanceof UsageError ? 'error' : 'failed'}: ${error.message}\n`);
			process.exit(2);
		},
	);
}

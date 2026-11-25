#!/usr/bin/env node
/**
 * What each release file is, for app-release.json: release-files.mjs <dist> <version> prints the spec that
 * `app-release.mjs build --artifacts` reads. A file it doesn't know, or a missing one, fails the release.
 */
import { readdirSync } from 'node:fs';
import { join } from 'node:path';

const [dist, version] = process.argv.slice(2);
const v = version.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
const rule = (pattern, app, platform, kind, optional = false) => ({ pattern: new RegExp(`^${pattern.replace('V', () => v)}$`), app, platform, kind, optional });
const windows = (app, name) => [
	rule(`${name}-V-x64-update\\.nupkg`, app, 'windows', 'velopack-full'),
	rule(`${name}-V-x64-Setup(-unsigned)?\\.exe`, app, 'windows', 'exe'),
	rule(`${name}-V-x64(-unsigned)?\\.msi`, app, 'windows', 'msi'),
	rule(`${name}-V-x64-portable(-unsigned)?\\.zip`, app, 'windows', 'zip'),
	// Only with a certificate (docs/RELEASE.md)
	rule(`${name}-V-x64\\.msix`, app, 'windows', 'msix', true),
];
const linux = (app, name, deb) => [rule(`${deb}_V_amd64\\.deb`, app, 'linux', 'deb'), rule(`${name}-V-x86_64\\.flatpak`, app, 'linux', 'flatpak')];
const RULES = [
	...windows('viewer', 'AlUmmahNow-Windows'),
	...windows('studio', 'AlUmmahNow-Studio-Windows'),
	...linux('viewer', 'AlUmmahNow-Linux', 'alummahnow'),
	...linux('studio', 'AlUmmahNow-Studio-Linux', 'alummahnow-studio'),
	// The TV's packages are no update of an app here: Samsung's store has them
	rule('AlUmmahNow-Tizen-V-unsigned\\.tpk', null),
	rule('AlUmmahNow-Tizen-V\\.tpk', null, null, null, true),
];
const files = readdirSync(dist).sort();
const out = [];
for (const { pattern, app, platform, kind, optional } of RULES) {
	const hits = files.filter((f) => pattern.test(f));
	if (hits.length !== 1 && !(optional && hits.length === 0)) throw new Error(`${app} ${platform} ${kind}: ${hits.length} files match ${pattern}`);
	if (app) for (const hit of hits) out.push({ app, platform, arch: 'x64', kind, file: join(dist, hit) });
}
const unknown = files.filter((f) => !RULES.some((r) => r.pattern.test(f)));
if (unknown.length) throw new Error(`not a known release file: ${unknown.join(', ')}`);
process.stdout.write(JSON.stringify(out, null, 2) + '\n');

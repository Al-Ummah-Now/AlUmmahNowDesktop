#!/usr/bin/env node
/**
 * no-test-hooks.mjs <dir> [--expect]: fails when a built app carries the update test's hooks (UPDATE_TEST: a root key
 * and a server taken from the environment). `--expect` is the opposite, so the update test proves this check can see them.
 */
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

const [dir, flag] = process.argv.slice(2);
const needle = Buffer.from('AUN_UPDATE_TEST_', 'utf16le'); // .NET keeps string literals as UTF-16
const dlls = readdirSync(dir, { recursive: true }).map(String).filter((f) => /(^|[\\/])AlUmmahNow[^\\/]*\.dll$/.test(f));
if (!dlls.some((f) => f.endsWith('AlUmmahNow.CoreBridge.Native.dll'))) throw new Error(`no AlUmmahNow.CoreBridge.Native.dll under ${dir}`);
const hooked = dlls.filter((f) => readFileSync(join(dir, f)).includes(needle));
// Both the app and the bridge have hooks of their own: a check that sees only one would miss a release built with the other
if (flag === '--expect' ? hooked.length < 2 : hooked.length) {
	console.error(`::error::${flag === '--expect' ? `test hooks not found where they should be (${hooked.join(', ')})` : `update test hooks in ${hooked.join(', ')}`}`);
	process.exit(1);
}
console.log(flag === '--expect' ? `test build: ${hooked.join(', ')}` : `no test hooks in ${dlls.length} assemblies`);

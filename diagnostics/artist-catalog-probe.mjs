import {spawn} from 'node:child_process';
import {mkdtemp, rm} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {join, resolve} from 'node:path';
import {fileURLToPath} from 'node:url';

const root = resolve(fileURLToPath(new URL('..', import.meta.url)));
const dataDirectory = await mkdtemp(join(tmpdir(), 'InnerTuneArtistCatalog-'));

function artist(argument) {
  return new Promise((resolveResult, reject) => {
    const child = spawn(process.execPath, [join(root, 'provider', 'provider.mjs'), 'artist', JSON.stringify(argument)], {
      env: {...process.env, ITMUSIC_DATA_DIR: dataDirectory},
      stdio: ['ignore', 'pipe', 'pipe']
    });
    let output = '';
    let errors = '';
    child.stdout.on('data', chunk => output += chunk);
    child.stderr.on('data', chunk => errors += chunk);
    child.on('close', code => code === 0
      ? resolveResult(JSON.parse(output))
      : reject(new Error(errors.trim() || `Artist provider exited with ${code}.`)));
  });
}

try {
  const catalog = await artist({name: 'Daft Punk'});
  const unique = new Set(catalog.tracks.map(track => track.id));
  if (!catalog.id.startsWith('UC')) throw new Error('A legacy artist name did not resolve to an artist ID.');
  if (catalog.tracks.length <= 100) throw new Error('The artist continuation page was not loaded.');
  if (unique.size !== catalog.tracks.length) throw new Error('The complete artist catalog contains duplicates.');
  const recovered = await artist({name: 'Daft Punk', id: 'UCinvalid'});
  if (recovered.id !== catalog.id || recovered.tracks.length !== catalog.tracks.length)
    throw new Error('A stale artist ID did not recover through name resolution.');
  console.log(JSON.stringify({
    passed: true,
    legacyArtistResolved: true,
    staleArtistIdRecovered: true,
    continuationLoaded: true,
    artistId: catalog.id,
    trackCount: catalog.tracks.length
  }));
}
finally {
  await rm(dataDirectory, {recursive: true, force: true});
}

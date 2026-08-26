import {mkdtemp, readFile, rm, writeFile} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {join, resolve} from 'node:path';
import {fileURLToPath} from 'node:url';
import {Client} from '../provider/node_modules/@modelcontextprotocol/sdk/dist/esm/client/index.js';
import {StdioClientTransport} from '../provider/node_modules/@modelcontextprotocol/sdk/dist/esm/client/stdio.js';

const root = resolve(fileURLToPath(new URL('..', import.meta.url)));
const dataDirectory = await mkdtemp(join(tmpdir(), 'InnerTuneMcpQueue-'));
const song = id => ({id, title: id, artist: 'Test artist', durationSeconds: 180, durationText: '3:00'});
const current = song('current');
const added = song('added');
const next = song('next');
const state = async () => JSON.parse(await readFile(join(dataDirectory, 'library.json'), 'utf8'));
const ids = data => data.queue.map(track => track.id).join(',');
const expect = (actual, wanted, message) => { if (actual !== wanted) throw new Error(`${message}: expected ${wanted}, got ${actual}`); };
const toolJson = result => JSON.parse(result.content.find(item => item.type === 'text')?.text || '{}');

await writeFile(join(dataDirectory, 'library.json'), JSON.stringify({
  version: 1,
  volume: 0,
  queueSourceId: null,
  queueSourceName: 'Playing now',
  playback: {status: 'paused', track: current, trackId: current.id, queueIndex: 0, queueId: null, queueName: 'Playing now', positionSeconds: 10},
  queue: [current],
  folders: [],
  favorites: [{track: added, folderId: null}, {track: next, folderId: null}],
  savedQueues: [
    {id: '11111111-1111-4111-8111-111111111111', name: 'One', folderId: null, tracks: [current, added]},
    {id: '22222222-2222-4222-8222-222222222222', name: 'Two', folderId: null, tracks: [added, next]}
  ],
  recentlyPlayed: [],
  pendingCommands: [],
  settings: {}
}, null, 2));

const client = new Client({name: 'innertune-queue-probe', version: '1.0.0'});
const transport = new StdioClientTransport({
  command: process.execPath,
  args: [join(root, 'provider', 'mcp-server.mjs')],
  env: {...process.env, ITMUSIC_DATA_DIR: dataDirectory}
});

try {
  await client.connect(transport);
  await client.callTool({name: 'add_to_queue', arguments: {videoIds: [added.id]}});
  expect(ids(await state()), 'current,added', 'Standalone Add did not form an ad-hoc queue');

  await client.callTool({name: 'play_next', arguments: {videoIds: [next.id]}});
  let data = await state();
  expect(ids(data), 'current,next,added', 'Play next did not insert after the current song');
  expect(data.pendingCommands.at(-1)?.type, 'prioritize_next', 'Play next did not reach the live player');

  await client.callTool({name: 'play_song', arguments: {videoId: added.id}});
  data = await state();
  expect(ids(data), 'added', 'Standalone Play did not replace the transient queue');
  expect(data.queueSourceName, 'Playing now', 'Standalone Play used the wrong source label');
  expect(data.pendingCommands.at(-1)?.type, 'play', 'Standalone Play did not reach the live player');

  await client.callTool({name: 'shuffle_all_saved_queues', arguments: {}});
  data = await state();
  if (data.queue.length !== 3 || new Set(data.queue.map(track => track.id)).size !== 3)
    throw new Error('Shuffle all did not combine and deduplicate saved queues.');
  expect(data.queueSourceName, 'All queues · shuffled', 'Shuffle all used the wrong source label');
  expect(data.pendingCommands.at(-1)?.type, 'play', 'Shuffle all did not reach the live player');

  const musicState = toolJson(await client.callTool({name: 'get_music_state', arguments: {}}));
  const firstSaved = musicState.savedQueues.find(queue => queue.path === 'One');
  expect(firstSaved?.shortId, '11111111', 'Saved queue did not expose a stable short ID');

  await client.callTool({name: 'replace_queue', arguments: {videoIds: [next.id]}});
  await client.callTool({name: 'update_saved_queue', arguments: {shortId: '#11111111'}});
  data = await state();
  expect(data.savedQueues[0].tracks.map(track => track.id).join(','), 'next', 'Short-ID replace did not use the current queue');

  await client.callTool({name: 'update_saved_queue', arguments: {shortId: '11111111', mode: 'append', videoIds: [added.id, next.id]}});
  data = await state();
  expect(data.savedQueues[0].tracks.map(track => track.id).join(','), 'next,added', 'Short-ID append did not deduplicate songs');

  await client.callTool({name: 'update_saved_queue', arguments: {shortId: '11111111', mode: 'remove', videoIds: [next.id]}});
  data = await state();
  expect(data.savedQueues[0].tracks.map(track => track.id).join(','), 'added', 'Short-ID remove did not update the saved queue');
  expect(data.savedQueues.length, 2, 'Short-ID updates created a duplicate saved queue');
  expect(data.savedQueues[0].id, '11111111-1111-4111-8111-111111111111', 'Short-ID updates changed playlist identity');
  expect(ids(data), 'next', 'Updating a saved queue unexpectedly changed the current queue');

  console.log(JSON.stringify({passed: true, adHocQueue: true, playNext: true, standalonePlay: true, shuffleAllSavedQueues: true, savedQueueShortIds: true, updateSavedQueue: true}));
}
finally {
  await client.close().catch(() => {});
  await rm(dataDirectory, {recursive: true, force: true});
}
